using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;

namespace WinWaechter.Core.Security.Checks;

public enum Light { Green, Yellow, Red, Grey }

/// <summary>ExtensionPage: opens the browser's own page for one extension (<see cref="SecurityItem.Extension"/>). Action: runs <see cref="SecurityItem.Act"/>.</summary>
public enum SecurityActionKind { None, Link, Change, DefenderScan, ExtensionPage, Action }

/// <summary>One browser extension a row's action refers to.</summary>
public sealed record ExtensionTarget(string Browser, string Id);

/// <param name="Chip">Chip text instead of the light's default text (e.g. „Ansehen“, „Deaktiviert“).</param>
public sealed record SecurityItem(string Id, string Title, Light Light, string Finding, string Explanation,
    SecurityActionKind Action = SecurityActionKind.None, string? ActionLabel = null, string? Link = null,
    IChange? Change = null, string? ScanPath = null, IReadOnlyList<SecurityItem>? Children = null,
    string? Chip = null, ExtensionTarget? Extension = null,
    SystemAction? Act = null, string? ChangeLabel = null, bool Recommended = false);

public static class SecurityIds
{
    public const string FirewallDomain = "firewall-domain", FirewallPrivate = "firewall-private", FirewallPublic = "firewall-public",
        Defender = "defender", SmartScreen = "smartscreen", Uac = "uac", RemoteDesktop = "rdp", Encryption = "encryption",
        Updates = "updates", SystemProtection = "sysprotect", Listeners = "listeners", Extensions = "extensions";
}

/// <summary>Row titles, shared by the rules and the grey fallback rows.</summary>
public static class SecurityTitles
{
    public const string Defender = "Microsoft Defender", SmartScreen = "SmartScreen", Uac = "Benutzerkontensteuerung",
        RemoteDesktop = "Remotedesktop", Encryption = "Geräteverschlüsselung", Updates = "Windows-Updates", SystemProtection = "Computerschutz",
        Listeners = "Im Netz lauschende Programme", Extensions = "Browser-Erweiterungen";

    public static string Firewall(FirewallProfile p) => $"Firewall ({FirewallChange.ProfileName(p)})";
}
