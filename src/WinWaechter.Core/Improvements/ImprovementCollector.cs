using System.Globalization;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Security.Checks;

namespace WinWaechter.Core.Improvements;

public enum ImprovementArea { Aufraeumen, AutostartHintergrund, DatenschutzWerbung, Sicherheit, Leistung }

public static class ImprovementAreas
{
    public static string Title(ImprovementArea a) => a switch
    {
        ImprovementArea.Aufraeumen => "Aufräumen",
        ImprovementArea.AutostartHintergrund => "Autostart & Hintergrund",
        ImprovementArea.DatenschutzWerbung => "Datenschutz & Werbung",
        ImprovementArea.Sicherheit => "Sicherheit",
        ImprovementArea.Leistung => "Leistung",
        _ => a.ToString(),
    };
}

/// <summary>One recommended improvement: reversible changes only. RunFirst: runs as its own step before the session (Computerschutz).</summary>
public sealed record Improvement(string Id, ImprovementArea Area, string Title, string Why,
    IReadOnlyList<IChange> Changes, bool RunFirst = false);

/// <summary>A recommended one-off action without undo; never part of the collect button.</summary>
public sealed record ActionSuggestion(string Id, string Title, string Why, SystemAction Action);

public sealed record ImprovementScan(IReadOnlyList<Improvement> Items, IReadOnlyList<ActionSuggestion> Actions,
    IReadOnlyList<string> Unchecked, DateTimeOffset At);

public sealed record ImprovementSource(string Name,
    Func<CancellationToken, Task<(IReadOnlyList<Improvement> Items, IReadOnlyList<ActionSuggestion> Actions)>> ReadAsync);

/// <summary>Runs the area sources and merges what they recommend into one list plus separate action suggestions.</summary>
public sealed class ImprovementCollector(IReadOnlyList<ImprovementSource> sources)
{
    static readonly StringComparer German = StringComparer.Create(new CultureInfo("de-DE"), true);

    // The same registry value can come from privacy, performance and security: one key space for those kinds.
    static string KeyOf(IChange c) =>
        c.Kind is "regvalue" or "perfvalue" or "secvalue"
            ? "reg|" + c.Target.Trim().ToUpperInvariant()
            : c.Kind + "|" + c.Target;

    public async Task<ImprovementScan> CollectAsync(IProgress<string>? p, CancellationToken ct)
    {
        var items = new List<Improvement>();
        var actions = new List<ActionSuggestion>();
        var notChecked = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenActions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            p?.Report($"Prüfe {source.Name} …");
            try
            {
                var (i, a) = await source.ReadAsync(ct).ConfigureAwait(false);
                foreach (var item in i)
                {
                    // Per change, first wins: drop what an earlier item (or this one) already offers.
                    var kept = new List<IChange>();
                    var keys = new List<string>();
                    foreach (var c in item.Changes)
                    {
                        var key = KeyOf(c);
                        if (seen.Contains(key) || keys.Contains(key)) continue;
                        keys.Add(key);
                        kept.Add(c);
                    }
                    if (kept.Count == 0) continue;
                    foreach (var k in keys) seen.Add(k);
                    items.Add(kept.Count == item.Changes.Count ? item : item with { Changes = kept });
                }
                foreach (var act in a)
                    if (seenActions.Add(act.Id)) actions.Add(act);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Warn($"Empfehlungen: {source.Name} nicht prüfbar", ex);
                notChecked.Add($"{source.Name}: nicht prüfbar");
            }
        }

        var ordered = items.OrderBy(x => x.Area).ThenBy(x => x.Title, German).ToList();
        return new ImprovementScan(ordered, actions, notChecked, DateTimeOffset.Now);
    }
}

/// <summary>Turns the recommendations of each area into improvements; only entries that carry a change qualify.</summary>
public static class ImprovementMap
{
    public static IReadOnlyList<Improvement> FromFindings(IEnumerable<Finding> findings)
    {
        var list = new List<Improvement>();
        foreach (var f in findings)
        {
            if (!f.Preselected || f.Change is null || f.IsProtectedVendor) continue;
            ImprovementArea? area = f.Area switch
            {
                FindingArea.Speicher => ImprovementArea.Aufraeumen,
                FindingArea.Autostart or FindingArea.Dienst or FindingArea.Aufgabe => ImprovementArea.AutostartHintergrund,
                FindingArea.Prozess or FindingArea.Verbindung => ImprovementArea.Sicherheit,
                _ => null,
            };
            if (area is null) continue;
            list.Add(new Improvement(f.Id, area.Value, f.Title, f.Reasons.FirstOrDefault() ?? "", [f.Change]));
        }
        return list;
    }

    public static IReadOnlyList<Improvement> FromPrivacy(IEnumerable<PrivacyRow> rows) =>
        rows.Where(r => r.Setting.Recommended && r.Changes.Count > 0)
            .Select(r => new Improvement("privacy:" + r.Setting.Id, ImprovementArea.DatenschutzWerbung,
                r.Setting.Title, r.Setting.Explanation, r.Changes.Cast<IChange>().ToList()))
            .ToList();

    public static IReadOnlyList<Improvement> FromPerformance(IEnumerable<PerfSetting> settings,
        Func<PerfSetting, int, IReadOnlyList<IChange>> changesFor)
    {
        var list = new List<Improvement>();
        foreach (var s in settings)
        {
            if (!s.Available || s.Recommended is not { } r || r == s.Current) continue;
            var changes = changesFor(s, r);
            if (changes.Count == 0) continue;
            var area = s.Group is "background" or "browser-background" ? ImprovementArea.AutostartHintergrund : ImprovementArea.Leistung;
            list.Add(new Improvement("perf:" + s.Id, area, s.Title, s.Explanation, changes));
        }
        return list;
    }

    public static (IReadOnlyList<Improvement> Items, IReadOnlyList<ActionSuggestion> Actions) FromSecurity(IEnumerable<SecurityItem> items)
    {
        var improvements = new List<Improvement>();
        var actions = new List<ActionSuggestion>();
        Walk(items);
        return (improvements, actions);

        void Walk(IEnumerable<SecurityItem> level)
        {
            foreach (var it in level)
            {
                if (it.Recommended && it.Change is { } change)
                    improvements.Add(new Improvement("sec:" + it.Id, ImprovementArea.Sicherheit, it.Title, it.Explanation,
                        [change], RunFirst: change.Kind == "sysprotect"));
                if (it.Act is { } act && it.Light != Light.Green)
                    actions.Add(new ActionSuggestion(it.Id, it.Title, it.Explanation, act));
                if (it.Children is { Count: > 0 }) Walk(it.Children);
            }
        }
    }

    public static IReadOnlyList<Improvement> FromBloatApps(IEnumerable<BloatAppRow> rows, bool allowed) =>
        !allowed
            ? []
            : rows.Where(r => r.Rule.Recommended)
                .Select(r => new Improvement("bloat:" + r.Package.FamilyName, ImprovementArea.Aufraeumen, r.Rule.Title,
                    "Vorinstalliertes Programm, das Sie vermutlich nicht brauchen.", [r.Change]))
                .ToList();
}
