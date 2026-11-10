using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WinWaechter.Core.Security.Checks;

// Effective firewall state per profile through the COM policy object.
public sealed class FwPolicy2Firewall : IFirewall
{
    static object Policy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)
        ?? throw new InvalidOperationException("Firewall-Richtlinie nicht verfügbar.");

    public bool IsEnabled(FirewallProfile p)
    {
        var pol = Policy();
        try { return Convert.ToBoolean(pol.GetType().InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, pol, [(int)p])); }
        finally { Marshal.ReleaseComObject(pol); }
    }

    public void SetEnabled(FirewallProfile p, bool on)
    {
        var pol = Policy();
        try { pol.GetType().InvokeMember("FirewallEnabled", BindingFlags.SetProperty, null, pol, [(int)p, on]); }
        finally { Marshal.ReleaseComObject(pol); }
    }
}

public sealed class WmiSecurityCenter : ISecurityCenter
{
    static readonly string[] MicrosoftPrefixes =
        ["Windows Defender", "Microsoft Defender", "Windows Firewall", "Windows Defender Firewall", "Microsoft Defender Firewall"];

    public static bool IsMicrosoftName(string name) =>
        MicrosoftPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<SecurityProduct> AntiVirus() => Read("AntiVirusProduct");
    public IReadOnlyList<SecurityProduct> Firewalls() => Read("FirewallProduct");

    static List<SecurityProduct> Read(string cls)
    {
        var list = new List<SecurityProduct>();
        using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", $"SELECT displayName, productState FROM {cls}",
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
        foreach (var o in s.Get().Cast<ManagementBaseObject>())
        {
            var name = o["displayName"] as string ?? "";
            var state = Convert.ToUInt32(o["productState"]);
            list.Add(new SecurityProduct(name, ((state >> 12) & 0xF) == 1, IsMicrosoftName(name)));
        }
        return list;
    }
}

public sealed class WmiVolumeEncryption : IVolumeEncryption
{
    public VolumeEncryptionState? SystemDrive()
    {
        try
        {
            var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!.TrimEnd('\\');
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'",
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
            using var vol = s.Get().Cast<ManagementObject>().FirstOrDefault();
            if (vol is null) return null;
            var protection = Convert.ToInt32(vol["ProtectionStatus"]);
            var conversion = 0;
            using (var r = vol.InvokeMethod("GetConversionStatus", null, null))
                if (r is not null && Convert.ToUInt32(r["ReturnValue"]) == 0) conversion = Convert.ToInt32(r["ConversionStatus"]);
            return new VolumeEncryptionState(protection, conversion);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException) { return null; }
    }
}
