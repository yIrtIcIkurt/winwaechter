using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Sys;

public enum PolicyState { None, Ours, Foreign }

/// <summary>Whether WinWächter itself set exactly this policy value.</summary>
public interface IPolicyOwnership
{
    bool IsOurs(Hive h, string key, string name);
}

/// <summary>
/// The policy rule: only values under SOFTWARE\Policies\… (HKLM or HKCU) count as policy.
/// A present policy value is ours when the journal says so, otherwise it is foreign and no fix is offered.
/// </summary>
public static class PolicyValues
{
    public const string ForeignText = "Von einer Richtlinie festgelegt";

    const string Prefix = @"SOFTWARE\Policies\";

    /// <summary>True for SOFTWARE\Policies\…; …\CurrentVersion\Policies\System is not a policy key.</summary>
    public static bool IsPolicyKey(string key) =>
        key.Trim('\\').StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>None for a non-policy key or a missing value; Ours when the journal says WinWächter set it; else Foreign.</summary>
    public static PolicyState State(IRegistry reg, IPolicyOwnership own, Hive h, string key, string name)
    {
        if (!IsPolicyKey(key) || reg.GetKind(h, key, name) is null) return PolicyState.None;
        return own.IsOurs(h, key, name) ? PolicyState.Ours : PolicyState.Foreign;
    }
}

/// <summary>
/// Ownership from the journals: for each value the newest perfvalue/secvalue/regvalue record (by At)
/// decides, and only Applied means ours. The journals are read once per instance, on first use.
/// </summary>
public sealed class JournalPolicyOwnership(SessionStore store) : IPolicyOwnership
{
    static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "perfvalue", "secvalue", "regvalue" };

    readonly Lazy<HashSet<(Hive, string, string)>> _ours = new(() => Load(store));

    public bool IsOurs(Hive h, string key, string name) => _ours.Value.Contains((h, NormKey(key), name.ToUpperInvariant()));

    static HashSet<(Hive, string, string)> Load(SessionStore store)
    {
        var newest = new Dictionary<(Hive, string, string), ChangeRecord>();
        foreach (var r in store.List().SelectMany(s => s.Records))
        {
            if (!Kinds.Contains(r.Kind)) continue;
            Hive? hive = r.Before.GetValueOrDefault("hive") switch
            {
                "HKCU" => Hive.CurrentUser,
                "HKLM" => Hive.LocalMachine,
                _ => null,
            };
            var key = r.Before.GetValueOrDefault("key");
            var name = r.Before.GetValueOrDefault("name");
            if (hive is null || key is null || name is null) continue;
            var id = (hive.Value, NormKey(key), name.ToUpperInvariant());
            if (!newest.TryGetValue(id, out var seen) || r.At > seen.At) newest[id] = r;
        }
        return newest.Where(p => p.Value.Status == ChangeStatus.Applied).Select(p => p.Key).ToHashSet();
    }

    static string NormKey(string key) => key.Trim('\\').ToUpperInvariant();
}
