using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Security.Heuristics;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeSecurityCenter : ISecurityCenter
{
    public List<SecurityProduct> Av { get; } = [];
    public List<SecurityProduct> Fw { get; } = [];
    public bool Throw { get; set; }
    /// <summary>Each call sleeps this long first (slow WMI).</summary>
    public TimeSpan Block { get; set; }

    public IReadOnlyList<SecurityProduct> AntiVirus() => Answer(Av);
    public IReadOnlyList<SecurityProduct> Firewalls() => Answer(Fw);

    IReadOnlyList<SecurityProduct> Answer(List<SecurityProduct> list)
    {
        if (Block > TimeSpan.Zero) Thread.Sleep(Block);
        return Throw ? throw new InvalidOperationException("SecurityCenter fehlt") : list;
    }
}

public sealed class FakeVolumeEncryption : IVolumeEncryption
{
    public VolumeEncryptionState? State { get; set; }
    public bool Throw { get; set; }

    public VolumeEncryptionState? SystemDrive() => Throw ? throw new InvalidOperationException("WMI blockiert") : State;
}

public sealed class FakeUpdateSearch : IWindowsUpdateSearch
{
    public WindowsUpdateState State { get; set; } = new(0, null, false);
    /// <summary>Search sleeps this long before answering (simulates a hanging COM call).</summary>
    public TimeSpan Block { get; set; }
    public List<TimeSpan> Calls { get; } = [];

    public WindowsUpdateState Search(TimeSpan timeout)
    {
        Calls.Add(timeout);
        if (Block > TimeSpan.Zero) Thread.Sleep(Block);
        return State;
    }
}

public sealed class FakeListeners : IListenerSource
{
    public List<ListeningPort> Ports { get; } = [];
    public bool Throw { get; set; }

    public IReadOnlyList<ListeningPort> Listening() => Throw ? throw new InvalidOperationException("TCP-Tabelle fehlt") : Ports.ToList();
}
