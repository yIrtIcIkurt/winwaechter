using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

/// <summary>The absolute Nie-Liste of the preinstalled-apps feature: never listed, never offered, never removed.</summary>
public static class BloatApps
{
    // Trailing "*" = prefix. Every other name is exact.
    public static readonly string[] Never =
    [
        "Microsoft.GamingApp", "Microsoft.XboxIdentityProvider", "Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay",
        "Microsoft.XboxSpeechToTextOverlay", "Microsoft.Xbox.TCUI", "Microsoft.GamingServices",
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.Windows.Photos", "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.WindowsTerminal",
        "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.WindowsCamera", "Microsoft.WindowsAlarms",
        "Microsoft.SecHealthUI", "Microsoft.MicrosoftEdge*", "Microsoft.VCLibs*", "Microsoft.NET*",
        "Microsoft.UI.Xaml*", "Microsoft.WindowsAppRuntime*", "Microsoft.Services.Store.Engagement",
    ];

    // Vendors whose apps are never touched, wherever in the name the vendor stands.
    static readonly string[] NeverContains = ["Dell", "Lexware", "Haufe"];

    static readonly Regex RulePackage = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]*\*?\z", RegexOptions.CultureInvariant);

    public static bool IsNever(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return true;
        if (NeverContains.Any(v => packageName.Contains(v, StringComparison.OrdinalIgnoreCase))) return true;
        return Never.Any(n => n.EndsWith('*')
            ? packageName.StartsWith(n[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(packageName, n, StringComparison.OrdinalIgnoreCase));
    }

    // Frameworks, system packages and anything whose safety flags could not be read (fail closed).
    public static bool IsBlocked(AppPackage p) =>
        p.IsFramework || p.NonRemovable || !SafeKinds.Contains(p.SignatureKind);

    static readonly string[] SafeKinds = ["None", "Developer", "Enterprise", "Store"];

    /// <summary>Plain package name characters with an optional "*" at the very end.</summary>
    public static bool IsValidRulePackage(string package) => RulePackage.IsMatch(package);

    /// <summary>
    /// True when a bloatApps rule would name something on the Nie-Liste: the package itself, or (for "X*") any
    /// Nie-Liste name the prefix would cover, or the prefix is so short that it would.
    /// </summary>
    public static bool RuleTouchesNever(string package)
    {
        var star = package.EndsWith('*');
        var stem = star ? package[..^1] : package;
        if (IsNever(stem) || IsNever(package)) return true;
        if (!star) return false;
        return Never.Any(n => n.TrimEnd('*').StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            || NeverContains.Any(v => v.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record AppPackage(string Name, string FullName, string FamilyName, string? InstallLocation,
    bool IsFramework, bool NonRemovable, string SignatureKind, bool InImage, string? Publisher = null);

public interface IAppPackages
{
    IReadOnlyList<AppPackage> List();
    void Remove(string fullName);
    void Register(string manifestPath);
    bool IsInstalled(string familyName);

    /// <summary>The only manifest path Register accepts for this package (null for an invalid full name).</summary>
    string? ManifestPathFor(string fullName);
}

/// <summary>
/// The built-in Windows PowerShell Appx cmdlets for the signed-in user's packages. Every value is checked
/// against a strict pattern before it is put into a command; values are single-quoted literals.
/// </summary>
public sealed class WinAppPackages(IProcessRunner runner, string userSid, string programFiles) : IAppPackages
{
    static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    static readonly Regex FullName = new(@"^[A-Za-z0-9.\-]+_[0-9.]+_[A-Za-z0-9]+_[A-Za-z0-9.\-~]*_[a-z0-9]{13}\z", RegexOptions.CultureInvariant);
    static readonly Regex Family = new(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}\z", RegexOptions.CultureInvariant);
    static readonly Regex Sid = new(@"^S-1-5-21(-[0-9]+){4}\z", RegexOptions.CultureInvariant);

    public static bool IsValidFullName(string? s) => s is not null && FullName.IsMatch(s);
    public static bool IsValidFamily(string? s) => s is not null && Family.IsMatch(s);
    public static bool IsValidSid(string? s) => s is not null && Sid.IsMatch(s);

    // SignatureKind of Get-AppxPackage: Windows PowerShell 5.1 writes the enum as a number.
    static readonly string[] SignatureKinds = ["None", "Developer", "Enterprise", "Store", "System"];

    public IReadOnlyList<AppPackage> List()
    {
        var packages = ListPackages();
        var inImage = ProvisionedNames();
        return packages.Select(p => p with { InImage = inImage.Contains(p.Name) }).ToList();
    }

    public void Remove(string fullName)
    {
        if (!IsValidFullName(fullName)) throw new ArgumentException("Ungültiger Paketname.", nameof(fullName));
        Run($"Remove-AppxPackage -Package '{fullName}'");
    }

    public void Register(string manifestPath)
    {
        Run($"Add-AppxPackage -Register '{CheckedManifest(manifestPath)}' -DisableDevelopmentMode");
    }

    public bool IsInstalled(string familyName)
    {
        if (!IsValidFamily(familyName)) throw new ArgumentException("Ungültiger Paketfamilienname.", nameof(familyName));
        return ListPackages().Any(p => string.Equals(p.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));
    }

    // Exactly <programFiles>\WindowsApps\<FullName>\AppxManifest.xml, nothing else.
    /// <summary>True for every character PowerShell may read as a quote: ' " and the typographic U+2018..U+201B.</summary>
    public static bool HasQuote(string s) => s.Any(c => c is '\'' or '"' or (>= '\u2018' and <= '\u201B'));

    public string? ManifestPathFor(string fullName)
    {
        if (!IsValidFullName(fullName)) return null;
        var path = Path.Combine(programFiles, "WindowsApps", fullName, "AppxManifest.xml");
        return HasQuote(path) ? null : path;
    }

    string CheckedManifest(string manifestPath)
    {
        if (manifestPath is null || HasQuote(manifestPath))
            throw new ArgumentException("Ungültiger Manifest-Pfad.", nameof(manifestPath));
        var folder = Path.GetFileName(Path.GetDirectoryName(manifestPath) ?? "");
        var expected = ManifestPathFor(folder);
        if (expected is null || !string.Equals(manifestPath, expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Das Manifest liegt nicht im WindowsApps-Ordner des Pakets.", nameof(manifestPath));
        return expected;
    }

    string CheckedSid()
    {
        if (!IsValidSid(userSid)) throw new InvalidOperationException("Ungültige Benutzerkennung (SID).");
        return userSid;
    }

    List<AppPackage> ListPackages()
    {
        var sid = CheckedSid();
        var json = Run($"Get-AppxPackage -User '{sid}' | Select-Object Name,PackageFullName,PackageFamilyName,Publisher,InstallLocation,IsFramework,NonRemovable,SignatureKind | ConvertTo-Json -Compress");
        var result = new List<AppPackage>();
        foreach (var e in Elements(json))
        {
            var name = Str(e, "Name");
            var full = Str(e, "PackageFullName");
            var family = Str(e, "PackageFamilyName");
            if (name is null || !IsValidFullName(full) || !IsValidFamily(family)) continue;   // never offer what we cannot address safely
            // A flag that is missing or not a boolean counts as "framework"/"not removable": never offered.
            result.Add(new AppPackage(name, full!, family!, Str(e, "InstallLocation"), Bool(e, "IsFramework", true), Bool(e, "NonRemovable", true),
                Kind(e), false, Str(e, "Publisher")));
        }
        return result;
    }

    // Windows-image copies. Not readable (e.g. not elevated): then nothing counts as "in the image" - the
    // conservative label "Nur über den Store wiederherstellbar" is shown.
    HashSet<string> ProvisionedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = Run("Get-AppxProvisionedPackage -Online | Select-Object DisplayName | ConvertTo-Json -Compress");
            foreach (var e in Elements(json))
                if (Str(e, "DisplayName") is { Length: > 0 } n) names.Add(n);
        }
        catch (InvalidOperationException) { }
        return names;
    }

    // ConvertTo-Json gives an object for one item, an array for several, nothing for none.
    static IEnumerable<JsonElement> Elements(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => x.Clone()).ToList(),
                JsonValueKind.Object => [doc.RootElement.Clone()],
                _ => [],
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Die Paketliste von Windows ließ sich nicht lesen: " + ex.Message, ex);
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name, bool whenUnreadable) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True
            : whenUnreadable;

    static string Kind(JsonElement e)
    {
        if (!e.TryGetProperty("SignatureKind", out var v)) return "Unknown";
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return i >= 0 && i < SignatureKinds.Length ? SignatureKinds[i] : "Unknown";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "Unknown" : "Unknown";
    }

    string Run(string command)
    {
        var (exit, stdout, stderr) = Task.Run(() => runner.RunAsync(PowerShell,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"", CancellationToken.None)).GetAwaiter().GetResult();
        if (exit != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"Windows meldete Fehlercode {exit}." : stderr.Trim());
        return stdout;
    }
}

/// <summary>Removes one preinstalled app for the signed-in user. Undo registers it again from the files Windows kept.</summary>
public sealed class AppxChange : IChange, IUndoLimit
{
    readonly IAppPackages _pkgs;
    readonly AppPackage _pkg;
    readonly BloatAppRule _rule;
    readonly RuleSet _rules;
    readonly bool? _restorable;   // from the journal after FromRecord; null before Apply

    public AppxChange(IAppPackages pkgs, AppPackage pkg, BloatAppRule rule, RuleSet rules, bool? restorable = null)
    {
        if (!WinAppPackages.IsValidFullName(pkg.FullName) || !WinAppPackages.IsValidFamily(pkg.FamilyName))
            throw new ArgumentException("Ungültiger Paketname.", nameof(pkg));
        _pkgs = pkgs; _pkg = pkg; _rule = rule; _rules = rules; _restorable = restorable;
    }

    public string Kind => "appx";
    public string Target => _pkg.FamilyName;
    public string Description => $"„{_rule.Title}“ entfernen";
    public string? UndoLimit => (_restorable ?? _pkg.InImage) ? null : UndoLimits.StoreOnly;

    public static AppxChange FromRecord(ChangeRecord r, IAppPackages pkgs, RuleSet rules)
    {
        var name = r.Before.GetValueOrDefault("name");
        var rule = name is null ? null : rules.FindBloatApp(name);
        if (name is null || rule is null || BloatApps.IsNever(name))
            throw Rejected();
        var full = r.Before.GetValueOrDefault("fullName");
        var family = r.Before.GetValueOrDefault("family");
        if (!WinAppPackages.IsValidFullName(full) || !WinAppPackages.IsValidFamily(family)
            || !family!.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) || !full!.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))
            throw Rejected();
        var manifest = r.After.GetValueOrDefault("manifest");
        if (!string.IsNullOrEmpty(manifest) && !IsAcceptedManifest(pkgs, manifest, full))
            throw Rejected();
        bool? restorable = r.After.TryGetValue("restorable", out var flag) ? flag == "true" && !string.IsNullOrEmpty(manifest) : null;
        return new AppxChange(pkgs, new AppPackage(name, full, family, null, false, false, "Store", false), rule, rules, restorable);
    }

    static InvalidOperationException Rejected() =>
        new("Dieser Eintrag gehört nicht zu den entfernbaren Apps – Rückgängig abgelehnt.");

    public static void Register(ChangeFactory f, IAppPackages pkgs, RuleSet rules) =>
        f.Register("appx", r => FromRecord(r, pkgs, rules));

    // Exactly the path the package interface would accept for this package (WinAppPackages checks it again).
    static bool IsAcceptedManifest(IAppPackages pkgs, string manifest, string fullName)
    {
        if (WinAppPackages.HasQuote(manifest)) return false;
        var expected = pkgs.ManifestPathFor(fullName);
        return expected is not null && string.Equals(manifest, expected, StringComparison.OrdinalIgnoreCase);
    }

    public (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() => (Identity(_pkg), new());

    Dictionary<string, string?> Identity(AppPackage p) => new()
    {
        ["name"] = p.Name, ["fullName"] = p.FullName, ["family"] = p.FamilyName, ["title"] = _rule.Title,
    };

    public ChangeRecord Apply(SessionContext ctx)
    {
        // What is installed right now counts, not what was listed or journalled earlier (update, retry).
        var live = _pkgs.List().FirstOrDefault(p => string.Equals(p.FamilyName, _pkg.FamilyName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"„{_rule.Title}“ ist nicht mehr installiert.");
        Recheck(live);
        try { _pkgs.Remove(live.FullName); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"„{_rule.Title}“ ließ sich nicht entfernen: {ex.Message}", ex);
        }

        // The files Windows kept (image copy) at the one path Register accepts decide whether Undo can work.
        var manifest = _pkgs.ManifestPathFor(live.FullName);
        var restorable = manifest is not null && !WinAppPackages.HasQuote(manifest) && File.Exists(manifest);
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description, Identity(live),
            restorable ? new() { ["restorable"] = "true", ["manifest"] = manifest } : new() { ["restorable"] = "false" },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    // The list and the protection are checked again right before removing, against the live package.
    void Recheck(AppPackage live)
    {
        if (!string.Equals(live.Name, _pkg.Name, StringComparison.OrdinalIgnoreCase)
            || !WinAppPackages.IsValidFullName(live.FullName) || !WinAppPackages.IsValidFamily(live.FamilyName)
            || !live.FullName.StartsWith(live.Name + "_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"„{_rule.Title}“ stimmt nicht mehr mit dem installierten Paket überein.");
        if (_rules.FindBloatApp(live.Name) is null || BloatApps.IsNever(live.Name))
            throw new InvalidOperationException($"„{_rule.Title}“ steht nicht auf der Liste der entfernbaren Apps.");
        if (BloatApps.IsBlocked(live) || _rules.IsProtectedApp(new ItemFacts(live.Name, live.InstallLocation, live.Publisher, null)))
            throw new InvalidOperationException($"„{_rule.Title}“ ist ein geschützter Windows-Bestandteil und wird nicht entfernt.");
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        var manifest = record.After.GetValueOrDefault("manifest");
        if (record.After.GetValueOrDefault("restorable") != "true" || string.IsNullOrEmpty(manifest) || !IsAcceptedManifest(_pkgs, manifest, _pkg.FullName))
            throw new InvalidOperationException(UndoLimits.StoreOnly);
        _pkgs.Register(manifest);
    }

    public DriftState CheckDrift(ChangeRecord record) =>
        _pkgs.IsInstalled(_pkg.FamilyName) ? DriftState.Changed : DriftState.Unchanged;
}

public sealed record BloatAppRow(BloatAppRule Rule, AppPackage Package, AppxChange Change);

/// <summary>The preinstalled apps that are on the fixed list and may be removed; nothing else is ever offered.</summary>
public sealed class BloatAppScanner(IAppPackages pkgs, RuleSet rules)
{
    static readonly StringComparer German = StringComparer.Create(new CultureInfo("de-DE"), true);

    public IReadOnlyList<BloatAppRow> Scan()
    {
        var rows = new List<BloatAppRow>();
        foreach (var p in pkgs.List())
        {
            if (BloatApps.IsBlocked(p) || BloatApps.IsNever(p.Name)) continue;
            var rule = rules.FindBloatApp(p.Name);
            if (rule is null) continue;
            if (rules.IsProtectedApp(new ItemFacts(p.Name, p.InstallLocation, p.Publisher, null))) continue;
            if (!WinAppPackages.IsValidFullName(p.FullName) || !WinAppPackages.IsValidFamily(p.FamilyName)) continue;
            rows.Add(new BloatAppRow(rule, p, new AppxChange(pkgs, p, rule, rules)));
        }
        return rows.OrderByDescending(r => r.Rule.Recommended).ThenBy(r => r.Rule.Title, German).ToList();
    }
}
