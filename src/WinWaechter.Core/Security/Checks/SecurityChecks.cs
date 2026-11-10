using WinWaechter.Core.Security.Defender;
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

    public static IReadOnlyList<SecurityItem> Firewall(IFirewall fw, IReadOnlyList<SecurityProduct> firewalls)
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
            var title = $"Firewall ({FirewallChange.ProfileName(p)})";
            if (takeover is not null)
                items.Add(new(id, title, Light.Green, $"Übernommen von {takeover.Name}",
                    "Eine andere Sicherheitssoftware schützt Ihren Rechner. Die Windows-Firewall bleibt unangetastet."));
            else if (fw.IsEnabled(p))
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
        const string title = "Microsoft Defender";
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
        return new(SecurityIds.SmartScreen, "SmartScreen", off ? Light.Yellow : Light.Green, off ? "Ausgeschaltet" : "Eingeschaltet", why,
            SecurityActionKind.Link, "Einstellungen öffnen", SystemLinks.DefenderAppBrowser);
    }

    public static SecurityItem Uac(IRegistry reg)
    {
        const string title = "Benutzerkontensteuerung";
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
        const string title = "Remotedesktop";
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
        const string title = "Geräteverschlüsselung";
        if (s is null)
            return new(SecurityIds.Encryption, title, Light.Grey, "Nicht verfügbar", "Für dieses Gerät lässt sich der Verschlüsselungsstatus nicht lesen.");
        SecurityItem Row(Light l, string finding, string why) =>
            new(SecurityIds.Encryption, title, l, finding, why, SecurityActionKind.Link, "Einstellungen öffnen", SystemLinks.DeviceEncryption);
        if (s.ProtectionStatus == 1)
            return Row(Light.Green, "Systemlaufwerk verschlüsselt", "Bei Verlust des Geräts sind Ihre Daten ohne Anmeldung nicht lesbar.");
        if (s.ConversionStatus != 0)
            return Row(Light.Yellow, "Verschlüsselt, wartet auf Aktivierung", "Das Laufwerk ist verschlüsselt, der Schutz ist aber noch nicht aktiv.");
        return Row(Light.Yellow, "Systemlaufwerk nicht verschlüsselt", "Bei Verlust des Geräts könnten Dritte Ihre Daten auslesen.");
    }

    public static SecurityItem Unavailable(string id, string title, string? detail = null) =>
        new(id, title, Light.Grey, detail is null ? "Nicht prüfbar" : $"Nicht prüfbar – {detail}",
            "Diese Prüfung konnte nicht durchgeführt werden. Die übrigen Prüfungen sind davon nicht betroffen.");

    static int? Dword(IRegistry reg, string key, string name) =>
        reg.Get(Hive.LocalMachine, key, name) is int i ? i : null;
}
