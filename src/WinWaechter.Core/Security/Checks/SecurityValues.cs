using Microsoft.Win32;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

/// <summary>One registry value the Security page may set; Target is the only value ever written.</summary>
public sealed record SecValue(string Id, Hive Hive, string Key, string Name, RegistryValueKind Kind, object Target);

/// <summary>The fixed whitelist behind the Security fixes. Nothing outside this list is ever written.</summary>
public static class SecurityValues
{
    const string Explorer = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
    const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    const string TermServer = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    public static readonly SecValue SmartScreen = new("smartscreen", Hive.LocalMachine, Explorer, "SmartScreenEnabled", RegistryValueKind.String, "Warn");
    public static readonly SecValue UacEnableLua = new("uac-enablelua", Hive.LocalMachine, UacKey, "EnableLUA", RegistryValueKind.DWord, 1);
    public static readonly SecValue UacConsentAdmin = new("uac-consent", Hive.LocalMachine, UacKey, "ConsentPromptBehaviorAdmin", RegistryValueKind.DWord, 5);
    public static readonly SecValue UacSecureDesktop = new("uac-securedesktop", Hive.LocalMachine, UacKey, "PromptOnSecureDesktop", RegistryValueKind.DWord, 1);
    public static readonly SecValue RdpDeny = new("rdp-deny", Hive.LocalMachine, TermServer, "fDenyTSConnections", RegistryValueKind.DWord, 1);

    static readonly SecValue[] All = [SmartScreen, UacEnableLua, UacConsentAdmin, UacSecureDesktop, RdpDeny];

    /// <summary>The value for exactly this (hive, key, name), or null when it is not on the list.</summary>
    public static SecValue? Find(Hive hive, string key, string name) =>
        All.FirstOrDefault(v => v.Hive == hive
            && string.Equals(v.Key, key.Trim('\\'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
}
