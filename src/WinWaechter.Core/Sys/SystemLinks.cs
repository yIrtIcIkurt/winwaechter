namespace WinWaechter.Core.Sys;

/// <summary>The only URIs WinWaechter opens through the shell.</summary>
public static class SystemLinks
{
    public const string AppsFeatures = "ms-settings:appsfeatures",
        WindowsUpdate = "ms-settings:windowsupdate",
        RemoteDesktop = "ms-settings:remotedesktop",
        DeviceEncryption = "ms-settings:deviceencryption",
        DefenderThreat = "windowsdefender://threat",
        DefenderAppBrowser = "windowsdefender://appbrowser",
        Repository = "https://github.com/yIrtIcIkurt/winwaechter";

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(
        [AppsFeatures, WindowsUpdate, RemoteDesktop, DeviceEncryption, DefenderThreat, DefenderAppBrowser, Repository],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Exact match against <see cref="Allowed"/> (case-insensitive, no trimming).</summary>
    public static bool IsAllowed(string? uri) => uri is not null && Allowed.Contains(uri);
}
