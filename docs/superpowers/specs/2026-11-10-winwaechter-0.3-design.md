# WinWächter 0.3.0 – Design: App-Übersicht, Sicherheits-Check, Regeln-Update, Feinschliff

Stand: 2026-11-10 · baut auf 0.1 (`2026-11-08-winwaechter-design.md`) und 0.2 (`2026-11-09-winwaechter-0.2-design.md`) auf; alle Grundsätze gelten weiter: erst zeigen, dann ändern, alles umkehrbar (Journal, Ablage, Verlauf), Schutzliste geht vor, keine Telemetrie, App läuft mit Adminrechten.
Vom Nutzer gewählt: App-Übersicht + Deinstallieren (inkl. Notfall „Erzwungen entfernen“), Sicherheits-Check (Ampel), Regeln-Update von GitHub, Feinschliff + Icon. Leistungs-Funktionen kommen als eigene Version 0.4.0. Alle übrigen Entscheidungen hier sind **Entscheidungen des Bauenden**.
Rechner (Messung 2026-11-10): 106 sichtbare Uninstall-Einträge, Firewall alle Profile an, UAC an, Remotedesktop aus, 11 Chrome-Erweiterungen (Default-Profil), 15 Prozesse lauschen auf nicht-lokalen TCP-Ports, kein Icon-Bild vorhanden.

## 1. App-Übersicht (neue Seite „Programme“)
### 1.1 Liste
- Quelle: Uninstall-Einträge aus `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, `HKLM\SOFTWARE\WOW6432Node\…\Uninstall`, `HKU\<SID>\Software\Microsoft\Windows\CurrentVersion\Uninstall` (interaktiver Benutzer). Ausgeblendet: `SystemComponent=1`, Einträge mit `ParentKeyName` (Updates), ohne `DisplayName`, `ReleaseType` „Update“/„Hotfix“/„Security Update“.
- Spalten: Name, Herausgeber, Version, Installationsdatum (`InstallDate` yyyyMMdd), Größe (`EstimatedSize` KB; fehlt → leer, keine teure Messung), Kennzeichen.
- Kennzeichen über `RuleSet.Classify(Name, Exe aus DisplayIcon/InstallLocation, Publisher)`: **Geschützt** (z. B. Lexware, Dell, Microsoft, Treiber), **Unerwünscht (PUA)**, **Miner**, sonst nichts. Store-/MSIX-Apps sind in 0.3.0 nicht enthalten.
- Suche (Textfeld) und Sortierung (Name, Größe, Datum). Liste wird beim ersten Öffnen der Seite gelesen, Knopf „Neu laden“.

### 1.2 Offiziell deinstallieren
- Knopf „Deinstallieren“ je Zeile; bei **Geschützt** kein Knopf, sondern Hinweis „Geschützt – bei Bedarf über Windows-Einstellungen → Apps deinstallieren“.
- Bestätigung (Name, Herausgeber, „Das Deinstallationsprogramm des Herstellers wird gestartet. Das lässt sich in WinWächter nicht rückgängig machen.“).
- Start: `UninstallString` (nicht `QuietUninstallString`, der Nutzer soll den Deinstaller sehen). `MsiExec.exe /I{GUID}` oder `/X{GUID}` → immer `%SystemRoot%\System32\msiexec.exe /X{GUID}` (GUID per Regex geprüft). Sonst: Programmpfad mit `CommandLineParser` bestimmen, muss absolut sein und existieren, Start per `ProcessStartInfo` mit fester Datei + Argumenten (keine Shell, kein `cmd /c`). Anzeige der Signatur des Deinstallers im Bestätigungsdialog.
- Warten bis der Prozess endet (Fortschritt „Deinstallation läuft …“, Abbrechen nicht möglich, Hinweis darauf); danach Liste neu lesen.
- **Danach Reste-Suche** für genau dieses Programm (siehe 1.4).

### 1.3 Notfall „Erzwungen entfernen“
- Nur sichtbar, wenn **nicht geschützt** und eine der Bedingungen gilt: Deinstallationsprogramm fehlt (Datei existiert nicht), oder die offizielle Deinstallation ist beendet und der Eintrag existiert danach noch.
- Vorschau-Dialog listet jede einzelne Änderung; erst nach Bestätigung (Knopf-Stil „endgültig“-Hinweis entfällt, weil umkehrbar; Text: „Alles wird gesichert und lässt sich im Verlauf rückgängig machen.“) anwenden über die Engine (Journal, Verlauf):
  - Programmordner (`InstallLocation`, sonst Ordner des Programms aus `DisplayIcon`) → `MoveToAblageChange.ForFolder` (Kind `folder`, nie automatisch geleert). Nur wenn der Ordner unter `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%LOCALAPPDATA%\Programs` oder `%ProgramData%\<Name>` liegt, nicht geschützt ist und kein anderer Uninstall-Eintrag auf ihn zeigt; sonst Ordner nicht anbieten (Grund anzeigen).
  - Dienste, deren ImagePath/ServiceDll im Ordner liegt → bestehende Change „auf Manuell“ (0.1-Regel: nie deaktivieren, nie stoppen).
  - Aufgaben mit Ziel im Ordner → deaktivieren (bestehende Change).
  - Autostarts mit Ziel im Ordner → ausschalten (bestehende Change).
  - Uninstall-Eintrag → neue Change-Art **`regkey`**: exportiert genau diesen Schlüssel als `.reg` in die Sitzungssicherung, löscht ihn; Undo importiert die Sicherung (`%SystemRoot%\System32\reg.exe import`). Nur für Schlüssel direkt unter einem der drei Uninstall-Pfade (Prüfung in Apply **und** FromRecord), nie anderswo.
- Ordner in Benutzung → wie 0.2 (R7): nichts verschoben, Grund melden, Rest läuft weiter.

### 1.4 Reste nach der Deinstallation
- Nach offizieller Deinstallation: gezielte Suche für das eine Programm: Ordner aus `InstallLocation`/`DisplayIcon` (falls noch vorhanden), Autostarts/Aufgaben/Dienste, deren Ziel in diesem Ordner liegt oder fehlt und den Programmnamen trägt. Die 30-Tage-Regel aus 0.2 gilt hier **nicht** (der Nutzer hat gerade deinstalliert), alle anderen Schutzregeln schon (geschützt, laufender Prozess, anderer Uninstall-Eintrag, Reparse-Points, Lese-Fehler → nichts anbieten).
- Anzeige in einem Dialog wie die Optimieren-Vorschau (Häkchen, Gründe); Ordner **nicht** vorausgewählt, verwaiste Einträge vorausgewählt (wie 0.2). Anwenden über die Engine.

## 2. Sicherheits-Check (neue Seite „Sicherheit“, Ampel)
Jede Zeile: Ampel (Grün/Gelb/Rot/Grau = unbekannt), Titel, Befund, Erklärung, ggf. Aktion. Lesen läuft im Hintergrund; jede Prüfung einzeln abgesichert (Fehler → Grau „nicht prüfbar“).

| Prüfung | Quelle | Ampel | Aktion |
|---|---|---|---|
| Firewall je Profil (Domäne/Privat/Öffentlich) | WMI `root\StandardCimv2` `MSFT_NetFirewallProfile.Enabled` | aus → Rot | „Einschalten“ (neue Change `firewall`: setzt Enabled=1 für das Profil per WMI; Before = alter Wert; Undo stellt ihn her) |
| Defender Echtzeit-/Manipulationsschutz, Signaturalter | bestehender `DefenderClient` | aus → Rot, Signatur > 3 Tage → Gelb | Link „Windows-Sicherheit öffnen“ (`windowsdefender://threat`) |
| SmartScreen für Apps | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer` `SmartScreenEnabled`, Richtlinie `HKLM\SOFTWARE\Policies\Microsoft\Windows\System` `EnableSmartScreen` | „Off“/0 → Gelb; fehlt → Grün (Standard an) | Link `windowsdefender://appbrowser` |
| Benutzerkontensteuerung | `…\Policies\System` `EnableLUA`, `ConsentPromptBehaviorAdmin` | EnableLUA=0 → Rot, Consent=0 → Gelb | nur Hinweis |
| Remotedesktop | `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server` `fDenyTSConnections` | 0 (an) → Gelb | Link `ms-settings:remotedesktop` |
| Geräteverschlüsselung/BitLocker (Systemlaufwerk) | WMI `root\CIMV2\Security\MicrosoftVolumeEncryption` `Win32_EncryptableVolume` (`ProtectionStatus` für das Systemlaufwerk) | aus → Gelb, nicht verfügbar → Grau | Link `ms-settings:deviceencryption` |
| Ausstehende Windows-Updates | `HKLM\…\WindowsUpdate\Auto Update\RebootRequired`; Anzahl über COM `Microsoft.Update.Session` mit `Online=false` (nur zwischengespeicherte Daten), Zeitlimit 30 s | Neustart nötig → Gelb, Updates > 0 → Gelb | Link `ms-settings:windowsupdate` |
| Im Netz lauschende Programme | `GetExtendedTcpTable` LISTEN (IPv4/IPv6), lokale Adresse nicht Loopback; Prozess + Signatur | unsigniert oder in AppData/Temp → Gelb (je Programm eine Unterzeile) | „Von Defender prüfen lassen“ |
| Browser-Erweiterungen | Chromium: `<Profil>\Preferences`/`Secure Preferences` (`extensions.settings`: Name, `from_webstore`, `location`, `state`) für Edge/Chrome/Opera; Firefox `extensions.json` | per Richtlinie erzwungen (`location` 5/10 bzw. Policy `ExtensionInstallForcelist`) oder nicht aus dem Store → Gelb | nur Hinweis „im Browser unter Erweiterungen entfernen“ |

- Links/URIs werden mit `UseShellExecute=true` geöffnet, aber nur aus einer festen Liste (`windowsdefender://…`, `ms-settings:…`).
- Übersicht-Kachel „Sicherheit“: Anzahl Rot/Gelb.

## 3. Regeln-Update von GitHub
- Dateien im Repo: `rules/rules.json` (wie bisher, zusätzlich Felder `"rulesVersion": <int>` und `"minAppVersion": "x.y.z"`) und `rules/rules.json.sig` (ECDSA P-256/SHA-256 über die exakten Bytes von `rules.json`, Base64, gleicher Schlüssel wie Updates).
- Abruf: `https://raw.githubusercontent.com/yIrtIcIkurt/winwaechter/main/rules/rules.json` und `.sig`. `raw.githubusercontent.com` wird **nur für den Regel-Abruf** zur Host-Liste hinzugefügt (eigene HostPolicy-Instanz), keine Weiterleitungen außer zu erlaubten Hosts, max. 1 MB.
- Annahme nur wenn: Signatur gültig, JSON lädt fehlerfrei als `RuleSet`, `rulesVersion` > aktuelle (mitgeliefert oder zwischengespeichert), `minAppVersion` ≤ App-Version. Gespeichert unter `%ProgramData%\WinWaechter\Regeln\rules.json` + `.sig` (Admin-ACL wie der Datenordner).
- Laden beim Start: höchste gültige `rulesVersion` aus mitgelieferter und zwischengespeicherter Datei; die zwischengespeicherte wird beim Laden erneut gegen die Signatur geprüft. **Schutzliste = Vereinigung** aus mitgelieferter und geladener Liste (ein Update kann Schutz nur hinzufügen, nie entfernen).
- Prüfung: beim Start, wenn „Beim Start nach Updates suchen“ an ist, sonst Knopf in Einstellungen „Regeln aktualisieren“; Anzeige „Regeln: Version n vom …“.
- Release-Werkzeug: `ReleaseTool sign-rules <keyPem> <rulesJson>` schreibt `rules.json.sig`; `build-release.ps1` signiert die Regeln mit; neues Skript `publish-rules.ps1` (erhöht `rulesVersion`, signiert, committet, pusht – nur nach Zustimmung des Nutzers).

## 4. Feinschliff
- Rückgängig zeigt Fortschritt: Engine meldet je Datensatz und bei Ablage-Änderungen je 500 Dateien „(n/m) … x Dateien zurückgeholt“; Verlauf zeigt Fortschrittsleiste + Text.
- Defender-Status ohne Microsoft-Dokumentation (z. B. 106): Text „Abgeschlossen (Code 106)“ wenn der Fund nicht aktiv ist, sonst „Unbekannter Status (Code n)“.
- App-Icon: wenn `assets/icon/winwaechter-1024.png` vorliegt, `tools/make-ico.ps1` ausführen und einbinden (App, Installer). Sonst bleibt es beim Standard; der Nutzer bekommt erneut den Bild-Prompt.

## 5. Oberfläche
- Navigation: Übersicht, Optimieren, Schutz, **Sicherheit**, Datenschutz, **Programme**, Verlauf; Einstellungen unten. Gestaltung nach `docs/design-notes.md` (bestehende Tokens, keine neuen Farben; Ampel nutzt die vorhandenen Töne Good/Watch/Alarm/Calm).
- Alle Änderungen über `ChangeGate` + Engine; ohne Adminrechte keine Aktionen (bestehender Hinweis).

## 6. Rahmen
- Version 0.3.0, CHANGELOG, Release nach Zustimmung.
- Tests: Fakes/Temp-Ordner; keine echten Deinstallationen, Firewall-Änderungen, Registry-Löschungen in Tests.
- Fertig heißt: baut, Tests grün, Installer; auf dem Rechner (mit Zustimmung): Programme-Liste stimmt, ein harmloses Programm (Vorschlag: ein vom Nutzer gewähltes, z. B. ein altes Spiel-Tool) offiziell deinstallieren + Reste-Suche, Sicherheits-Seite plausibel, Regeln-Update mit einer neuen signierten `rulesVersion` aus dem Repo, Update 0.2.1 → 0.3.0 über die App.

## 7. Entscheidungen nach der Prüfung (verbindlich, gehen den Abschnitten 1–4 vor)
1. **Wer startet den Deinstaller:** WinWächter startet einen Deinstaller nur selbst (mit Adminrechten), wenn er `msiexec.exe` ist oder seine Datei unter `%ProgramFiles%`, `%ProgramFiles(x86)%` oder `%SystemRoot%` liegt (nur für Admins beschreibbar) **und** der Eintrag aus HKLM stammt. Für HKU-Einträge oder Deinstaller in benutzerbeschreibbaren Orten (AppData usw.) öffnet die App stattdessen `ms-settings:appsfeatures` mit dem Hinweis „Bitte über die Windows-Einstellungen deinstallieren“; die Reste-Suche danach ist trotzdem verfügbar (Knopf „Reste suchen“).
   **Geändert in 0.3.2 (Nutzerwunsch 2026-11-10):** HKU-Einträge und Deinstaller in benutzerbeschreibbaren Orten startet WinWächter jetzt selbst, aber **nur mit den normalen Rechten des angemeldeten Benutzers** (Token seines `explorer.exe` in der Konsolensitzung, SID muss passen, Token darf nicht erhöht sein; `CreateProcessWithTokenW`, Job-Objekt wie oben). Das entspricht dem, was die Windows-Einstellungen tun, ohne Umweg. Verlangt der Deinstaller Adminrechte (Fehler 740) oder klappt der Start nicht, bleibt es beim Weg über `ms-settings:appsfeatures`. Unsichere Befehle (Skripte, Nicht-`.exe`, `%SystemRoot%` außer `msiexec /X{GUID}`) gehen weiter nur über die Einstellungen. Mit Adminrechten wird weiterhin nur gestartet, was die Bedingungen oben erfüllt.
2. **Ende der Deinstallation:** Start in einem Job-Objekt, Warten bis alle Prozesse des Jobs beendet sind; danach Rückfrage „Ist die Deinstallation abgeschlossen?“ (Ja/Noch nicht). Erst dann Liste neu lesen und Reste/Erzwungen anbieten. Exit-Code 1602 (abgebrochen) → nichts anbieten; 3010 → Hinweis „Neustart nötig“.
3. **Regeln aus dem Netz dürfen nur ergänzen:** Alle Listen werden mit den mitgelieferten **vereinigt** (protected, expendableAutostart, pua, minerNames, minerCommandLine, poolPorts, leftoverIgnore). `privacy` wird **nie** aus dem Netz übernommen (nur mitgeliefert). Neue Regeln gelten ab dem nächsten Start (kein Austausch zur Laufzeit). Signatur über `"winwaechter-rules\n"` + Dateibytes (Domänentrennung zu Release-Signaturen), Format wie Releases (IeeeP1363), 1-MB-Grenze beim Lesen. `HostPolicy` wird zu einer Instanz mit eigener Host-Liste umgebaut (Updates: bisherige 4 Hosts; Regeln: zusätzlich `raw.githubusercontent.com`).
4. **Erzwungen entfernen:** nicht für MSI-Produkte (`WindowsInstaller=1`) – dort nur offizielle Deinstallation. Programmordner nur aus `InstallLocation`, muss echter Unterordner (mind. eine Ebene) von `%ProgramFiles%`, `%ProgramFiles(x86)%` oder `%LOCALAPPDATA%\Programs` sein, nicht `Common Files`/`WindowsApps`, und darf keinen `InstallLocation` eines anderen Eintrags enthalten oder darin liegen. `DisplayIcon` und `%ProgramData%` werden für die Ordnerwahl nicht benutzt.
5. **Fremde Sicherheitsprodukte:** `root\SecurityCenter2` `AntiVirusProduct`/`FirewallProduct` lesen; ist ein nicht-Microsoft-Produkt aktiv, zeigt die Zeile „Übernommen von <Produkt>“ (Grün) und bietet kein „Einschalten“ an.
6. **Windows-Updates:** Suche `IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0`, `Online=false`, per `BeginSearch` auf eigenem Thread, nach 30 s `RequestAbort` → Grau. Anzeige „Stand der letzten Windows-Suche“. `RebootRequired` = Schlüssel existiert.
7. **Browser-Erweiterungen:** alle Profile des interaktiven Benutzers (Opera: `%APPDATA%\Opera Software\Opera Stable`). Chromium `location`: 5/10 = eingebaut → ignorieren; 7/9 = per Richtlinie → Gelb; 4/6/8 = von außen installiert → Gelb; sonst und Store-Herkunft → Grün. Zusätzlich Richtlinien `HKLM`/`HKCU` `SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist` und `…\Microsoft\Edge\ExtensionInstallForcelist`. Namen aus `Extensions\<id>\<ver>\manifest.json`, `__MSG_x__` über `_locales\<default_locale>\messages.json` auflösen.
8. **Verschlüsselung:** ProtectionStatus 1 → Grün; verschlüsselt aber ProtectionStatus 0 (wartet auf Aktivierung) → Gelb mit diesem Text; Klasse/Volume fehlt → Grau „nicht verfügbar“.

Technische Korrekturen:
- Firewall über COM `HNetCfg.FwPolicy2` `FirewallEnabled[profile]` (wirksamer Zustand) lesen und setzen; Change `firewall` speichert und stellt genau diesen Wert wieder her.
- Deinstallations-Befehl mit eigenem Parser (Programm + Argumente getrennt): nur `.exe`; `cmd`, `rundll32`, `powershell`, Skripte → nicht selbst starten (dann Windows-Einstellungen öffnen). MSI-GUID aus dem Schlüsselnamen, wenn `WindowsInstaller=1`. WiX-Burn-Pfade unter `Package Cache` sind erlaubt, wenn unter `%ProgramData%\Package Cache` (nur Admins beschreibbar) – sonst Windows-Einstellungen.
- `regkey` ohne `reg.exe`: Schlüssel samt Unterschlüsseln nativ als JSON (Namen, Typen, Daten) in die Sitzungssicherung, Hash im Journal; Undo stellt nativ her, nur unter den drei Uninstall-Pfaden, Hash muss passen.
- Remotedesktop: auf Windows Home Zeile Grau „nicht verfügbar (Home)“.
- Lauschende Programme: PID 4 / Prozesse ohne lesbaren Pfad → Grau, nicht „unsigniert“. Nur TCP.
- Benutzerkontensteuerung: zusätzlich `PromptOnSecureDesktop=0` → Gelb.
- Icon und Installer-Bilder liegen bereits vor (`assets/icon`, `assets/installer`, mit Gemini erstellt, Commit 56f03e9); die App soll das Icon auch in der Seitenleiste statt des Glyphen zeigen.

## 8. Dem Bauenden überlassen
Klassennamen, Aufteilung, Texte im Detail (Sie-Form, sachlich), Sortier-/Suchdetails.
