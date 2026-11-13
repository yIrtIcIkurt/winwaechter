using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Performance;

/// <summary>One registry DWORD the Leistung page may change; Targets lists the only allowed values.</summary>
public sealed record PerfValue(string Id, Hive Hive, string Key, string Name,
    IReadOnlyList<int> Targets, int DefaultWhenMissing, bool Broadcast);

/// <summary>The fixed whitelist of values behind the Leistung page. Nothing outside this list is ever written.</summary>
public static class PerfValues
{
    public const string BackgroundRoot = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";

    const string StoragePolicyKey = @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";

    public static readonly PerfValue Transparency = new("transparency", Hive.CurrentUser,
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", [0, 1], 1, true);

    public static readonly PerfValue GameMode = new("gamemode", Hive.CurrentUser,
        @"Software\Microsoft\GameBar", "AutoGameModeEnabled", [0, 1], 1, false);

    // Default 0 means "not offered" (hardware or driver without support).
    public static readonly PerfValue GpuScheduling = new("gpu-scheduling", Hive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", [1, 2], 0, false);

    public static readonly PerfValue StorageSenseOn = new("storagesense-on", Hive.CurrentUser,
        StoragePolicyKey, "01", [0, 1], 0, false);

    public static readonly PerfValue StorageSenseFrequency = new("storagesense-frequency", Hive.CurrentUser,
        StoragePolicyKey, "2048", [0, 1, 7, 30], 0, false);

    // Fast startup (hybrid shutdown); only effective while hibernation is on.
    public static readonly PerfValue FastStartup = new("faststartup", Hive.LocalMachine,
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", [0, 1], 0, false);

    // Official browser machine policies. Only 0 (off) is ever written; a missing value means the browser default "on".
    // Switching back on happens only by undo in the Verlauf (deletes the value or restores the old one).
    public const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";
    public const string ChromePolicyKey = @"SOFTWARE\Policies\Google\Chrome";

    public static readonly PerfValue EdgeStartupBoost = new("edge-startupboost", Hive.LocalMachine,
        EdgePolicyKey, "StartupBoostEnabled", [0], 1, false);

    public static readonly PerfValue EdgeBackgroundMode = new("edge-background", Hive.LocalMachine,
        EdgePolicyKey, "BackgroundModeEnabled", [0], 1, false);

    public static readonly PerfValue ChromeBackgroundMode = new("chrome-background", Hive.LocalMachine,
        ChromePolicyKey, "BackgroundModeEnabled", [0], 1, false);

    static readonly PerfValue[] Fixed = [Transparency, GameMode, GpuScheduling, StorageSenseOn, StorageSenseFrequency,
        FastStartup, EdgeStartupBoost, EdgeBackgroundMode, ChromeBackgroundMode];

    public static PerfValue BackgroundDisabled(string app) => Background(app, "Disabled");

    public static PerfValue BackgroundDisabledByUser(string app) => Background(app, "DisabledByUser");

    static PerfValue Background(string app, string name) =>
        !IsBackgroundApp(app)
            ? throw new ArgumentException("Ungültiger Name einer Hintergrund-App.", nameof(app))
            : new($"background-{name.ToLowerInvariant()}", Hive.CurrentUser, BackgroundRoot + "\\" + app, name, [0, 1], 0, false);

    /// <summary>The value for exactly this (hive, key, name), or null when it is not on the list.</summary>
    public static PerfValue? Find(Hive hive, string key, string name)
    {
        foreach (var v in Fixed)
            if (v.Hive == hive
                && string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                return v;

        var prefix = BackgroundRoot + "\\";
        if (hive == Hive.CurrentUser && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var app = key[prefix.Length..];
            if (IsBackgroundApp(app))
            {
                if (string.Equals(name, "Disabled", StringComparison.OrdinalIgnoreCase)) return BackgroundDisabled(app);
                if (string.Equals(name, "DisabledByUser", StringComparison.OrdinalIgnoreCase)) return BackgroundDisabledByUser(app);
            }
        }
        return null;
    }

    /// <summary>A plain app id: one key segment, no path tricks, no control characters.</summary>
    public static bool IsBackgroundApp(string app) =>
        !string.IsNullOrEmpty(app) && app.Length <= 200
        && !app.Contains('\\') && !app.Contains('/') && !app.Contains("..")
        && !app.Any(char.IsControl);
}
