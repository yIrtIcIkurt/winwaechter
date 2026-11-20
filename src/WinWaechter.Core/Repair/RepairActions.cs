using System.Text.RegularExpressions;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Repair;

/// <summary>
/// The repair tools of the Reparatur page, each a one-off action with fixed programs from System32 and fixed
/// arguments. Long ones (system files, disk scan) run outside the change gate with their own lock.
/// </summary>
public sealed partial class RepairActions(
    IProcessRunner run,
    IServiceControl services,
    IRepairFiles files,
    INetworkConfig network,
    Func<Task<bool>> ensureRestorePoint,
    IProcessEnder ender,
    string windowsDir,
    string localAppData,
    DateTime today,
    TimeSpan? explorerPoll = null,
    TimeSpan? explorerTimeout = null)
{
    string Sys32(string exe) => Path.Combine(windowsDir, "System32", exe);

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d)?)\s?%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"^(SoftwareDistribution|catroot2)\.bak-\d{8}(-\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnBackup();

    // sfc writes UTF-16; read as one-byte text every character is followed by NUL
    static string Clean(string s) => s.Replace("\0", "").ToLowerInvariant();

    // ---- 1. system files ----

    public SystemAction SystemFiles() =>
        new("repair-sfc", "Systemdateien prüfen und reparieren", "Prüfen und reparieren",
            "Prüft die Windows-Systemdateien und repariert beschädigte. Dauer meist 10–30 Minuten, Internet nötig (Ersatzdateien kommen über Windows Update).",
            false, async (p, ct) =>
            {
                p?.Report("Systemdateien werden repariert …");
                var dism = await run.RunAsync(Sys32("dism.exe"), "/Online /Cleanup-Image /RestoreHealth",
                    line => ReportPercent(p, "Systemdateien werden repariert …", line), CancellationToken.None).ConfigureAwait(false);
                FileLog.Info($"DISM RestoreHealth: Code {dism.Exit}");
                ct.ThrowIfCancellationRequested();   // only between the two programs
                p?.Report("Systemdateien werden geprüft …");
                var sfc = await run.RunAsync(Sys32("sfc.exe"), "/scannow",
                    line => ReportPercent(p, "Systemdateien werden geprüft …", line.Replace("\0", "")), CancellationToken.None).ConfigureAwait(false);
                var text = Clean(sfc.StdOut);
                FileLog.Info("sfc: " + sfc.StdOut.Replace("\0", "").Trim());
                var (ok, message) = SfcResult(text);
                if (dism.Exit != 0)
                    return DismFailed(dism.Exit, message);
                return new ActionOutcome(ok, message);
            })
        { SkipGate = true, LockKey = "repair", Stamp = true };

    /// <summary>
    /// The component store could not be repaired because Windows Update had no repair files (0x800F0915 content missing,
    /// 0x800F081F source not found, 0x800F0906 download failed). Repeating the repair does not help; Windows' own
    /// „Jetzt neu installieren“ (keeps files and programs) does.
    /// </summary>
    public static bool IsRepairContentMissing(int dismExit) =>
        unchecked((uint)dismExit) is 0x800F0915 or 0x800F081F or 0x800F0906;

    public static ActionOutcome DismFailed(int exit, string sfcMessage)
    {
        var code = $"0x{unchecked((uint)exit):X8}";
        if (IsRepairContentMissing(exit))
            return new ActionOutcome(false,
                $"Windows konnte die Ersatzdateien für die Reparatur nicht laden ({code}). sfc wurde trotzdem ausgeführt: {sfcMessage} "
                + "Abhilfe: Windows über „Jetzt neu installieren“ reparieren; Dateien und Programme bleiben dabei erhalten.",
                SystemLinks.Recovery);
        return new ActionOutcome(false, $"DISM ist fehlgeschlagen ({code}). sfc wurde trotzdem ausgeführt: {sfcMessage}");
    }

    (bool Ok, string Message) SfcResult(string text)
    {
        if (text.Contains("keine integritätsverletzungen") || text.Contains("did not find any integrity violations"))
            return (true, "Keine Fehler gefunden.");
        if (text.Contains("nicht reparieren") || text.Contains("unable to fix"))
            return (false, $@"Einige Fehler ließen sich nicht reparieren. Details: {Path.Combine(windowsDir, "Logs", "CBS", "CBS.log")}");
        if (text.Contains("erfolgreich repariert") || text.Contains("successfully repaired"))
            return (true, "Fehler gefunden und repariert. Ein Neustart wird empfohlen.");
        return (false, "Die Prüfung ließ sich nicht abschließen. Details stehen im Protokoll.");
    }

    static void ReportPercent(IProgress<string>? p, string prefix, string line)
    {
        var m = Percent().Match(line);
        if (m.Success) p?.Report($"{prefix} {m.Groups[1].Value} %");
    }

    // ---- 2. Windows Update ----

    public SystemAction WindowsUpdate() =>
        new("repair-wu", "Windows Update reparieren", "Reparieren",
            "Die Update-Dienste werden angehalten, die Update-Zwischenspeicher umbenannt (als Sicherung behalten) und die Dienste wieder gestartet. "
            + "Der Update-Verlauf in den Einstellungen erscheint danach leer; installierte Updates bleiben.",
            true, async (p, ct) =>
            {
                if (!await ensureRestorePoint().ConfigureAwait(false)) return new ActionOutcome(false, "Abgebrochen.");
                var names = new[] { "wuauserv", "bits", "cryptsvc" };
                var wasRunning = names.Where(n => services.Get(n)?.Running == true).ToList();
                string? error = null;
                try
                {
                    p?.Report("Update-Dienste werden angehalten …");
                    foreach (var n in wasRunning) await Net("stop", n).ConfigureAwait(false);
                    p?.Report("Zwischenspeicher werden umbenannt …");
                    foreach (var dir in new[] { Path.Combine(windowsDir, "SoftwareDistribution"), Path.Combine(windowsDir, "System32", "catroot2") })
                        RenameKeepingOne(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    FileLog.Warn("Windows Update reparieren: Umbenennen fehlgeschlagen", ex);
                    error = ex.Message;
                }
                finally
                {
                    p?.Report("Update-Dienste werden gestartet …");
                    foreach (var n in wasRunning)
                    {
                        try { await Net("start", n).ConfigureAwait(false); }
                        catch (Exception ex) { FileLog.Warn($"Dienst {n} ließ sich nicht starten", ex); }
                    }
                }
                return error is null
                    ? new ActionOutcome(true, "Windows Update wurde zurückgesetzt. Suchen Sie danach erneut nach Updates.")
                    : new ActionOutcome(false, "Windows Update ließ sich nicht vollständig zurücksetzen: " + error);
            })
        { LockKey = "repair", Stamp = true };

    void RenameKeepingOne(string dir)
    {
        if (!files.DirectoryExists(dir)) return;
        var parent = Path.GetDirectoryName(dir)!;
        var name = Path.GetFileName(dir);
        // older backups of WinWächter (exactly this pattern) go; anything else stays untouched
        foreach (var old in files.Directories(parent))
        {
            var oldName = Path.GetFileName(old);
            if (oldName.StartsWith(name + ".bak-", StringComparison.OrdinalIgnoreCase) && OwnBackup().IsMatch(oldName)
                && !oldName.StartsWith($"{name}.bak-{today:yyyyMMdd}", StringComparison.OrdinalIgnoreCase))
                files.DeleteDirectory(old);
        }
        var target = Path.Combine(parent, $"{name}.bak-{today:yyyyMMdd}");
        for (var i = 2; files.DirectoryExists(target); i++) target = Path.Combine(parent, $"{name}.bak-{today:yyyyMMdd}-{i}");
        files.MoveDirectory(dir, target);
    }

    async Task Net(string verb, string service)
    {
        var (exit, _, err) = await run.RunAsync(Sys32("net.exe"), $"{verb} {service}", CancellationToken.None).ConfigureAwait(false);
        // 2 = already in that state
        if (exit is not (0 or 2)) throw new InvalidOperationException($"Dienst {service}: {verb} fehlgeschlagen ({exit}) {err.Trim()}");
    }

    // ---- 3. network ----

    public SystemAction FlushDns() =>
        new("repair-dns", "DNS-Cache leeren", "Leeren", null, false, async (_, _) =>
        {
            var (exit, _, _) = await run.RunAsync(Sys32("ipconfig.exe"), "/flushdns", CancellationToken.None).ConfigureAwait(false);
            return exit == 0 ? new ActionOutcome(true, "Der DNS-Cache wurde geleert.") : new ActionOutcome(false, $"Fehlgeschlagen (Code {exit}).");
        })
        { LockKey = "repair", Stamp = true };

    public SystemAction RenewIp() =>
        new("repair-renew", "Verbindung neu aufbauen", "Neu aufbauen", "Die Verbindung ist kurz weg.", false, async (p, _) =>
        {
            p?.Report("Verbindung wird getrennt …");
            await run.RunAsync(Sys32("ipconfig.exe"), "/release", CancellationToken.None).ConfigureAwait(false);
            p?.Report("Verbindung wird neu aufgebaut …");
            var (exit, _, _) = await run.RunAsync(Sys32("ipconfig.exe"), "/renew", CancellationToken.None).ConfigureAwait(false);
            return exit == 0
                ? new ActionOutcome(true, "Die Verbindung wurde neu aufgebaut.")
                : new ActionOutcome(false, "Die Verbindung ließ sich nicht neu aufbauen. Prüfen Sie Kabel oder WLAN.");
        })
        { LockKey = "repair", Stamp = true };

    public SystemAction NetworkReset()
    {
        var items = new List<string>();
        foreach (var a in network.Read())
        {
            if (a.StaticIp && a.Ips.Count > 0) items.Add($"{a.Name}: IP {string.Join(", ", a.Ips)}");
            if (a.StaticDns && a.Dns.Count > 0) items.Add($"{a.Name}: DNS {string.Join(", ", a.Dns)}");
        }
        var confirm = "Von Hand eingetragene IP-Adressen und DNS-Server werden gelöscht. VPN- und Proxy-Einstellungen müssen danach eventuell neu eingerichtet werden. "
                      + "Gespeicherte WLAN-Passwörter bleiben. Danach ist ein Neustart nötig."
                      + (items.Count > 0 ? " Notieren Sie diese Werte vorher:" : "");
        return new SystemAction("repair-net", "Netzwerk komplett zurücksetzen", "Zurücksetzen", confirm, true, async (p, _) =>
        {
            if (!await ensureRestorePoint().ConfigureAwait(false)) return new ActionOutcome(false, "Abgebrochen.");
            p?.Report("Netzwerk wird zurückgesetzt …");
            var a = await run.RunAsync(Sys32("netsh.exe"), "winsock reset", CancellationToken.None).ConfigureAwait(false);
            var b = await run.RunAsync(Sys32("netsh.exe"), "int ip reset", CancellationToken.None).ConfigureAwait(false);
            return a.Exit == 0 && b.Exit == 0
                ? new ActionOutcome(true, "Netzwerk zurückgesetzt. Bitte starten Sie den Rechner neu.")
                : new ActionOutcome(false, $"Das Zurücksetzen ist teilweise fehlgeschlagen (Code {a.Exit}/{b.Exit}). Details im Protokoll.");
        }, items) { LockKey = "repair", Stamp = true };
    }

    // ---- 4. print queue ----

    string SpoolDir => Path.Combine(windowsDir, "System32", "spool", "PRINTERS");

    public bool PrintQueueEmpty => !files.Files(SpoolDir, "*").Any();

    /// <summary>Null when nothing waits in the queue.</summary>
    public SystemAction? PrintQueue() => PrintQueueEmpty
        ? null
        : new SystemAction("repair-spool", "Druckerwarteschlange leeren", "Leeren", "Wartende Druckaufträge werden gelöscht.", true, async (p, _) =>
        {
            var wasRunning = services.Get("Spooler")?.Running == true;
            var failed = 0;
            try
            {
                if (wasRunning) await Net("stop", "Spooler").ConfigureAwait(false);
                foreach (var f in files.Files(SpoolDir, "*"))
                {
                    try { files.DeleteFile(f); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
                }
            }
            finally
            {
                if (wasRunning) await Net("start", "Spooler").ConfigureAwait(false);
            }
            return failed == 0
                ? new ActionOutcome(true, "Die Druckerwarteschlange ist leer.")
                : new ActionOutcome(false, $"{failed} Druckaufträge ließen sich nicht löschen.");
        }) { LockKey = "repair", Stamp = true };

    // ---- 5. icon and thumbnail cache ----

    public SystemAction IconCache() =>
        new("repair-icons", "Symbole und Miniaturansichten neu aufbauen", "Neu aufbauen",
            "Der Bildschirm flackert kurz, Fenster des Explorers schließen sich.", false, async (p, ct) =>
            {
                var locked = 0;
                void Delete()
                {
                    var explorerDir = Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
                    var targets = files.Files(localAppData, "IconCache.db")
                        .Concat(files.Files(explorerDir, "iconcache_*.db"))
                        .Concat(files.Files(explorerDir, "thumbcache_*.db"));
                    foreach (var f in targets)
                    {
                        try { files.DeleteFile(f); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { locked++; }
                    }
                }
                var outcome = await SystemActions.RestartExplorerAsync(ender, windowsDir, Delete, p, ct, explorerPoll, explorerTimeout)
                    .ConfigureAwait(false);
                var message = "Symbole und Miniaturansichten werden neu aufgebaut.";
                if (locked > 0) message += $" {locked} Dateien waren gesperrt und bleiben bis zum nächsten Neustart.";
                if (!outcome.Success) message += " " + outcome.Message;
                return new ActionOutcome(outcome.Success, message);
            })
        { LockKey = "repair", Stamp = true };

    // ---- 6. disk check ----

    /// <summary>Set by the last <see cref="ScanDisk"/> run: problems were found (offer <see cref="FixDiskOnRestart"/>).</summary>
    public bool ScanFoundErrors { get; private set; }

    static char Drive(char d) => d is >= 'A' and <= 'Z' ? d : throw new ArgumentException("Ungültiger Laufwerksbuchstabe.", nameof(d));

    public SystemAction ScanDisk(char drive)
    {
        var d = Drive(drive);
        return new SystemAction("repair-chkdsk-scan", "Datenträger prüfen", "Prüfen", null, false, async (p, _) =>
        {
            p?.Report($"Laufwerk {d}: wird geprüft …");
            var (exit, stdout, _) = await run.RunAsync(Sys32("chkdsk.exe"), $"{d}: /scan",
                line => ReportPercent(p, $"Laufwerk {d}: wird geprüft …", line), CancellationToken.None).ConfigureAwait(false);
            var text = Clean(stdout);
            var clean = exit == 0 && (text.Contains("keine probleme") || text.Contains("found no problems"));
            ScanFoundErrors = !clean;
            return clean
                ? new ActionOutcome(true, $"Laufwerk {d}: keine Fehler gefunden.")
                : new ActionOutcome(false, $"Auf Laufwerk {d}: wurden Fehler gefunden. Sie lassen sich beim nächsten Neustart reparieren.");
        }) { SkipGate = true, LockKey = "repair", Stamp = true };
    }

    public SystemAction FixDiskOnRestart(char drive)
    {
        var d = Drive(drive);
        return new SystemAction("repair-chkdsk-fix", "Beim nächsten Neustart reparieren", "Reparatur einplanen",
            "Windows prüft und repariert das Laufwerk beim nächsten Start. Der nächste Start dauert länger; schalten Sie den Rechner dabei nicht aus.",
            true, async (_, _) =>
            {
                var (exit, _, err) = await run.RunAsync(Sys32("fsutil.exe"), $"dirty set {d}:", CancellationToken.None).ConfigureAwait(false);
                return exit == 0
                    ? new ActionOutcome(true, "Die Reparatur läuft beim nächsten Neustart.")
                    : new ActionOutcome(false, $"Die Reparatur ließ sich nicht einplanen (Code {exit}). {err.Trim()}".Trim());
            }) { LockKey = "repair", Stamp = true };
    }
}

/// <summary>Quick read-only checks that decide which repair is recommended.</summary>
public sealed class RepairChecks(IProcessRunner run, string windowsDir)
{
    /// <summary>
    /// The system-file repair is recommended while DISM says „repairable“ – but not again within 30 days after a run that
    /// ended without repair files (it would fail the same way; the row then offers „Jetzt neu installieren“ instead).
    /// </summary>
    public static bool RecommendSystemFiles(bool? repairable, ActionStamp? last, DateTimeOffset now) =>
        repairable == true
        && !(Upgrade(last) is { Success: false, Link: SystemLinks.Recovery } l && now - l.At < TimeSpan.FromDays(30));

    static readonly System.Text.RegularExpressions.Regex OldDismText =
        new(@"^DISM ist fehlgeschlagen \(Code (-?\d+)\)\. sfc wurde trotzdem ausgeführt\. ?(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>
    /// A stamp written before 0.6.0's fix („DISM ist fehlgeschlagen (Code -2146498283) …“, no link) reads like a new one:
    /// plain text and, for missing repair files, the way out. Anything else stays as it is.
    /// </summary>
    public static ActionStamp? Upgrade(ActionStamp? s)
    {
        if (s is null || s.Link is not null) return s;
        var m = OldDismText.Match(s.Summary);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var exit)) return s;
        var o = RepairActions.DismFailed(exit, m.Groups[2].Value.Trim());
        return s with { Summary = o.Message, Link = o.Link };
    }

    /// <summary>true = DISM says the component store is repairable; false = no corruption; null = unknown.</summary>
    public async Task<bool?> ComponentStoreRepairableAsync(CancellationToken ct)
    {
        var (_, stdout, _) = await run.RunAsync(Path.Combine(windowsDir, "System32", "dism.exe"), "/Online /Cleanup-Image /CheckHealth", ct)
            .ConfigureAwait(false);
        var t = stdout.ToLowerInvariant();
        if (t.Contains("keine komponentenspeicherbeschädigung") || t.Contains("no component store corruption")) return false;
        if (t.Contains("repairable") || t.Contains("kann repariert werden") || t.Contains("reparierbar")) return true;
        return null;
    }

    /// <summary>true = Windows marked the volume for a check (dirty bit); null = unknown.</summary>
    public async Task<bool?> IsDirtyAsync(char drive, CancellationToken ct)
    {
        if (drive is < 'A' or > 'Z') throw new ArgumentException("Ungültiger Laufwerksbuchstabe.", nameof(drive));
        var (_, stdout, _) = await run.RunAsync(Path.Combine(windowsDir, "System32", "fsutil.exe"), $"dirty query {drive}:", ct)
            .ConfigureAwait(false);
        var t = stdout.ToLowerInvariant();
        if (t.Contains("not dirty") || t.Contains("nicht fehlerhaft")) return false;
        if (t.Contains("dirty") || t.Contains("fehlerhaft")) return true;
        return null;
    }
}
