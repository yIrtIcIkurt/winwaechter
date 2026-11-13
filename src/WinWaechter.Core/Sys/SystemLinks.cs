namespace WinWaechter.Core.Sys;

/// <summary>The only URIs WinWaechter opens through the shell. No web addresses: from the elevated
/// process a browser could start elevated as well.</summary>
public static class SystemLinks
{
    public const string AppsFeatures = "ms-settings:appsfeatures",
        WindowsUpdate = "ms-settings:windowsupdate",
        RemoteDesktop = "ms-settings:remotedesktop",
        DeviceEncryption = "ms-settings:deviceencryption",
        DefenderThreat = "windowsdefender://threat",
        DefenderAppBrowser = "windowsdefender://appbrowser",
        VisualEffects = "ms-settings:easeofaccess-visualeffects",
        AdvancedGraphics = "ms-settings:display-advancedgraphics",
        StoragePolicies = "ms-settings:storagepolicies",
        PowerSleep = "ms-settings:powersleep";

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(
        [AppsFeatures, WindowsUpdate, RemoteDesktop, DeviceEncryption, DefenderThreat, DefenderAppBrowser,
            VisualEffects, AdvancedGraphics, StoragePolicies, PowerSleep],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Exact match against <see cref="Allowed"/> (case-insensitive, no trimming).</summary>
    public static bool IsAllowed(string? uri) => uri is not null && Allowed.Contains(uri);

    const string StorePrefix = "ms-windows-store://pdp/?PFN=";

    /// <summary>Store page of exactly one package family; throws for anything that is not a package family name.</summary>
    public static string StoreLink(string family) =>
        Programs.WinAppPackages.IsValidFamily(family)
            ? StorePrefix + family
            : throw new ArgumentException("Ungültiger Paketfamilienname.", nameof(family));

    /// <summary>True only for a link <see cref="StoreLink"/> could have made.</summary>
    public static bool IsStoreLink(string? uri) =>
        uri is not null && uri.StartsWith(StorePrefix, StringComparison.Ordinal)
        && Programs.WinAppPackages.IsValidFamily(uri[StorePrefix.Length..]);
}
