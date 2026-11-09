using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Privacy;

public enum PrivacyState { On, Partial, Off, AlreadyMinimal }

/// <summary>One privacy setting with its state and the changes that would switch it off.</summary>
public sealed record PrivacyRow(PrivacySetting Setting, PrivacyState State, IReadOnlyList<RegValueChange> Changes);

public sealed class PrivacyScanner
{
    readonly IRegistry _reg;
    readonly RuleSet _rules;

    public PrivacyScanner(IRegistry reg, RuleSet rules) { _reg = reg; _rules = rules; }

    /// <param name="requirementMet">Tells whether an optional component (e.g. "recall") exists; rows needing a missing one are left out.</param>
    public IReadOnlyList<PrivacyRow> Scan(Func<string, bool> requirementMet)
    {
        var rows = new List<PrivacyRow>();
        foreach (var s in _rules.Privacy)
        {
            if (s.Requires is not null && !requirementMet(s.Requires)) continue;

            if (s.Effective is { } e
                && _reg.GetKind(e.Hive, e.Key, e.Name) == RegistryValueKind.DWord
                && _reg.Get(e.Hive, e.Key, e.Name) is { } live
                && Convert.ToInt32(live, CultureInfo.InvariantCulture) <= e.AtMost)
            {
                rows.Add(new PrivacyRow(s, PrivacyState.AlreadyMinimal, []));
                continue;
            }

            var open = s.Values.Where(v => !AtTarget(v)).ToList();
            var state = open.Count == 0 ? PrivacyState.Off
                : open.Count == s.Values.Count ? PrivacyState.On
                : PrivacyState.Partial;
            rows.Add(new PrivacyRow(s, state, open.Select(v => new RegValueChange(_reg, v, s.Title)).ToList()));
        }
        return rows;
    }

    bool AtTarget(PrivacyValue v) =>
        _reg.GetKind(v.Hive, v.Key, v.Name) == RegistryValueKind.DWord
        && _reg.Get(v.Hive, v.Key, v.Name) is { } x
        && Convert.ToInt32(x, CultureInfo.InvariantCulture) == v.Target;
}
