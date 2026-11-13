using WinWaechter.Core.Logging;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Security.Heuristics;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

public sealed record SecuritySources(IFirewall Firewall, ISecurityCenter Center, IDefender Defender, IRegistry Registry,
    IVolumeEncryption Encryption, IWindowsUpdateSearch Updates, IListenerSource Listeners, IProcessList Processes,
    ISignatureVerifier Signatures, BrowserExtensionScanner Extensions, ExtensionPageOpener? ExtensionPages = null, Func<IPolicyOwnership>? Policies = null,
    IFirewallRules? FirewallRules = null, RuleSet? Rules = null, string? WindowsDir = null,
    SecurityActionSet? Actions = null);

/// <summary>
/// Runs every check off the calling thread, each with its own timeout. A failing or hanging source only
/// turns its own rows grey; the result always has all rows in <see cref="SecurityIds"/> order.
/// </summary>
public sealed class SecurityCheck(SecuritySources s, TimeSpan? sourceTimeout = null)
{
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan UpdateSearchTimeout = TimeSpan.FromSeconds(30);
    const string TimeoutDetail = "Zeitüberschreitung";

    static readonly FirewallProfile[] Profiles = [FirewallProfile.Domain, FirewallProfile.Private, FirewallProfile.Public];
    static readonly string[] FirewallIds = [SecurityIds.FirewallDomain, SecurityIds.FirewallPrivate, SecurityIds.FirewallPublic];

    readonly TimeSpan _timeout = sourceTimeout ?? SourceTimeout;

    public async Task<IReadOnlyList<SecurityItem>> RunAsync(CancellationToken ct)
    {
        // Security center failure counts as "no third-party products".
        var antiVirus = Guard<IReadOnlyList<SecurityProduct>>("Sicherheitscenter (Virenschutz)", () => s.Center.AntiVirus(), _ => [], ct);
        var firewalls = Guard<IReadOnlyList<SecurityProduct>>("Sicherheitscenter (Firewall)", () => s.Center.Firewalls(), _ => [], ct);

        var smartScreen = One(SecurityIds.SmartScreen, SecurityTitles.SmartScreen, () => SecurityChecks.SmartScreen(s.Registry, s.Policies?.Invoke()), ct);
        var uac = One(SecurityIds.Uac, SecurityTitles.Uac, () => SecurityChecks.Uac(s.Registry), ct);
        var rdp = One(SecurityIds.RemoteDesktop, SecurityTitles.RemoteDesktop, () => SecurityChecks.RemoteDesktop(s.Registry), ct);
        var encryption = One(SecurityIds.Encryption, SecurityTitles.Encryption, () => SecurityChecks.Encryption(s.Encryption.SystemDrive()), ct);
        var updates = One(SecurityIds.Updates, SecurityTitles.Updates, () => SecurityChecks.Updates(
            s.Registry.KeyExists(Hive.LocalMachine, SecurityChecks.RebootRequiredKey), s.Updates.Search(UpdateSearchTimeout), s.Actions), ct);
        var listeners = One(SecurityIds.Listeners, SecurityTitles.Listeners,
            () => SecurityChecks.Listeners(s.Listeners.Listening(), s.Processes.Snapshot(), s.Signatures,
                s.FirewallRules, s.Rules, s.WindowsDir), ct);
        var extensions = One(SecurityIds.Extensions, SecurityTitles.Extensions, () => SecurityChecks.Extensions(s.Extensions.Scan(), s.ExtensionPages is { } pages ? pages.CanOpen : null), ct);

        // Raw reads start right away, each with its own timeout; the center answer is applied afterwards.
        var defenderRead = Guard("Defender", async () => Outcome<DefenderStatus>.Of(await s.Defender.GetStatusAsync().ConfigureAwait(false)),
            Outcome<DefenderStatus>.Failed, ct);
        var firewallRead = Guard("Firewall", () => Outcome<IReadOnlyDictionary<FirewallProfile, bool>>.Of(
            Profiles.ToDictionary(p => p, s.Firewall.IsEnabled)), Outcome<IReadOnlyDictionary<FirewallProfile, bool>>.Failed, ct);

        var av = await antiVirus.ConfigureAwait(false);
        var status = await defenderRead.ConfigureAwait(false);
        var defender = status.Ok
            ? SecurityChecks.Defender(status.Value!, av, s.Actions)
            : SecurityChecks.Unavailable(SecurityIds.Defender, SecurityTitles.Defender, status.Detail);

        var fwProducts = await firewalls.ConfigureAwait(false);
        var states = await firewallRead.ConfigureAwait(false);
        // under a third-party takeover the profile states are not needed, so a failed read stays green
        var takenOver = fwProducts.Any(f => f.Enabled && !f.IsMicrosoft);
        var firewall = states.Ok || takenOver
            ? SecurityChecks.Firewall(s.Firewall, fwProducts, states.Value)
            : Profiles.Select((p, i) => SecurityChecks.Unavailable(FirewallIds[i], SecurityTitles.Firewall(p), states.Detail)).ToList();

        var rows = new List<SecurityItem>(firewall) { defender };
        foreach (var t in new[] { smartScreen, uac, rdp, encryption, updates, listeners, extensions })
            rows.Add(await t.ConfigureAwait(false));
        return rows;
    }

    Task<SecurityItem> One(string id, string title, Func<SecurityItem> work, CancellationToken ct) =>
        Guard(title, work, d => SecurityChecks.Unavailable(id, title, d), ct);

    Task<T> Guard<T>(string what, Func<T> work, Func<string?, T> fallback, CancellationToken ct) =>
        Guard(what, () => Task.FromResult(work()), fallback, ct);

    // Runs work on the thread pool; exception → fallback(null), timeout → fallback("Zeitüberschreitung").
    // Only cancellation of ct is passed on to the caller.
    async Task<T> Guard<T>(string what, Func<Task<T>> work, Func<string?, T> fallback, CancellationToken ct)
    {
        var task = Task.Run(work, CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = await Task.WhenAny(task, Task.Delay(_timeout, cts.Token)).ConfigureAwait(false);
        cts.Cancel();
        if (done != task)
        {
            // A late failure must not surface as an unobserved task exception.
            _ = task.ContinueWith(t => FileLog.Warn($"Sicherheitsprüfung „{what}“ nach Zeitüberschreitung fehlgeschlagen", t.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
            FileLog.Warn($"Sicherheitsprüfung „{what}“: Zeitüberschreitung");
            return fallback(TimeoutDetail);
        }
        try { return await task.ConfigureAwait(false); }
        catch (Exception ex)
        {
            FileLog.Warn($"Sicherheitsprüfung „{what}“ nicht möglich", ex);
            return fallback(null);
        }
    }

    // Result of a guarded raw read: the value, or the reason it is missing (null = error, else e.g. "Zeitüberschreitung").
    sealed record Outcome<T>(bool Ok, T? Value, string? Detail)
    {
        public static Outcome<T> Of(T value) => new(true, value, null);
        public static Outcome<T> Failed(string? detail) => new(false, default, detail);
    }
}
