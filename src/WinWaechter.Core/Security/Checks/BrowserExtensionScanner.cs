using System.Text.Json;
using System.Text.RegularExpressions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

public enum ExtensionOrigin { Store, Policy, External, Builtin }

public sealed record BrowserExtension(string Browser, string Profile, string Id, string Name, ExtensionOrigin Origin);

/// <summary>
/// Reads installed extensions of Edge, Chrome, Opera and Firefox for the interactive user.
/// Read only; unreadable, locked, oversized or broken files are skipped. Built-in extensions are left out.
/// </summary>
public sealed partial class BrowserExtensionScanner(CleanerPaths paths, IRegistry reg)
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const string PolicyProfile = "Richtlinie";

    static readonly (string Browser, string Key)[] ForceLists =
    [
        ("Chrome", @"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist"),
        ("Edge", @"SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist"),
    ];

    public IReadOnlyList<BrowserExtension> Scan()
    {
        var all = new List<BrowserExtension>();
        Safe("Edge", () => ChromiumUserData("Edge", Path.Combine(paths.LocalAppData, @"Microsoft\Edge\User Data"), all));
        Safe("Chrome", () => ChromiumUserData("Chrome", Path.Combine(paths.LocalAppData, @"Google\Chrome\User Data"), all));
        Safe("Opera", () => ChromiumProfile("Opera", "Standard", Path.Combine(paths.RoamingAppData, @"Opera Software\Opera Stable"), all));
        Safe("Firefox", () => Firefox(all));
        Safe("Richtlinien", () => ApplyPolicies(all));
        return all.Where(e => e.Origin != ExtensionOrigin.Builtin).ToList();
    }

    /// <summary>Chromium <c>location</c> values (ruling R8): 1 store; 5/10 built-in; 7/9 policy; 2/3/4/6/8 from outside.</summary>
    public static ExtensionOrigin ChromiumOrigin(int location) => location switch
    {
        5 or 10 => ExtensionOrigin.Builtin,
        7 or 9 => ExtensionOrigin.Policy,
        2 or 3 or 4 or 6 or 8 => ExtensionOrigin.External,
        _ => ExtensionOrigin.Store,
    };

    static void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { FileLog.Warn($"Browser-Erweiterungen ({what}) nicht lesbar", ex); }
    }

    [GeneratedRegex(@"^Profile \d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileFolder();

    static void ChromiumUserData(string browser, string userData, List<BrowserExtension> into)
    {
        if (!Directory.Exists(userData)) return;
        foreach (var dir in Directory.EnumerateDirectories(userData).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var isProfile = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) || ProfileFolder().IsMatch(name)
                            || File.Exists(Path.Combine(dir, "Preferences"));
            if (isProfile) Safe($"{browser} {name}", () => ChromiumProfile(browser, name, dir, into));
        }
    }

    static void ChromiumProfile(string browser, string profile, string dir, List<BrowserExtension> into)
    {
        if (!Directory.Exists(dir)) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            using var doc = ReadJson(Path.Combine(dir, file));
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            if (!TryPath(doc.RootElement, out var settings, "extensions", "settings") || settings.ValueKind != JsonValueKind.Object) continue;
            foreach (var ext in settings.EnumerateObject())
            {
                if (ext.Value.ValueKind != JsonValueKind.Object || !seen.Add(ext.Name)) continue;
                var location = ext.Value.TryGetProperty("location", out var l) && l.TryGetInt32(out var li) ? li : 0;
                var origin = ChromiumOrigin(location);
                var name = origin == ExtensionOrigin.Builtin ? ext.Name : ChromiumName(dir, ext.Name, ext.Value);
                into.Add(new BrowserExtension(browser, profile, ext.Name, name, origin));
            }
        }
    }

    // settings.manifest first, then the highest installed version's manifest.json; __MSG_x__ via _locales.
    static string ChromiumName(string profileDir, string id, JsonElement settings)
    {
        var versionDir = HighestVersionDir(Path.Combine(profileDir, "Extensions", id));
        string? name = null, locale = null;
        if (settings.TryGetProperty("manifest", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            name = Str(m, "name");
            locale = Str(m, "default_locale");
        }
        if (string.IsNullOrWhiteSpace(name) && versionDir is not null)
        {
            using var manifest = ReadJson(Path.Combine(versionDir, "manifest.json"));
            if (manifest?.RootElement.ValueKind == JsonValueKind.Object)
            {
                name = Str(manifest.RootElement, "name");
                locale ??= Str(manifest.RootElement, "default_locale");
            }
        }
        if (name is not null && name.StartsWith("__MSG_", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal))
            name = versionDir is not null && locale is not null ? LocalizedMessage(versionDir, locale, name[6..^2]) : null;
        return string.IsNullOrWhiteSpace(name) ? id : name;
    }

    static string? LocalizedMessage(string versionDir, string locale, string key)
    {
        if (locale.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || locale.Contains("..")) return null;
        using var doc = ReadJson(Path.Combine(versionDir, "_locales", locale, "messages.json"));
        if (doc?.RootElement.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in doc.RootElement.EnumerateObject())
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                return Str(p.Value, "message");
        return null;
    }

    static string? HighestVersionDir(string extDir)
    {
        if (!Directory.Exists(extDir)) return null;
        return Directory.EnumerateDirectories(extDir)
            .Select(d => (Dir: d, Key: VersionKey(Path.GetFileName(d))))
            .OrderByDescending(x => x.Key, VersionComparer.Instance)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    // "1.2.3_0" → [1, 2, 3, 0]; non-numeric parts count as -1.
    static int[] VersionKey(string folder) =>
        folder.Split('.', '_').Select(p => int.TryParse(p, out var n) ? n : -1).ToArray();

    sealed class VersionComparer : IComparer<int[]>
    {
        public static readonly VersionComparer Instance = new();
        public int Compare(int[]? a, int[]? b)
        {
            a ??= []; b ??= [];
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
                if (c != 0) return c;
            }
            return 0;
        }
    }

    void Firefox(List<BrowserExtension> into)
    {
        var profiles = Path.Combine(paths.RoamingAppData, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(profiles)) return;
        foreach (var dir in Directory.EnumerateDirectories(profiles).Order(StringComparer.OrdinalIgnoreCase))
        {
            using var doc = ReadJson(Path.Combine(dir, "extensions.json"));
            if (doc?.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array) continue;
            // "xxxxxxxx.default-release" → "default-release"
            var folder = Path.GetFileName(dir);
            var dot = folder.IndexOf('.');
            var profile = dot >= 0 && dot < folder.Length - 1 ? folder[(dot + 1)..] : folder;
            foreach (var a in addons.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || Str(a, "type") != "extension" || Str(a, "id") is not { } id) continue;
                var origin = FirefoxOrigin(Str(a, "location"), Str(a, "sourceURI"));
                if (origin == ExtensionOrigin.Builtin) continue;
                var name = a.TryGetProperty("defaultLocale", out var dl) && dl.ValueKind == JsonValueKind.Object ? Str(dl, "name") : null;
                into.Add(new BrowserExtension("Firefox", profile, id, string.IsNullOrWhiteSpace(name) ? id : name, origin));
            }
        }
    }

    static ExtensionOrigin FirefoxOrigin(string? location, string? sourceUri)
    {
        if (location is "app-builtin" or "app-system-defaults" or "app-system-addons") return ExtensionOrigin.Builtin;
        if (location == "app-profile" && Uri.TryCreate(sourceUri, UriKind.Absolute, out var u)
            && u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, "addons.mozilla.org", StringComparison.OrdinalIgnoreCase))
            return ExtensionOrigin.Store;
        return ExtensionOrigin.External;
    }

    // Force-installed ids mark the browser's extensions as Policy, or appear as their own entry.
    void ApplyPolicies(List<BrowserExtension> all)
    {
        foreach (var (browser, key) in ForceLists)
            foreach (var hive in new[] { Hive.LocalMachine, Hive.CurrentUser })
                Safe($"Richtlinie {browser}", () =>
                {
                    foreach (var valueName in reg.ValueNames(hive, key))
                    {
                        if (reg.Get(hive, key, valueName) is not string data) continue;
                        var id = data.Split(';')[0].Trim();
                        if (id.Length == 0) continue;
                        var hit = false;
                        for (var i = 0; i < all.Count; i++)
                        {
                            if (all[i].Browser != browser || !string.Equals(all[i].Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                            hit = true;
                            if (all[i].Origin != ExtensionOrigin.Builtin) all[i] = all[i] with { Origin = ExtensionOrigin.Policy };
                        }
                        if (!hit) all.Add(new BrowserExtension(browser, PolicyProfile, id, id, ExtensionOrigin.Policy));
                    }
                });
    }

    static JsonDocument? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxFileBytes) return null;
            return JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    static bool TryPath(JsonElement e, out JsonElement found, params string[] names)
    {
        found = e;
        foreach (var n in names)
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(n, out found)) return false;
        return true;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
