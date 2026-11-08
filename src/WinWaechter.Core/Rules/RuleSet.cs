using System.Text.Json;

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
        public bool IsMatch(ItemFacts f) =>
            (Name is not null || Publisher is not null || Path is not null)
            && (Name is null || WordStart(f.Name, Name))
            && (Publisher is null || WordStart(f.Publisher, Publisher))
            && (Path is null || (f.ExePath?.Contains(Path, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    sealed class Dto
    {
        public List<MatcherDto>? Protected { get; set; }
        public List<MatcherDto>? ExpendableAutostart { get; set; }
        public List<MatcherDto>? Pua { get; set; }
        public List<string>? MinerNames { get; set; }
        public List<string>? MinerCommandLine { get; set; }
        public List<int>? PoolPorts { get; set; }
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

    RuleSet(Dto d)
    {
        // The protected list is the safety core: refuse to run without it.
        if (d.Protected is null || d.Protected.Count == 0)
            throw new InvalidDataException("rules.json enthält keine Schutzliste (\"protected\").");
        _protected = ToMatchers(d.Protected);
        _expendable = ToMatchers(d.ExpendableAutostart);
        _pua = ToMatchers(d.Pua);
        _minerNames = (d.MinerNames ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        _minerCmd = (d.MinerCommandLine ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        PoolPorts = new HashSet<int>(d.PoolPorts ?? []);
    }

    static Matcher[] ToMatchers(List<MatcherDto>? list) =>
        (list ?? []).Select(m => new Matcher(Norm(m.Name), Norm(m.Publisher), Norm(m.Path))).ToArray();

    static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static RuleSet Load(string jsonPath)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            var dto = JsonSerializer.Deserialize<Dto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? throw new InvalidDataException("rules.json ist leer.");
            return new RuleSet(dto);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"rules.json konnte nicht geladen werden ({jsonPath}): {ex.Message}", ex);
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
