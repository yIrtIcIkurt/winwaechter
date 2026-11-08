using System.Diagnostics;
using System.Management;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Security.Heuristics;

public sealed record ProcessSample(int Pid, string Name, string? ExePath, string? CommandLine,
    double CpuPercentAvg, bool SustainedHigh);

public interface IProcessSource
{
    Task<IReadOnlyList<ProcessSample>> SampleAsync(TimeSpan window, CancellationToken ct);
}

/// <summary>
/// Reads processes via WMI and measures CPU time at three points (start, middle, end of the window).
/// SustainedHigh means both halves used at least 25 % of the total CPU. GPU load is not measured.
/// </summary>
public sealed class WmiProcessSource : IProcessSource
{
    public const double HighCpuPercent = 25;

    public async Task<IReadOnlyList<ProcessSample>> SampleAsync(TimeSpan window, CancellationToken ct)
    {
        var infos = await Task.Run(ReadInfos, ct);
        var t0 = Stopwatch.GetTimestamp();
        var cpu0 = ReadCpu(infos.Keys);
        var half = TimeSpan.FromTicks(window.Ticks / 2);
        await Task.Delay(half, ct);
        var t1 = Stopwatch.GetTimestamp();
        var cpu1 = ReadCpu(cpu0.Keys);
        await Task.Delay(half, ct);
        var t2 = Stopwatch.GetTimestamp();
        var cpu2 = ReadCpu(cpu1.Keys);

        var cores = Environment.ProcessorCount;
        var result = new List<ProcessSample>();
        foreach (var (pid, info) in infos)
        {
            if (!cpu0.TryGetValue(pid, out var a) || !cpu1.TryGetValue(pid, out var b) || !cpu2.TryGetValue(pid, out var c))
                continue;                                  // access denied or process ended
            var first = Percent(b - a, Stopwatch.GetElapsedTime(t0, t1), cores);
            var second = Percent(c - b, Stopwatch.GetElapsedTime(t1, t2), cores);
            var avg = Percent(c - a, Stopwatch.GetElapsedTime(t0, t2), cores);
            result.Add(new ProcessSample(pid, info.Name, info.ExePath, info.CommandLine, avg,
                first >= HighCpuPercent && second >= HighCpuPercent));
        }
        return result;
    }

    internal static double Percent(TimeSpan cpuDelta, TimeSpan elapsed, int cores) =>
        elapsed <= TimeSpan.Zero || cores <= 0 ? 0 : cpuDelta.TotalSeconds / (elapsed.TotalSeconds * cores) * 100;

    sealed record Info(string Name, string? ExePath, string? CommandLine);

    static Dictionary<int, Info> ReadInfos()
    {
        var map = new Dictionary<int, Info>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    var pid = Convert.ToInt32(o["ProcessId"]);
                    if (pid <= 4) continue;                // Idle and System
                    map[pid] = new Info(o["Name"] as string ?? "", o["ExecutablePath"] as string, o["CommandLine"] as string);
                }
            }
        }
        catch (Exception ex) { FileLog.Warn("Prozessliste (WMI) nicht lesbar", ex); }
        return map;
    }

    static Dictionary<int, TimeSpan> ReadCpu(IEnumerable<int> pids)
    {
        var map = new Dictionary<int, TimeSpan>();
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                map[pid] = p.TotalProcessorTime;
            }
            catch { /* access denied or gone */ }
        }
        return map;
    }
}
