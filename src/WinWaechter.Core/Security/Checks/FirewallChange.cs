using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Security.Checks;

/// <summary>Turns one firewall profile on; Undo restores the previous effective value.</summary>
public sealed class FirewallChange : IChange
{
    readonly IFirewall _fw;
    readonly FirewallProfile _profile;

    public FirewallChange(IFirewall fw, FirewallProfile profile)
    {
        _fw = fw;
        _profile = profile;
    }

    public string Kind => "firewall";
    public string Target => $"Firewall {ProfileName(_profile)}";
    public string Description => $"Firewall für das Profil „{ProfileName(_profile)}“ einschalten";

    public static string ProfileName(FirewallProfile p) => p switch
    {
        FirewallProfile.Domain => "Domäne",
        FirewallProfile.Private => "Privat",
        _ => "Öffentlich",
    };

    public static FirewallChange FromRecord(ChangeRecord r, IFirewall fw)
    {
        if (!r.Before.TryGetValue("profile", out var s) || !int.TryParse(s, out var n) || n is not (1 or 2 or 4))
            throw new InvalidOperationException("Unbekanntes Firewall-Profil im Eintrag – kann nicht rückgängig gemacht werden.");
        ParseEnabled(r);
        return new FirewallChange(fw, (FirewallProfile)n);
    }

    public static void Register(ChangeFactory f, IFirewall fw) => f.Register("firewall", r => FromRecord(r, fw));

    public ChangeRecord Apply(SessionContext ctx)
    {
        var before = _fw.IsEnabled(_profile);
        _fw.SetEnabled(_profile, true);
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            new() { ["profile"] = ((int)_profile).ToString(), ["enabled"] = before ? "true" : "false" },
            new() { ["enabled"] = "true" }, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() =>
        (new() { ["profile"] = ((int)_profile).ToString(), ["enabled"] = "false" }, new() { ["enabled"] = "true" });

    // Before["enabled"] must be exactly "true" or "false"; anything else must never switch the firewall off.
    static bool ParseEnabled(ChangeRecord r) =>
        r.Before.GetValueOrDefault("enabled") switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException("Der gespeicherte Firewall-Zustand ist ungültig – kann nicht rückgängig gemacht werden."),
        };

    public void Undo(ChangeRecord record, SessionContext ctx) => _fw.SetEnabled(_profile, ParseEnabled(record));

    public DriftState CheckDrift(ChangeRecord record)
    {
        var expected = !string.Equals(record.After.GetValueOrDefault("enabled"), "false", StringComparison.OrdinalIgnoreCase);
        return _fw.IsEnabled(_profile) == expected ? DriftState.Unchanged : DriftState.Changed;
    }
}
