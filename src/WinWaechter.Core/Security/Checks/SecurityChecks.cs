using System.Net;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Security.Heuristics;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

// Pure rules: sources in, one traffic-light row out.
public static class SecurityChecks
{
    const string PoliciesSystem = @"SOFTWARE\Policies\Microsoft\Windows\System";
    const string Explorer = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
    const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    const string WinNt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    const string TermServer = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    /// <param name="states">Already read profile states; when null, <paramref name="fw"/> is asked.</param>
    public static IReadOnlyList<SecurityItem> Firewall(IFirewall fw, IReadOnlyList<SecurityProduct> firewalls,
        IReadOnlyDictionary<FirewallProfile, bool>? states = null)
    {
        var takeover = firewalls.FirstOrDefault(f => f.Enabled && !f.IsMicrosoft);
        var items = new List<SecurityItem>();
        foreach (var (p, id) in new[]
        {
            (FirewallProfile.Domain, SecurityIds.FirewallDomain),
            (FirewallProfile.Private, SecurityIds.FirewallPrivate),
            (FirewallProfile.Public, SecurityIds.FirewallPublic),
        })
        {
            var title = SecurityTitles.Firewall(p);
            if (takeover is not null)
                items.Add(new(id, title, Light.Green, $"Übernommen von {takeover.Name}",
                    "Eine andere Sicherheitssoftware schützt Ihren Rechner. Die Windows-Firewall bleibt unangetastet."));
            else if (states?[p] ?? fw.IsEnabled(p))
                items.Add(new(id, title, Light.Green, "Eingeschaltet",
                    "Die Firewall blockiert unerwünschte Verbindungen von außen."));
            else
                items.Add(new(id, title, Light.Red, "Ausgeschaltet",
                    "Ohne Firewall sind Verbindungen von außen nicht gefiltert. Sie können sie hier wieder einschalten.",
                    SecurityActionKind.Change, "Einschalten", Change: new FirewallChange(fw, p)));
        }
        return items;
    }

    public static SecurityItem Defender(DefenderStatus s, IReadOnlyList<SecurityProduct> antiVirus)
    {
        const string title = SecurityTitles.Defender;
        var other = antiVirus.FirstOrDefault(a => a.Enabled && !a.IsMicrosoft);
        if (other is not null)
            return new(SecurityIds.Defender, title, Light.Green, $"Übernommen von {other.Name}",
                "Eine andere Schutzsoftware ist aktiv; Defender hält sich dann zurück.");

        SecurityItem Row(Light l, string finding, string why) =>
            new(SecurityIds.Defender, title, l, finding, why, SecurityActionKind.Link, "Windows-Sicherheit öffnen", SystemLinks.DefenderThreat);

        if (!s.Available) return Row(Light.Grey, "Nicht aktiv oder nicht lesbar", "Der Defender-Status konnte nicht gelesen werden. Prüfen Sie ihn in der Windows-Sicherheit.");
        if (!s.RealTime) return Row(Light.Red, "Echtzeitschutz ist aus", "Ohne Echtzeitschutz werden Schadprogramme nicht beim Öffnen erkannt.");
        if (!s.TamperProtected) return Row(Light.Red, "Manipulationsschutz ist aus", "Der Manipulationsschutz verhindert, dass Schadsoftware Defender abschaltet.");
        if (s.SignatureAgeDays > 3) return Row(Light.Yellow, $"Virendefinitionen sind {s.SignatureAgeDays} Tage alt", "Aktuelle Definitionen erkennen neue Schadprogramme besser. Eine Aktualisierung ist ratsam.");
        return Row(Light.Green, "Echtzeit- und Manipulationsschutz an", "Defender schützt Ihren Rechner und ist aktuell.");
    }

    public static SecurityItem SmartScreen(IRegistry reg)
    {
        var off = Dword(reg, PoliciesSystem, "EnableSmartScreen") == 0
                  || string.Equals(reg.Get(Hive.LocalMachine, Explorer, "SmartScreenEnabled") as string, "Off", StringComparison.OrdinalIgnoreCase);
        const string why = "SmartScreen warnt vor unbekannten oder gefährlichen Programmen und Webseiten.";
        return new(SecurityIds.SmartScreen, SecurityTitles.SmartScreen, off ? Light.Yellow : Light.Green, off ? "Ausgeschaltet" : "Eingeschaltet", why,
            SecurityActionKind.Link, "Einstellungen öffnen", SystemLinks.DefenderAppBrowser);
    }

    public static SecurityItem Uac(IRegistry reg)
    {
        const string title = SecurityTitles.Uac;
        if (Dword(reg, UacKey, "EnableLUA") == 0)
            return new(SecurityIds.Uac, title, Light.Red, "Ausgeschaltet", "Ohne Benutzerkontensteuerung laufen Programme ungefragt mit Administratorrechten.");
        if (Dword(reg, UacKey, "ConsentPromptBehaviorAdmin") == 0)
            return new(SecurityIds.Uac, title, Light.Yellow, "Admins werden nicht gefragt", "Programme können Administratorrechte erhalten, ohne dass Sie gefragt werden.");
        if (Dword(reg, UacKey, "PromptOnSecureDesktop") == 0)
            return new(SecurityIds.Uac, title, Light.Yellow, "Abfrage ohne sicheren Desktop", "Die Abfrage erscheint nicht auf dem geschützten Desktop und ist leichter zu täuschen.");
        return new(SecurityIds.Uac, title, Light.Green, "Eingeschaltet", "Programme brauchen Ihre Zustimmung, bevor sie Änderungen am System vornehmen.");
    }

    public static SecurityItem RemoteDesktop(IRegistry reg)
    {
        const string title = SecurityTitles.RemoteDesktop;
        var edition = reg.Get(Hive.LocalMachine, WinNt, "EditionID") as string;
        if (edition?.StartsWith("Core", StringComparison.OrdinalIgnoreCase) == true)
            return new(SecurityIds.RemoteDesktop, title, Light.Grey, "Nicht verfügbar (Home)", "Windows Home kann keine eingehenden Remotedesktop-Verbindungen annehmen.");
        var open = Dword(reg, TermServer, "fDenyTSConnections") == 0;
        return new(SecurityIds.RemoteDesktop, title, open ? Light.Yellow : Light.Green, open ? "Eingeschaltet" : "Ausgeschaltet",
            open ? "Andere Geräte können sich aus der Ferne mit diesem Rechner verbinden. Schalten Sie das aus, wenn Sie es nicht brauchen."
                 : "Es sind keine Fernverbindungen per Remotedesktop erlaubt.",
            SecurityActionKind.Link, "Einstellungen öffnen", SystemLinks.RemoteDesktop);
    }

    public static SecurityItem Encryption(VolumeEncryptionState? s)
    {
        const string title = SecurityTitles.Encryption;
        if (s is null)
            return new(SecurityIds.Encryption, title, Light.Grey, "Nicht verfügbar", "Für dieses Gerät lässt sich der Verschlüsselungsstatus nicht lesen.");
        SecurityItem Row(Light l, string finding, string why) =>
            new(SecurityIds.Encryption, title, l, finding, why, SecurityActionKind.Link, "Einstellungen öffnen", SystemLinks.DeviceEncryption);
        if (s.ProtectionStatus == 1)
            return Row(Light.Green, "Systemlaufwerk verschlüsselt", "Bei Verlust des Geräts sind Ihre Daten ohne Anmeldung nicht lesbar.");
        if (s.ConversionStatus != 0)
            return Row(Light.Yellow, "Verschlüsselt, Schutz derzeit nicht aktiv (z. B. angehalten oder wartet auf Aktivierung)",
                "Das Laufwerk ist verschlüsselt, der Schutz ist aber derzeit nicht aktiv.");
        return Row(Light.Yellow, "Systemlaufwerk nicht verschlüsselt", "Bei Verlust des Geräts könnten Dritte Ihre Daten auslesen.");
    }

    public const string RebootRequiredKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

    public static SecurityItem Updates(bool rebootRequired, WindowsUpdateState? s)
    {
        var stamp = s?.LastSearchUtc is { } t ? $" (Stand der letzten Windows-Suche: {t.ToLocalTime():dd.MM.yyyy HH:mm})" : "";
        SecurityItem Row(Light l, string finding, string why) =>
            new(SecurityIds.Updates, SecurityTitles.Updates, l, finding, why, SecurityActionKind.Link, "Windows Update öffnen", SystemLinks.WindowsUpdate);

        if (rebootRequired)
            return Row(Light.Yellow, "Neustart nötig" + stamp, "Installierte Updates werden erst nach einem Neustart wirksam.");
        if (s is null || s.TimedOut || s.Pending is null)
            return Row(Light.Grey, "Nicht prüfbar" + stamp, "Die Anzahl ausstehender Updates ließ sich nicht ermitteln. Prüfen Sie sie in Windows Update.");
        if (s.Pending > 0)
            return Row(Light.Yellow, (s.Pending == 1 ? "1 Update ausstehend" : $"{s.Pending} Updates ausstehend") + stamp,
                "Updates schließen Sicherheitslücken. Installieren Sie sie bald.");
        return Row(Light.Green, "Keine ausstehenden Updates" + stamp, "Windows hat bei der letzten Suche keine fehlenden Updates gefunden.");
    }

    public static SecurityItem Listeners(IReadOnlyList<ListeningPort> ports, IReadOnlyList<RunningProcess> procs, ISignatureVerifier sig)
    {
        var pathByPid = new Dictionary<int, string?>();
        foreach (var p in procs) pathByPid.TryAdd(p.Pid, p.ExePath);

        var external = ports.Where(p => IPAddress.TryParse(p.LocalAddress, out var a) && !IPAddress.IsLoopback(a));
        var groups = external
            .Select(p => (Port: p, Path: p.Pid == 4 ? null : pathByPid.GetValueOrDefault(p.Pid)))
            .GroupBy(x => x.Path is null ? $"pid:{x.Port.Pid}" : "path:" + x.Path.ToUpperInvariant())
            .ToList();

        var children = new List<SecurityItem>();
        foreach (var g in groups)
        {
            var path = g.First().Path;
            var pid = g.First().Port.Pid;
            var title = path is null ? $"PID {pid}" : Path.GetFileName(path);
            var finding = "Port " + string.Join(", ", g.Select(x => x.Port.LocalPort).Distinct().Order());
            children.Add(ListenerRow(title, finding, path, sig));
        }
        children = children.OrderBy(c => c.Light switch { Light.Yellow => 0, Light.Grey => 1, _ => 2 })
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        var yellow = children.Any(c => c.Light == Light.Yellow);
        var n = children.Count;
        return new(SecurityIds.Listeners, SecurityTitles.Listeners, yellow ? Light.Yellow : Light.Green,
            n == 0 ? "Keine Programme lauschen im Netz" : n == 1 ? "1 Programm" : $"{n} Programme",
            yellow ? "Mindestens ein Programm, das Verbindungen aus dem Netz annimmt, ist auffällig. Lassen Sie es von Defender prüfen."
                   : "Diese Programme nehmen Verbindungen aus dem Netz an. Auffälligkeiten wurden nicht gefunden.",
            Children: children);
    }

    static SecurityItem ListenerRow(string title, string finding, string? path, ISignatureVerifier sig)
    {
        const string id = SecurityIds.Listeners;
        if (path is null)
            return new(id, title, Light.Grey, finding, "Pfad nicht lesbar. Systemprozesse und geschützte Dienste geben ihren Pfad nicht preis.");

        SecurityItem Yellow(string why) =>
            new(id, title, Light.Yellow, finding, why, SecurityActionKind.DefenderScan, "Von Defender prüfen lassen", ScanPath: path);

        SignatureInfo info;
        try { info = sig.Verify(path); }
        catch (Exception) { info = new(SignatureState.Unknown, null, false); }

        if (info.State == SignatureState.Unsigned) return Yellow("Nicht signiert. Der Hersteller des Programms ist nicht bestätigt.");
        if (info.State == SignatureState.Invalid) return Yellow("Signatur ungültig. Die Datei wurde nach dem Signieren verändert oder die Signatur ist nicht vertrauenswürdig.");
        if (path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase))
            return Yellow("Läuft aus einem Benutzerordner. Programme, die Verbindungen annehmen, liegen normalerweise unter „Programme“.");
        if (info.State != SignatureState.Valid)
            return new(id, title, Light.Grey, finding, "Signatur nicht prüfbar. Die Datei konnte nicht gelesen werden.");
        return new(id, title, Light.Green, finding, $"Signiert: {info.Publisher}");
    }

    public const string RemoveAdvice = "Entfernen empfohlen, wenn Sie diese Erweiterung nicht selbst installiert haben.";
    public const string PolicyFinding = "Von Ihrer Organisation oder einem Programm per Richtlinie installiert";
    public const string PolicyHint = "Entfernen nur über die Richtlinie möglich.";

    public static string OriginText(ExtensionOrigin o) => o switch
    {
        ExtensionOrigin.Store => "Aus dem Store",
        ExtensionOrigin.Policy => "Von einer Richtlinie installiert",
        ExtensionOrigin.External => "Von einem Programm installiert",
        ExtensionOrigin.Unpacked => "Von Hand geladen (Entwicklermodus)",
        _ => "Herkunft unbekannt",
    };

    /// <summary>
    /// One child row per installed extension. Store: green, rights listed. Policy: yellow, removable only via the policy.
    /// Any other origin: yellow with the advice to remove. Switched off: grey „Deaktiviert“. Every row except a policy one
    /// gets „In {Browser} entfernen“ when canOpen accepts the browser.
    /// </summary>
    /// <param name="canOpen">Whether the browser's own extensions page can be opened (trusted exe found).</param>
    public static SecurityItem Extensions(IReadOnlyList<BrowserExtension> exts, Func<string, bool>? canOpen = null)
    {
        var children = exts.Select(e => ExtensionRow(e, canOpen)).ToList();
        var yellow = children.Count(c => c.Light == Light.Yellow);
        var finding = children.Count == 0 ? "Keine Erweiterungen gefunden"
            : yellow == 0 ? "Alle in Ordnung"
            : yellow == 1 ? "1 Erweiterung ansehen" : $"{yellow} Erweiterungen ansehen";
        return new(SecurityIds.Extensions, SecurityTitles.Extensions, yellow > 0 ? Light.Yellow : Light.Green, finding,
            "Erweiterungen sehen mit, was Sie im Browser tun. Unbekannte oder nicht mehr benötigte Erweiterungen können Sie im Browser unter Erweiterungen entfernen.",
            Children: children, Chip: yellow > 0 ? "Ansehen" : "In Ordnung");
    }

    static SecurityItem ExtensionRow(BrowserExtension e, Func<string, bool>? canOpen)
    {
        var title = $"{e.Browser} ({e.Profile}): {e.Name}";
        var lines = new List<string>();
        var facts = new List<string>();
        if (e.Version is { } v) facts.Add($"Version {v}");
        if (e.InstalledUtc is { } t) facts.Add($"Installiert am {t.ToLocalTime().ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)}");
        if (facts.Count > 0) lines.Add(string.Join(" · ", facts));
        if (e.Permissions is not null || e.Hosts is not null)
        {
            var rights = ExtensionRights.Describe(e.Permissions ?? [], e.Hosts ?? []);
            lines.Add(rights is [ExtensionRights.None] ? ExtensionRights.None : "Rechte: " + string.Join(" · ", rights));
        }
        string Text(params string[] more) => string.Join("\n", lines.Concat(more));

        // a policy decides about its extensions; every other one the user may remove, also when it is fine or switched off
        if (e.Origin == ExtensionOrigin.Policy)
            return new(SecurityIds.Extensions, title, Light.Yellow, PolicyFinding, Text(PolicyHint), Chip: "Ansehen");
        var button = canOpen is not null && BrowserExtensionScanner.IsChromiumId(e.Id) && canOpen(e.Browser);
        SecurityItem Row(Light light, string explanation, string? chip = null) =>
            new(SecurityIds.Extensions, title, light, OriginText(e.Origin), explanation,
                button ? SecurityActionKind.ExtensionPage : SecurityActionKind.None, button ? $"In {e.Browser} entfernen" : null,
                Chip: chip, Extension: button ? new ExtensionTarget(e.Browser, e.Id) : null);

        if (e.Disabled)
            return Row(Light.Grey, Text("Die Erweiterung ist im Browser ausgeschaltet."), "Deaktiviert");
        if (e.Origin == ExtensionOrigin.Store)
            return Row(Light.Green, lines.Count > 0 ? Text() : "Diese Erweiterung wurde aus dem offiziellen Store installiert.");
        return Row(Light.Yellow, Text(RemoveAdvice), "Ansehen");
    }

    public static SecurityItem Unavailable(string id, string title, string? detail = null) =>
        new(id, title, Light.Grey, detail is null ? "Nicht prüfbar" : $"Nicht prüfbar – {detail}",
            "Diese Prüfung konnte nicht durchgeführt werden. Die übrigen Prüfungen sind davon nicht betroffen.");

    static int? Dword(IRegistry reg, string key, string name) =>
        reg.Get(Hive.LocalMachine, key, name) is int i ? i : null;
}
