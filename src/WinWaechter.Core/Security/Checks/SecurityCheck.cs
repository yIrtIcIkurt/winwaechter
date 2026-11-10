using WinWaechter.Core.Logging;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Security.Heuristics;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

public sealed record SecuritySources(IFirewall Firewall, ISecurityCenter Center, IDefender Defender, IRegistry Registry,
    IVolumeEncryption Encryption, IWindowsUpdateSearch Updates, IListenerSource Listeners, IProcessList Processes,
    ISignatureVerifier Signatures, BrowserExtensionScanner Extensions);

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

        var smartScreen = One(SecurityIds.SmartScreen, Titles.SmartScreen, () => SecurityChecks.SmartScreen(s.Registry), ct);
        var uac = One(SecurityIds.Uac, Titles.Uac, () => SecurityChecks.Uac(s.Registry), ct);
        var rdp = One(SecurityIds.RemoteDesktop, Titles.RemoteDesktop, () => SecurityChecks.RemoteDesktop(s.Registry), ct);
        var encryption = One(SecurityIds.Encryption, Titles.Encryption, () => SecurityChecks.Encryption(s.Encryption.SystemDrive()), ct);
        var updates = One(SecurityIds.Updates, Titles.Updates, () => SecurityChecks.Updates(
            s.Registry.KeyExists(Hive.LocalMachine, SecurityChecks.RebootRequiredKey), s.Updates.Search(UpdateSearchTimeout)), ct);
        var listeners = One(SecurityIds.Listeners, Titles.Listeners,
            () => SecurityChecks.Listeners(s.Listeners.Listening(), s.Processes.Snapshot(), s.Signatures), ct);
        var extensions = One(SecurityIds.Extensions, Titles.Extensions, () => SecurityChecks.Extensions(s.Extensions.Scan()), ct);

        // These two need the security-center answer; their own timeout starts once it is there.
        var av = await antiVirus.ConfigureAwait(false);
        var defender = Guard(Titles.Defender, async () => SecurityChecks.Defender(await s.Defender.GetStatusAsync().ConfigureAwait(false), av),
            d => SecurityChecks.Unavailable(SecurityIds.Defender, Titles.Defender, d), ct);
        var fwProducts = await firewalls.ConfigureAwait(false);
        var firewall = Guard<IReadOnlyList<SecurityItem>>("Firewall", () => SecurityChecks.Firewall(s.Firewall, fwProducts),
            d => Profiles.Select((p, i) => SecurityChecks.Unavailable(FirewallIds[i], Titles.Firewall(p), d)).ToList(), ct);

        var rows = new List<SecurityItem>(await firewall.ConfigureAwait(false));
        foreach (var t in new[] { defender, smartScreen, uac, rdp, encryption, updates, listeners, extensions })
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
}
