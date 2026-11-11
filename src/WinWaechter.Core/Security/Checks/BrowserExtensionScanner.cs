using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

/// <summary>Unpacked: loaded by hand from a folder (developer mode, Chromium location 4).</summary>
public enum ExtensionOrigin { Store, Policy, External, Builtin, Unknown, Unpacked }

public sealed record BrowserExtension(string Browser, string Profile, string Id, string Name, ExtensionOrigin Origin)
{
    public string? Version { get; init; }
    public DateTime? InstalledUtc { get; init; }
    /// <summary>Installed but switched off in the browser.</summary>
    public bool Disabled { get; init; }
    /// <summary>API permissions and host patterns; null when not known (an entry only a policy names).</summary>
    public IReadOnlyList<string>? Permissions { get; init; }
    public IReadOnlyList<string>? Hosts { get; init; }
}

/// <summary>
/// Reads installed extensions of Edge, Chrome, Opera and Firefox for the interactive user.
/// Read only; unreadable, locked, oversized or broken files are skipped. Built-in extensions are left out, and so are
/// Chromium entries that are not installed (sync placeholders: neither a manifest nor an existing install path).
/// </summary>
public sealed partial class BrowserExtensionScanner(CleanerPaths paths, IRegistry reg)
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const int MaxNameLength = 100;
    public const string PolicyProfile = "Richtlinie";
    /// <summary>Upper bound for permission and host entries read per list.</summary>
    public const int MaxEntries = 500;

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
        return all.Where(e => e.Origin != ExtensionOrigin.Builtin)
            .Select(e => e with { Name = CleanName(e.Name, e.Id), Version = CleanVersion(e.Version) })
            .ToList();
    }

    /// <summary>Chromium <c>location</c> values (ruling R8): 1 store; 5/10 built-in; 7/9 policy; 4 unpacked (by hand);
    /// 2/3/6/8 from outside; missing or any other value is unknown.</summary>
    public static ExtensionOrigin ChromiumOrigin(int? location) => location switch
    {
        1 => ExtensionOrigin.Store,
        5 or 10 => ExtensionOrigin.Builtin,
        7 or 9 => ExtensionOrigin.Policy,
        4 => ExtensionOrigin.Unpacked,
        2 or 3 or 6 or 8 => ExtensionOrigin.External,
        _ => ExtensionOrigin.Unknown,
    };

    /// <summary>Chromium extension ids are 32 letters a–p; only those are used to build paths.</summary>
    public static bool IsChromiumId(string id) => ChromiumIdPattern().IsMatch(id);

    // Display names come from files anyone can write: no control or format characters (e.g. U+202E
    // right-to-left override), bounded length without splitting a surrogate pair.
    internal static string CleanName(string name, string id)
    {
        static string Clean(string t)
        {
            var c = new string(t.Where(ch => !char.IsControl(ch)
                && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format).ToArray()).Trim();
            if (c.Length <= MaxNameLength) return c;
            var cut = char.IsHighSurrogate(c[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
            return c[..cut].TrimEnd();
        }
        var n = Clean(name);
        if (n.Length == 0) n = Clean(id);
        return n.Length == 0 ? "(ohne Namen)" : n;
    }

    // Versions are shown too: digits, letters and . - + only, at most 40 characters; anything else is dropped.
    internal static string? CleanVersion(string? v) =>
        v is { Length: > 0 and <= 40 } && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? v : null;

    static void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { FileLog.Warn($"Browser-Erweiterungen ({what}) nicht lesbar", ex); }
    }

    [GeneratedRegex(@"^Profile \d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileFolder();

    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex ChromiumIdPattern();

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
                if (ext.Value.ValueKind != JsonValueKind.Object || seen.Contains(ext.Name)) continue;
                var origin = ChromiumOrigin(Int(ext.Value, "location"));
                var entry = origin == ExtensionOrigin.Builtin
                    ? new BrowserExtension(browser, profile, ext.Name, ext.Name, origin)
                    : ChromiumEntry(browser, profile, dir, ext.Name, ext.Value, origin);
                if (entry is null) continue;   // not installed (sync placeholder); the other file may know it
                seen.Add(ext.Name);
                into.Add(entry);
            }
        }
    }

    // Installed when there is a manifest (in the settings or a readable manifest.json in the highest version folder)
    // or the install path exists. Name: settings.manifest first, then manifest.json; __MSG_x__ via _locales.
    static BrowserExtension? ChromiumEntry(string browser, string profile, string profileDir, string id, JsonElement settings,
        ExtensionOrigin origin)
    {
        // Disk lookups only for well-formed ids, and only inside <profile>\Extensions.
        var extRoot = Path.GetFullPath(Path.Combine(profileDir, "Extensions")) + Path.DirectorySeparatorChar;
        var versionDir = IsChromiumId(id) ? HighestVersionDir(extRoot, Path.Combine(extRoot, id)) : null;
        JsonElement? prefsManifest = settings.TryGetProperty("manifest", out var m) && m.ValueKind == JsonValueKind.Object ? m : null;
        JsonDocument? fileDoc = null;
        var fileRead = false;
        JsonElement? FileManifest()
        {
            if (!fileRead && versionDir is not null)
                fileDoc = ReadJson(InsideFile(extRoot, Path.Combine(versionDir, "manifest.json")));
            fileRead = true;
            return fileDoc?.RootElement.ValueKind == JsonValueKind.Object ? fileDoc.RootElement : null;
        }
        try
        {
            var manifest = prefsManifest ?? FileManifest();
            if (manifest is null && !InstallPathExists(extRoot, Str(settings, "path"))) return null;

            string? name = null, locale = null;
            if (prefsManifest is { } pm)
            {
                name = Str(pm, "name");
                locale = Str(pm, "default_locale");
            }
            if (string.IsNullOrWhiteSpace(name) && FileManifest() is { } fm)
            {
                name = Str(fm, "name");
                locale ??= Str(fm, "default_locale");
            }
            if (name is not null && name.StartsWith("__MSG_", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal))
                name = versionDir is not null && locale is not null ? LocalizedMessage(extRoot, versionDir, locale, name[6..^2]) : null;

            var version = (manifest is { } vm ? Str(vm, "version") : null) ?? (FileManifest() is { } fv ? Str(fv, "version") : null);
            var perms = new List<string>();
            var hosts = new List<string>();
            if (settings.TryGetProperty("active_permissions", out var ap) && ap.ValueKind == JsonValueKind.Object)
                foreach (var list in new[] { "api", "manifest_permissions", "explicit_host", "scriptable_host" })
                    Collect(ap, list, perms, hosts);
            if (manifest is { } mf)
            {
                Collect(mf, "permissions", perms, hosts);
                Collect(mf, "host_permissions", perms, hosts);
                if (mf.TryGetProperty("content_scripts", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    foreach (var script in cs.EnumerateArray().Take(MaxEntries))
                        if (script.ValueKind == JsonValueKind.Object) Collect(script, "matches", perms, hosts);
            }
            return new BrowserExtension(browser, profile, id, string.IsNullOrWhiteSpace(name) ? id : name, origin)
            {
                Version = version,
                InstalledUtc = ChromiumTime(Str(settings, "first_install_time")) ?? ChromiumTime(Str(settings, "install_time")),
                Disabled = ChromiumDisabled(settings),
                Permissions = perms.Distinct(StringComparer.Ordinal).ToList(),
                Hosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }
        finally
        {
            fileDoc?.Dispose();
        }
    }

    // state 0 (older browsers) or any disable reason means switched off.
    static bool ChromiumDisabled(JsonElement settings) =>
        Int(settings, "state") == 0
        || (settings.TryGetProperty("disable_reasons", out var dr)
            && (dr.ValueKind == JsonValueKind.Array ? dr.GetArrayLength() > 0 : Int(settings, "disable_reasons") is { } d && d != 0));

    // A relative path lies inside <profile>\Extensions; an absolute one (unpacked) must be on a local drive.
    // Only its existence is checked, nothing is read from there.
    static bool InstallPathExists(string extRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string? full;
        if (Path.IsPathRooted(path))
        {
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
            full = path;
        }
        else
        {
            full = Inside(extRoot, Path.Combine(extRoot, path));
        }
        return full is not null && Directory.Exists(full);
    }

    /// <summary>Chromium time (microseconds since 1601-01-01 UTC, as a string) in UTC; null when missing or implausible.</summary>
    public static DateTime? ChromiumTime(string? microseconds)
    {
        if (!long.TryParse(microseconds, NumberStyles.None, CultureInfo.InvariantCulture, out var us) || us <= 0) return null;
        var epoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (us > (DateTime.MaxValue.Ticks - epoch.Ticks) / 10) return null;
        return epoch.AddTicks(us * 10);
    }

    // Strings are sorted into API permissions and host patterns; objects ({"socket": [...]}) count by their names.
    static void Collect(JsonElement parent, string name, List<string> perms, List<string> hosts)
    {
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var v in arr.EnumerateArray().Take(MaxEntries))
        {
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 2048 } t)
                (ExtensionRights.IsHostPattern(t) ? hosts : perms).Add(t);
            else if (v.ValueKind == JsonValueKind.Object)
                perms.AddRange(v.EnumerateObject().Take(MaxEntries).Select(p => p.Name));
        }
    }

    static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    static string? LocalizedMessage(string extRoot, string versionDir, string locale, string key)
    {
        if (locale.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || locale.Contains("..")) return null;
        var localeDir = Inside(extRoot, Path.Combine(versionDir, "_locales", locale));
        if (localeDir is null || IsReparsePoint(Path.Combine(versionDir, "_locales")) || IsReparsePoint(localeDir)) return null;
        using var doc = ReadJson(InsideFile(extRoot, Path.Combine(localeDir, "messages.json")));
        if (doc?.RootElement.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in doc.RootElement.EnumerateObject())
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                return Str(p.Value, "message");
        return null;
    }

    static string? HighestVersionDir(string extRoot, string extDir)
    {
        if (Inside(extRoot, extDir) is null || !Directory.Exists(extDir) || IsReparsePoint(extDir)) return null;
        return Directory.EnumerateDirectories(extDir)
            .Where(d => Inside(extRoot, d) is not null && !IsReparsePoint(d))
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
                var perms = new List<string>();
                var hosts = new List<string>();
                if (a.TryGetProperty("userPermissions", out var up) && up.ValueKind == JsonValueKind.Object)
                {
                    Collect(up, "permissions", perms, hosts);
                    Collect(up, "origins", perms, hosts);
                }
                into.Add(new BrowserExtension("Firefox", profile, id, string.IsNullOrWhiteSpace(name) ? id : name, origin)
                {
                    Version = Str(a, "version"),
                    InstalledUtc = a.TryGetProperty("installDate", out var d) && d.ValueKind == JsonValueKind.Number
                                   && d.TryGetInt64(out var ms) && ms is > 0 and <= MaxUnixMs
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null,
                    Disabled = Bool(a, "active") == false || Bool(a, "userDisabled") == true,
                    Permissions = perms.Distinct(StringComparer.Ordinal).ToList(),
                    Hosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                });
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

    /// <summary>The full path when it lies below root (root ends with a separator), else null.</summary>
    static string? Inside(string root, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    static string? InsideFile(string root, string path) =>
        Inside(root, path) is { } full && File.Exists(full) && !IsReparsePoint(full) ? full : null;

    // Junctions and symlinks under Extensions could point anywhere; they are not followed.
    static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    static JsonDocument? ReadJson(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path)) return null;
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

    const long MaxUnixMs = 253402300799999;   // 9999-12-31

    static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
