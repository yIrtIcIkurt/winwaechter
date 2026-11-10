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
        DefenderAppBrowser = "windowsdefender://appbrowser";

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(
        [AppsFeatures, WindowsUpdate, RemoteDesktop, DeviceEncryption, DefenderThreat, DefenderAppBrowser],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Exact match against <see cref="Allowed"/> (case-insensitive, no trimming).</summary>
    public static bool IsAllowed(string? uri) => uri is not null && Allowed.Contains(uri);
}
