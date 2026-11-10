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
