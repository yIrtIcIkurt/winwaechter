using WinWaechter.Core.Performance;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeBroadcast : ISettingBroadcast
{
    public int Count { get; private set; }
    public void ImmersiveColorSetChanged() => Count++;
}

public sealed class FakePowerModes : IPowerModes
{
    readonly Dictionary<PowerSource, Guid?> _modes = new()
    {
        [PowerSource.Ac] = PowerModeIds.Balanced,
        [PowerSource.Dc] = PowerModeIds.Balanced,
    };

    public bool HasBattery { get; set; } = true;
    public PowerSource Current { get; set; } = PowerSource.Ac;
    public List<(PowerSource Source, Guid Mode)> SetCalls { get; } = [];

    public Guid? Get(PowerSource s) => _modes[s];
    public void Set(PowerSource s, Guid mode) { _modes[s] = mode; SetCalls.Add((s, mode)); }

    /// <summary>Pre-fills a mode without recording a call; null simulates an unreadable value.</summary>
    public FakePowerModes Seed(PowerSource s, Guid? mode) { _modes[s] = mode; return this; }
}

public sealed class FakeAnimationSetting : IAnimationSetting
{
    public bool Value { get; set; } = true;
    public List<bool> SetCalls { get; } = [];
    public bool Get() => Value;
    public void Set(bool on) { Value = on; SetCalls.Add(on); }
}

public sealed class FakeHibernation : IHibernation
{
    public bool IsEnabled { get; set; } = true;
    public long FileBytes { get; set; } = 13L * 1024 * 1024 * 1024;
    public List<bool> SetCalls { get; } = [];
    public void Set(bool on) { IsEnabled = on; SetCalls.Add(on); }
}

public sealed class FakeAppDisplayNames : IAppDisplayNames
{
    public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Resolve(string packageFamilyName) =>
        Names.TryGetValue(packageFamilyName, out var n) ? n : packageFamilyName.Split('_')[0];
}

public sealed class FakeSystemSources : ISystemSources
{
    public CpuTimes? CpuValue { get; set; } = new(0, 0, 0);
    public (long Used, long Total)? MemoryValue { get; set; } = (8L << 30, 16L << 30);
    public double? DiskValue { get; set; } = 10;
    public List<ProcTime> Procs { get; set; } = [];
    public int Cores { get; set; } = 1;
    public bool ThrowCpu { get; set; }
    public bool ThrowDisk { get; set; }
    public int CpuCalls { get; private set; }

    public CpuTimes? Cpu()
    {
        CpuCalls++;
        return ThrowCpu ? throw new InvalidOperationException("cpu boom") : CpuValue;
    }

    public (long Used, long Total)? Memory() => MemoryValue;
    public double? DiskBusy() => ThrowDisk ? throw new InvalidOperationException("disk boom") : DiskValue;
    public IReadOnlyList<ProcTime> Processes() => Procs;
}

public sealed class FakeBootEventSource : IBootEventSource
{
    public List<BootEvent> Events { get; } = [];
    public Exception? Throws { get; set; }
    public DateTime? LastSince { get; private set; }

    public IReadOnlyList<BootEvent> Read(DateTime since)
    {
        LastSince = since;
        if (Throws is not null) throw Throws;
        return Events;
    }
}

public sealed class FakeTicks : WinWaechter.App.ViewModels.ITickSource
{
    Action? _tick;
    public bool Running { get; private set; }
    public TimeSpan? Every { get; private set; }

    public void Start(TimeSpan every, Action tick) { Running = true; Every = every; _tick = tick; }
    public void Stop() => Running = false;

    /// <summary>Fires one tick like the timer would; does nothing while stopped.</summary>
    public void Fire() { if (Running) _tick?.Invoke(); }
}
