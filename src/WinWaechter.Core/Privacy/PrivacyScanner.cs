using System.Globalization;
using Microsoft.Win32;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Privacy;

public enum PrivacyState { On, Partial, Off, AlreadyMinimal }

/// <summary>One privacy setting with its state and the changes that would switch it off.
/// Note: e.g. „Von einer Richtlinie festgelegt“ when a foreign policy value is left alone.</summary>
public sealed record PrivacyRow(PrivacySetting Setting, PrivacyState State, IReadOnlyList<RegValueChange> Changes, string? Note = null);

public sealed class PrivacyScanner
{
    readonly IRegistry _reg;
    readonly RuleSet _rules;
    readonly IPolicyOwnership? _policies;

    /// <param name="policies">Who set a present SOFTWARE\Policies value. Without it (or when it fails) every present
    /// policy value counts as foreign and is not offered (the policy rule, <see cref="PolicyValues"/>).</param>
    public PrivacyScanner(IRegistry reg, RuleSet rules, IPolicyOwnership? policies = null)
    {
        _reg = reg; _rules = rules; _policies = policies;
    }

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
            // a policy someone else set is never overwritten
            var foreign = open.Where(IsForeignPolicy).ToList();
            rows.Add(new PrivacyRow(s, state,
                open.Except(foreign).Select(v => new RegValueChange(_reg, v, s.Title)).ToList(),
                foreign.Count > 0 ? PolicyValues.ForeignText : null));
        }
        return rows;
    }

    bool IsForeignPolicy(PrivacyValue v)
    {
        if (!PolicyValues.IsPolicyKey(v.Key) || _reg.GetKind(v.Hive, v.Key, v.Name) is null) return false;
        try { return _policies?.IsOurs(v.Hive, v.Key, v.Name) != true; }
        catch (Exception) { return true; }   // unreadable journal: foreign (fail closed)
    }

    bool AtTarget(PrivacyValue v) =>
        _reg.GetKind(v.Hive, v.Key, v.Name) == RegistryValueKind.DWord
        && _reg.Get(v.Hive, v.Key, v.Name) is { } x
        && Convert.ToInt32(x, CultureInfo.InvariantCulture) == v.Target;
}
