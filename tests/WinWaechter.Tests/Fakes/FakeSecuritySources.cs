using WinWaechter.Core.Security.Checks;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeSecurityCenter : ISecurityCenter
{
    public List<SecurityProduct> Av { get; } = [];
    public List<SecurityProduct> Fw { get; } = [];
    public bool Throw { get; set; }

    public IReadOnlyList<SecurityProduct> AntiVirus() => Throw ? throw new InvalidOperationException("SecurityCenter fehlt") : Av;
    public IReadOnlyList<SecurityProduct> Firewalls() => Throw ? throw new InvalidOperationException("SecurityCenter fehlt") : Fw;
}

public sealed class FakeVolumeEncryption : IVolumeEncryption
{
    public VolumeEncryptionState? State { get; set; }
    public bool Throw { get; set; }

    public VolumeEncryptionState? SystemDrive() => Throw ? throw new InvalidOperationException("WMI blockiert") : State;
}
