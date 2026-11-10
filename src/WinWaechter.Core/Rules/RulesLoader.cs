using WinWaechter.Core.Logging;
using WinWaechter.Core.Update;

namespace WinWaechter.Core.Rules;

public sealed record RulesInfo(int Version, bool FromUpdate, DateTime? StoredAtUtc)
{
    public string DisplayText => FromUpdate && StoredAtUtc is { } t
        ? $"Regeln: Version {Version} vom {t.ToLocalTime():dd.MM.yyyy}"
        : $"Regeln: Version {Version} (mitgeliefert)";
}

// Start-up choice of the effective rules: the bundled set, plus a verified newer cache unioned on top.
public static class RulesLoader
{
    public static (RuleSet Rules, RulesInfo Info) Load(RuleSet bundled, RulesStore store, SemVer app)
    {
        try
        {
            var cached = store.LoadVerified();
            if (cached is not null
                && cached.Rules.RulesVersion > bundled.RulesVersion
                && SemVer.TryParse(cached.Rules.MinAppVersion, out var min) && min <= app)
                return (bundled.UnionWith(cached.Rules), new RulesInfo(cached.Rules.RulesVersion, true, cached.StoredAtUtc));
        }
        catch (Exception e)
        {
            FileLog.Warn("Heruntergeladene Regeln konnten nicht geladen werden", e);
        }
        return (bundled, new RulesInfo(bundled.RulesVersion, false, null));
    }
}
