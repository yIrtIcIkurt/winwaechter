using System.Text.Json;
using BloatNever = WinWaechter.Core.Programs.BloatApps;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Rules;

public enum RuleVerdict { Protected, Expendable, Pua, Miner, Unknown }

/// <summary>
/// Rules from rules.json. Order of decision: Protected > Miner > Pua > Expendable > Unknown.
/// Matching is case-insensitive. Name and publisher matchers match on word start
/// ("Dell" hits "Dell Inc" and "DellClientManagementService", not "Modeller").
/// A needle that starts with a non-letter (e.g. "_Bundle") is a plain substring.
/// Path matchers are plain substrings of the exe path (they contain backslashes).
/// </summary>
public sealed class RuleSet
{
    sealed record Matcher(string? Name, string? Publisher, string? Path)
    {
        // An empty matcher never matches (it would otherwise match everything).
        // The two protected entries that do not apply to apps named in bloatApps.
        public bool IsAppException =>
            (Name is null && Path is null && string.Equals(Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase))
            || (Name is null && Publisher is null && Path is not null
                && (string.Equals(Path, @"%ProgramFiles%\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path, @"%ProgramFiles(x86)%\WindowsApps\", StringComparison.OrdinalIgnoreCase)));

        public bool IsMatch(ItemFacts f) =>
            (Name is not null || Publisher is not null || Path is not null)
            && (Name is null || WordStart(f.Name, Name))
            && (Publisher is null || WordStart(f.Publisher, Publisher))
            && (Path is null || PathMatches(f.ExePath));

        // "%..%" matchers are expanded at load and anchored at the start of the normalized path
        // (an AppData\Realtek or Users\x\Windows folder must not look protected).
        // Other path matchers are plain substrings; \Lexware\ stays one on purpose:
        // wrongly protecting a Lexware look-alike is better than touching the bookkeeping.
        bool PathMatches(string? exe)
        {
            if (string.IsNullOrEmpty(exe) || Path is null) return false;
            return Path.StartsWith('%')
                ? exe.StartsWith(Environment.ExpandEnvironmentVariables(Path), StringComparison.OrdinalIgnoreCase)
                : exe.Contains(Path, StringComparison.OrdinalIgnoreCase);
        }
    }

    sealed class Dto
    {
        public int? RulesVersion { get; set; }
        public string? MinAppVersion { get; set; }
        public List<MatcherDto>? Protected { get; set; }
        public List<MatcherDto>? ExpendableAutostart { get; set; }
        public List<MatcherDto>? Pua { get; set; }
        public List<string>? MinerNames { get; set; }
        public List<string>? MinerCommandLine { get; set; }
        public List<int>? PoolPorts { get; set; }
        public List<string>? LeftoverIgnore { get; set; }
        public List<PrivacyDto>? Privacy { get; set; }
        public List<BloatAppDto>? BloatApps { get; set; }
    }

    sealed class BloatAppDto
    {
        public string? Package { get; set; }
        public string? Title { get; set; }
        public bool Recommended { get; set; }
    }

    sealed class PrivacyDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Explanation { get; set; }
        public bool Recommended { get; set; }
        public string? Requires { get; set; }
        public PrivacyEffectiveDto? Effective { get; set; }
        public string? Group { get; set; }
        public List<PrivacyValueDto>? Values { get; set; }
    }

    sealed class PrivacyValueDto
    {
        public string? Hive { get; set; }
        public string? Key { get; set; }
        public string? Name { get; set; }
        public int Target { get; set; }
    }

    sealed class PrivacyEffectiveDto
    {
        public string? Hive { get; set; }
        public string? Key { get; set; }
        public string? Name { get; set; }
        public int AtMost { get; set; }
    }

    sealed class MatcherDto
    {
        public string? Name { get; set; }
        public string? Publisher { get; set; }
        public string? Path { get; set; }
    }

    readonly Matcher[] _protected, _expendable, _pua;
    readonly string[] _minerNames, _minerCmd;

    public IReadOnlySet<int> PoolPorts { get; }

    /// <summary>Known miner program names (matched against exe file names by the heuristics).</summary>
    public IReadOnlyList<string> MinerNames => _minerNames;

    /// <summary>Version of the rules file ("rulesVersion"); 0 when the field is missing.</summary>
    public int RulesVersion { get; }

    /// <summary>Raw "minAppVersion" string; null when missing.</summary>
    public string? MinAppVersion { get; }

    RuleSet(Dto d)
    {
        // The protected list is the safety core: refuse to run without it.
        if (d.Protected is null || d.Protected.Count == 0)
            throw new InvalidDataException("rules.json enthält keine Schutzliste (\"protected\").");
        RulesVersion = d.RulesVersion ?? 0;
        MinAppVersion = d.MinAppVersion;
        _protected = ToMatchers(d.Protected);
        _expendable = ToMatchers(d.ExpendableAutostart);
        _pua = ToMatchers(d.Pua);
        _minerNames = (d.MinerNames ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        _minerCmd = (d.MinerCommandLine ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        PoolPorts = new HashSet<int>(d.PoolPorts ?? []);
        LeftoverIgnore = (d.LeftoverIgnore ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        Privacy = (d.Privacy ?? []).Select(ToSetting).ToArray();
        RejectDuplicatePrivacyValues(Privacy);
        BloatApps = (d.BloatApps ?? []).Select(ToBloatRule).ToArray();
    }

    // Used by UnionWith only.
    RuleSet(int version, string? minApp, Matcher[] prot, Matcher[] exp, Matcher[] pua, string[] minerNames,
        string[] minerCmd, IReadOnlySet<int> ports, IReadOnlyList<string> leftoverIgnore, IReadOnlyList<PrivacySetting> privacy,
        IReadOnlyList<BloatAppRule> bloatApps)
    {
        RulesVersion = version;
        MinAppVersion = minApp;
        _protected = prot;
        _expendable = exp;
        _pua = pua;
        _minerNames = minerNames;
        _minerCmd = minerCmd;
        PoolPorts = ports;
        LeftoverIgnore = leftoverIgnore;
        Privacy = privacy;
        BloatApps = bloatApps;
    }

    /// <summary>
    /// Bundled rules (this) plus downloaded rules (other). Lists are only ever unioned, so an update can add
    /// protection but never remove it. Privacy and BloatApps always stay this set's (never taken from the network).
    /// </summary>
    public RuleSet UnionWith(RuleSet other)
    {
        static Matcher[] M(Matcher[] a, Matcher[] b) => a.Concat(b).DistinctBy(
            m => (m.Name?.ToUpperInvariant(), m.Publisher?.ToUpperInvariant(), m.Path?.ToUpperInvariant())).ToArray();
        static string[] S(IEnumerable<string> a, IEnumerable<string> b) => a.Concat(b).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return new RuleSet(Math.Max(RulesVersion, other.RulesVersion), other.MinAppVersion,
            M(_protected, other._protected), M(_expendable, other._expendable), M(_pua, other._pua),
            S(_minerNames, other._minerNames), S(_minerCmd, other._minerCmd),
            new HashSet<int>(PoolPorts.Concat(other.PoolPorts)), S(LeftoverIgnore, other.LeftoverIgnore), Privacy, BloatApps);
    }

    // The fixed list of registry values the privacy feature may ever write.
    static PrivacySetting ToSetting(PrivacyDto p)
    {
        if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Title) || p.Values is not { Count: > 0 })
            throw new InvalidDataException("rules.json: ungültiger Eintrag in \"privacy\".");
        var values = p.Values.Select(v =>
        {
            if (string.IsNullOrWhiteSpace(v.Key) || string.IsNullOrWhiteSpace(v.Name))
                throw new InvalidDataException($"rules.json: Wert ohne Schlüssel/Name in \"{p.Id}\".");
            return new PrivacyValue(ToHive(v.Hive), v.Key.Trim('\\'), v.Name, v.Target);
        }).ToArray();
        PrivacyEffective? eff = null;
        if (p.Effective is { } e)
        {
            if (string.IsNullOrWhiteSpace(e.Key) || string.IsNullOrWhiteSpace(e.Name))
                throw new InvalidDataException($"rules.json: \"effective\" ohne Schlüssel/Name in \"{p.Id}\".");
            eff = new PrivacyEffective(ToHive(e.Hive), e.Key.Trim('\\'), e.Name, e.AtMost);
        }
        return new PrivacySetting(p.Id, p.Title, p.Explanation ?? "", p.Recommended, p.Requires, values, eff,
            string.IsNullOrWhiteSpace(p.Group) ? null : p.Group.Trim());
    }

    // One (hive, key, name) may be listed once only: FindPrivacyValue takes the first match.
    static void RejectDuplicatePrivacyValues(IReadOnlyList<PrivacySetting> privacy)
    {
        var seen = new HashSet<(Hive, string, string)>();
        foreach (var s in privacy)
            foreach (var v in s.Values)
                if (!seen.Add((v.Hive, v.Key.ToUpperInvariant(), v.Name.ToUpperInvariant())))
                    throw new InvalidDataException($"rules.json: Wert \"{v.Name}\" kommt in \"privacy\" mehrfach vor (\"{s.Id}\").");
    }

    // The fixed list of preinstalled apps that may be offered for removal ("*" only at the end).
    static BloatAppRule ToBloatRule(BloatAppDto b)
    {
        if (string.IsNullOrWhiteSpace(b.Package) || string.IsNullOrWhiteSpace(b.Title))
            throw new InvalidDataException("rules.json: ungültiger Eintrag in \"bloatApps\" (Paket und Titel nötig).");
        var star = b.Package.IndexOf('*');
        if (star >= 0 && star != b.Package.Length - 1)
            throw new InvalidDataException($"rules.json: \"*\" nur am Ende erlaubt (\"{b.Package}\" in \"bloatApps\").");
        if (!BloatNever.IsValidRulePackage(b.Package))
            throw new InvalidDataException($"rules.json: ungültiger Paketname \"{b.Package}\" in \"bloatApps\".");
        if (BloatNever.RuleTouchesNever(b.Package))
            throw new InvalidDataException($"rules.json: \"{b.Package}\" steht auf der Nie-Liste und darf nicht in \"bloatApps\" stehen.");
        return new BloatAppRule(b.Package, b.Title, b.Recommended);
    }

    static Hive ToHive(string? s) => s switch
    {
        "HKCU" => Hive.CurrentUser,
        "HKLM" => Hive.LocalMachine,
        _ => throw new InvalidDataException($"rules.json: unbekannter Registry-Zweig \"{s}\" (nur HKCU/HKLM)."),
    };

    /// <summary>Privacy settings (fixed list; the only registry values the privacy feature may write).</summary>
    public IReadOnlyList<PrivacySetting> Privacy { get; }

    /// <summary>Preinstalled apps that may be offered for removal (fixed local list; never from the network).</summary>
    public IReadOnlyList<BloatAppRule> BloatApps { get; }

    /// <summary>The rule naming this package (exact name, or prefix for "X*"); null for anything unlisted or on the Nie-Liste.</summary>
    public BloatAppRule? FindBloatApp(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName) || BloatNever.IsNever(packageName)) return null;
        return BloatApps.FirstOrDefault(r => r.Package.EndsWith('*')
            ? packageName.StartsWith(r.Package[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(packageName, r.Package, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The protection list for an app named in bloatApps: like <see cref="Classify"/>'s Protected, but without the
    /// {publisher: Microsoft} entry and the two WindowsApps paths (nearly every such app is a Microsoft package there).
    /// Dell, Lexware, antivirus, names and all other paths still protect.
    /// </summary>
    public bool IsProtectedApp(ItemFacts f)
    {
        if (!string.IsNullOrWhiteSpace(f.ExePath)) f = f with { ExePath = PathNormalizer.NormalizePath(f.ExePath) };
        return _protected.Any(m => !m.IsAppException && m.IsMatch(f));
    }

    /// <summary>Folder-name patterns the leftover scan skips ("X*" = prefix, else exact).</summary>
    public IReadOnlyList<string> LeftoverIgnore { get; }

    public bool IsLeftoverIgnored(string folderName) =>
        LeftoverIgnore.Any(p => p.EndsWith('*')
            ? folderName.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(folderName, p, StringComparison.OrdinalIgnoreCase));

    public PrivacyValue? FindPrivacyValue(Hive h, string key, string name)
    {
        key = key.Trim('\\');
        return Privacy.SelectMany(s => s.Values).FirstOrDefault(v =>
            v.Hive == h && string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    static Matcher[] ToMatchers(List<MatcherDto>? list) =>
        (list ?? []).Select(m => new Matcher(Norm(m.Name), Norm(m.Publisher), Norm(m.Path))).ToArray();

    static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static RuleSet Load(string jsonPath)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(jsonPath); }
        catch (Exception ex)
        {
            throw new InvalidDataException($"rules.json konnte nicht geladen werden ({jsonPath}): {ex.Message}", ex);
        }
        return Parse(bytes);
    }

    /// <summary>Parses rules.json bytes (UTF-8, BOM allowed). Throws InvalidDataException on any error.</summary>
    public static RuleSet Parse(byte[] json)
    {
        try
        {
            ReadOnlySpan<byte> span = json;
            if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) span = span[3..];
            var dto = JsonSerializer.Deserialize<Dto>(span, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? throw new InvalidDataException("rules.json ist leer.");
            return new RuleSet(dto);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"rules.json konnte nicht geladen werden: {ex.Message}", ex);
        }
    }

    static readonly Lazy<RuleSet> DefaultSet = new(() => Load(FindDefaultFile()));

    public static RuleSet Default => DefaultSet.Value;

    static string FindDefaultFile()
    {
        var dirs = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(RuleSet).Assembly.Location) ?? "" };
        foreach (var dir in dirs.Where(d => d.Length > 0).Distinct())
            foreach (var rel in new[] { "rules.json", Path.Combine("rules", "rules.json") })
            {
                var p = Path.Combine(dir, rel);
                if (File.Exists(p)) return p;
            }
        throw new FileNotFoundException("rules.json nicht gefunden (neben WinWaechter.Core.dll erwartet).");
    }

    public RuleVerdict Classify(ItemFacts f)
    {
        if (!string.IsNullOrWhiteSpace(f.ExePath)) f = f with { ExePath = PathNormalizer.NormalizePath(f.ExePath) };
        if (_protected.Any(m => m.IsMatch(f))) return RuleVerdict.Protected;
        if (IsMiner(f)) return RuleVerdict.Miner;
        if (_pua.Any(m => m.IsMatch(f))) return RuleVerdict.Pua;
        if (_expendable.Any(m => m.IsMatch(f))) return RuleVerdict.Expendable;
        return RuleVerdict.Unknown;
    }

    bool IsMiner(ItemFacts f)
    {
        var exeName = string.IsNullOrEmpty(f.ExePath) ? null : Path.GetFileName(f.ExePath);
        if (_minerNames.Any(n => WordStart(f.Name, n) || WordStart(exeName, n))) return true;
        return f.CommandLine is not null && IsMinerCommandLine(f.CommandLine);
    }

    public bool IsMinerCommandLine(string cmd) =>
        !string.IsNullOrEmpty(cmd) && _minerCmd.Any(s => cmd.Contains(s, StringComparison.OrdinalIgnoreCase));

    // True if needle occurs in text at the start of a word (anywhere if needle starts with a non-letter).
    static bool WordStart(string? text, string needle)
    {
        if (string.IsNullOrEmpty(text) || needle.Length == 0) return false;
        var plain = !char.IsLetterOrDigit(needle[0]);
        var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            if (plain || i == 0 || !char.IsLetterOrDigit(text[i - 1])) return true;
            i = i + 1 < text.Length ? text.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase) : -1;
        }
        return false;
    }
}
