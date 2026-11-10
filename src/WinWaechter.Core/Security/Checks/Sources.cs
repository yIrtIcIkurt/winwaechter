namespace WinWaechter.Core.Security.Checks;

public enum FirewallProfile { Domain = 1, Private = 2, Public = 4 }

public interface IFirewall
{
    bool IsEnabled(FirewallProfile p);
    void SetEnabled(FirewallProfile p, bool on);
}

public sealed record SecurityProduct(string Name, bool Enabled, bool IsMicrosoft);

public interface ISecurityCenter
{
    IReadOnlyList<SecurityProduct> AntiVirus();
    IReadOnlyList<SecurityProduct> Firewalls();
}

public sealed record VolumeEncryptionState(int ProtectionStatus, int ConversionStatus);

public interface IVolumeEncryption
{
    VolumeEncryptionState? SystemDrive();   // null = class/volume not available
}
