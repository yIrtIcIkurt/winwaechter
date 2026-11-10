using System.Diagnostics;
using System.Runtime.InteropServices;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys.Native;

namespace WinWaechter.Core.Performance;

public sealed record Consumer(string Name, int Count, double? CpuPercent, long MemoryBytes);

public sealed record SystemSample(
    double? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes, double? DiskPercent, IReadOnlyList<Consumer> Top);

public sealed record CpuTimes(long Idle, long Kernel, long User);

public sealed record ProcTime(int Pid, string Name, TimeSpan? Cpu, long WorkingSet);

public static class CpuMath
{
    /// <summary>Kernel time includes idle time, so busy = (kernel + user - idle) / (kernel + user).</summary>
    public static double? Busy(CpuTimes prev, CpuTimes now)
    {
        var total = (now.Kernel - prev.Kernel) + (now.User - prev.User);
        if (total <= 0) return null;
        var busy = total - (now.Idle - prev.Idle);
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }
}

public static class TopConsumers
{
    public static IReadOnlyList<Consumer> Compute(
        IReadOnlyList<ProcTime> prev, IReadOnlyList<ProcTime> now, TimeSpan interval, int cores, int take = 5)
    {
        // keyed by PID and name: a PID reused by another program gives no (bogus) difference
        var before = new Dictionary<(int, string), ProcTime>();
        foreach (var p in prev) before[(p.Pid, p.Name)] = p;
        var capacity = interval.TotalSeconds * Math.Max(cores, 1);

        var perProc = new List<(string Name, double? Cpu, long Mem)>();
        foreach (var p in now)
        {
            if (p.Pid == 0 || p.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase)) continue;
            double? cpu = null;
            if (capacity > 0 && p.Cpu is { } c && before.TryGetValue((p.Pid, p.Name), out var o) && o.Cpu is { } oc)
                cpu = Math.Clamp((c - oc).TotalSeconds / capacity * 100.0, 0, 100);
            perProc.Add((p.Name, cpu, p.WorkingSet));
        }

        return perProc
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Consumer(
                g.First().Name,
                g.Count(),
                g.Any(x => x.Cpu is not null) ? g.Sum(x => x.Cpu ?? 0) : null,
                g.Sum(x => x.Mem)))
            .OrderByDescending(c => c.CpuPercent ?? -1)
            .ThenByDescending(c => c.MemoryBytes)
            .Take(take)
            .ToList();
    }
}

public interface ISystemSources
{
    CpuTimes? Cpu();
    (long Used, long Total)? Memory();
    double? DiskBusy();
    IReadOnlyList<ProcTime> Processes();
    int Cores { get; }
}

/// <summary>Real Windows sources. Every method catches and returns null/empty; the PDH query stays open until Dispose.</summary>
public sealed class WinSystemSources : ISystemSources, IDisposable
{
    readonly object _pdhLock = new();
    IntPtr _query, _counter;
    bool _pdhTried, _pdhHasSample;

    public int Cores => Math.Max(Environment.ProcessorCount, 1);

    public CpuTimes? Cpu()
    {
        try
        {
            return PerfNative.GetSystemTimes(out var idle, out var kernel, out var user)
                ? new CpuTimes(idle.Ticks, kernel.Ticks, user.Ticks)
                : null;
        }
        catch { return null; }
    }

    public (long Used, long Total)? Memory()
    {
        try
        {
            var m = new PerfNative.MemoryStatusEx { Length = (uint)Marshal.SizeOf<PerfNative.MemoryStatusEx>() };
            if (!PerfNative.GlobalMemoryStatusEx(ref m)) return null;
            return ((long)(m.TotalPhys - m.AvailPhys), (long)m.TotalPhys);
        }
        catch { return null; }
    }

    public double? DiskBusy()
    {
        try
        {
            lock (_pdhLock)
            {
                if (!_pdhTried)
                {
                    _pdhTried = true;
                    if (PerfNative.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0
                        || PerfNative.PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _counter) != 0)
                    {
                        CloseQuery();
                        return null;
                    }
                }
                if (_query == IntPtr.Zero) return null;
                if (PerfNative.PdhCollectQueryData(_query) != 0) return null;
                if (!_pdhHasSample) { _pdhHasSample = true; return null; }
                if (PerfNative.PdhGetFormattedCounterValue(_counter, PerfNative.PdhFmtDouble, IntPtr.Zero, out var v) != 0) return null;
                if (v.CStatus is not (0 or 1)) return null;
                return Math.Clamp(100 - v.DoubleValue, 0, 100);
            }
        }
        catch { return null; }
    }

    public IReadOnlyList<ProcTime> Processes()
    {
        try
        {
            var list = new List<ProcTime>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        TimeSpan? cpu = null;
                        try { cpu = p.TotalProcessorTime; } catch { }
                        list.Add(new ProcTime(p.Id, p.ProcessName, cpu, p.WorkingSet64));
                    }
                    catch { /* exited meanwhile */ }
                }
            }
            return list;
        }
        catch { return []; }
    }

    void CloseQuery()
    {
        if (_query != IntPtr.Zero) PerfNative.PdhCloseQuery(_query);
        _query = _counter = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_pdhLock) { try { CloseQuery(); } catch { } }
    }
}

/// <summary>Takes one reading per call; a failing source yields null for that readout and is logged once.</summary>
public sealed class SystemSampler(ISystemSources src, Action<string, Exception?>? warn = null)
{
    readonly Action<string, Exception?> _warn = warn ?? ((m, e) => FileLog.Warn(m, e));
    readonly HashSet<string> _logged = [];
    CpuTimes? _prevCpu;
    IReadOnlyList<ProcTime> _prevProcs = [];
    bool _hasPrev;

    /// <summary>Forgets the previous reading, so the next one does not average over a long pause.</summary>
    public void Reset()
    {
        _prevCpu = null;
        _prevProcs = [];
    }

    public SystemSample Next(TimeSpan sinceLast)
    {
        var cpuNow = Read("CPU", src.Cpu, nullIsFailure: true);
        double? cpu = _prevCpu is not null && cpuNow is not null ? CpuMath.Busy(_prevCpu, cpuNow) : null;
        _prevCpu = cpuNow;

        var mem = Read("Arbeitsspeicher", src.Memory, nullIsFailure: true);
        // The first disk reading is legitimately empty (PDH needs two samples).
        var disk = Read("Datenträger", src.DiskBusy, nullIsFailure: _hasPrev);

        var procs = Read("Prozesse", src.Processes, nullIsFailure: false) ?? [];
        if (procs.Count == 0) LogOnce("Prozesse", "returned nothing", null);
        var top = TopConsumers.Compute(_prevProcs, procs, sinceLast, src.Cores);
        _prevProcs = procs;

        _hasPrev = true;
        return new SystemSample(cpu, mem?.Used, mem?.Total, disk, top);
    }

    T? Read<T>(string name, Func<T> read, bool nullIsFailure)
    {
        try
        {
            var v = read();
            if (v is null && nullIsFailure) LogOnce(name, "returned no value", null);
            return v;
        }
        catch (Exception ex)
        {
            LogOnce(name, "failed", ex);
            return default;
        }
    }

    void LogOnce(string name, string what, Exception? ex)
    {
        if (_logged.Add(name)) _warn($"SystemSampler: source {name} {what}", ex);
    }
}
