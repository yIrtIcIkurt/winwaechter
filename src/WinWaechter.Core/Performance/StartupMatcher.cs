using WinWaechter.Core.Model;
using WinWaechter.Core.Rules;

namespace WinWaechter.Core.Performance;

/// <summary>Result of matching a slow-start entry: a changeable Finding, or a note why there is none.</summary>
public sealed record StartupMatch(Finding? Finding, string? Note);

public static class StartupMatcher
{
    public const string NotChangeable = "Teil von Windows oder ein Treiber – nicht änderbar";
    public const string ProtectedNote = "Geschützt (z. B. Dell)";
    public const string AlreadyOff = "Startet bereits nicht mehr mit Windows";

    static bool IsStartupArea(FindingArea a) => a is FindingArea.Autostart or FindingArea.Aufgabe or FindingArea.Dienst;

    public static StartupMatch Match(BootDelay d, IReadOnlyList<Finding> startup, RuleSet rules)
    {
        var pool = startup.Where(f => IsStartupArea(f.Area)).ToList();

        var candidates = string.IsNullOrWhiteSpace(d.FileName)
            ? []
            : pool.Where(f => !string.IsNullOrWhiteSpace(f.ExePath)
                              && string.Equals(Path.GetFileName(f.ExePath), d.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
            candidates = pool.Where(f => string.Equals(f.Title, d.Name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (candidates.Count > 0)
        {
            var usable = candidates.FirstOrDefault(f => f.Change is not null && !f.IsProtectedVendor);
            if (usable is not null) return new StartupMatch(usable, null);
            if (candidates.Any(f => f.IsProtectedVendor)) return new StartupMatch(null, ProtectedNote);
            return new StartupMatch(null, AlreadyOff);
        }

        var isProtected = rules.Classify(new ItemFacts(d.Name, null, null, null)) == RuleVerdict.Protected
                          || (!string.IsNullOrWhiteSpace(d.FileName)
                              && rules.Classify(new ItemFacts(d.FileName, null, null, null)) == RuleVerdict.Protected);
        return new StartupMatch(null, isProtected ? ProtectedNote : NotChangeable);
    }
}
