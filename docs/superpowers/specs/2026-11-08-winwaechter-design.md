# WinWächter – Design

Stand: 2026-11-08 · Arbeitsname „WinWächter“ (Programm, Ordner, Repo `yIrtIcIkurt/winwaechter`) · Status: zur Freigabe

## 1. Absicht und Warum

Der Nutzer hat auf seinem Rechner von Hand geprüft und aufgeräumt: Defender-Status und Funde, Prozesse mit hoher Last oder an ungewöhnlichen Orten, Autostarts, geplante Aufgaben, Drittanbieter-Dienste, Signaturen und ausgehende Verbindungen. Gefunden wurden typische Fälle: Glary Utilities (PUA), ein FileZilla-Bundle-Installer, ein unsignierter „Cua-Driver“ hinter einer versteckten Aufgabe und viele Autostarts von Spielen und Launchern.

WinWächter soll genau diese Arbeit automatisieren und dabei genauso vorsichtig vorgehen wie der Nutzer: **erst zeigen, dann ändern, alles umkehrbar**. Zielgruppe ist der Nutzer selbst. Er ist technisch versiert, hat aber keine Adminrechte im Alltag (Adminrechte über UAC), Windows 11 Home, einen Dell-Laptop mit Intel Arc 140V und 32 GB RAM.

## 2. Ziele

1. **One-Click-Optimizer**: Ein Knopf prüft Autostarts, Drittanbieter-Dienste, geplante Aufgaben und Temp-Dateien. Danach zeigt das Programm eine Vorschau mit Begründung je Punkt. Erst nach Bestätigung ändert es etwas, jede Änderung ist rückgängig zu machen.
2. **Schädlings-Scan**: Microsoft Defender ist die Engine (Status, Funde, Schnell- und Vollscan, Dateiprüfung, Bereinigung). Dazu kommen eigene Heuristiken für Miner und Verdächtiges.
3. **Selbst-Update** über GitHub Releases. Download nur per HTTPS, vor dem Ausführen werden SHA-256-Prüfsumme und Signatur geprüft.
4. **Installer** mit Inno Setup, inklusive Startmenü-Eintrag und Deinstallation.
5. **Deutsche, hochwertige Oberfläche** mit eigenem Stil (Skill `frontend-design`), kein KI-Einheitslook.

## 3. Nicht-Ziele

- Keine eigene Virensignatur-Engine, kein Echtzeitschutz und kein Hintergrunddienst.
- Kein Registry-Cleaner, kein „RAM-Booster“, kein Treiber-Updater.
- Keine Telemetrie, keine Konten, keine Cloud. Die einzige Netzverbindung ist die Update-Prüfung bei GitHub.
- Kein Code-Signing-Zertifikat (kostet Geld). SmartScreen warnt deshalb beim ersten Installieren, das ist akzeptiert.
- Keine geplanten Hintergrund-Scans in Version 1.

## 4. Anti-Ziele (wäre ein Fehlschlag, selbst wenn es „funktioniert“)

- Das Programm fasst Dell-Dienste/-Software, **Lexware** (Buchhaltung, wird gebraucht), Windows- oder Microsoft-Komponenten, Treiber (Intel, Realtek usw.) oder Defender an.
- Es löscht etwas endgültig, ohne dass der Nutzer das ausdrücklich will.
- Es ändert etwas ohne Vorschau und Bestätigung.
- Es macht mit roten Warnungen Angst, wo nur „unbekannt“ angebracht wäre. Funde nennen immer ihre Gründe und eine Einstufung.
- Das Programm selbst ist ein Einfallstor, zum Beispiel weil das Update nicht geprüft wird oder Sicherungsdaten von normalen Benutzern verändert werden können.

## 5. Rahmenbedingungen

- **Technik (vom Nutzer gewählt):** C# / **.NET 10** (LTS bis 11/2028; .NET 8 endet am 10.11.2026, deshalb gewechselt) / WPF. Die Veröffentlichung ist self-contained (win-x64), es braucht keine installierte Runtime.
- **Projektordner (vom Nutzer gewählt):** `C:\Users\tahsi\dev\winwaechter`, nicht in OneDrive.
- **Updates (vom Nutzer gewählt):** öffentliches GitHub-Repo `yIrtIcIkurt/winwaechter`, GitHub Releases.
- **Werkzeuge (installiert):** .NET SDK 10.0.x (und 8.0.427) unter `%LOCALAPPDATA%\Microsoft\dotnet`, Inno Setup 6 (Benutzerinstallation), `gh`.
- **Rechte:** Das Programm startet mit Adminrechten (Manifest `requireAdministrator`, eine UAC-Abfrage beim Start), weil Scans, Dienste und Aufgaben das brauchen. Installiert wird nach `C:\Program Files\WinWaechter`. Geprüft: Das Konto `tahsi` ist Mitglied der Administratoren mit geteiltem Token. Die erhöhte App läuft also als derselbe Benutzer, HKCU und `%TEMP%` sind die richtigen. Für Sonderfälle (Admin-Kennwort eines anderen Kontos) ermittelt die App trotzdem den angemeldeten Benutzer über die Sitzung von `explorer.exe` und arbeitet dann über `HKU\<SID>` und dessen Profilpfade. *(Meine Entscheidung.)*
- **Smart App Control:** Geprüft, ist aus (`VerifiedAndReputablePolicyState = 0`). Unsignierte Builds laufen also, SmartScreen warnt nur.
- **Grafiken:** Ich erzeuge keine. Wo Icon oder Logo nötig sind, bekommt der Nutzer fertige Bild-Prompts mit genauem Ordner und Dateinamen. Bis dahin nutzt die App ein Text- bzw. Vektor-Platzhalterzeichen, der Installer das Standard-Icon.
- **Abhängigkeiten:** so wenige wie möglich, nur Open Source und ohne Datensammlung. Geplant sind `CommunityToolkit.Mvvm`, `System.Management` (WMI) und `TaskScheduler` (David Hall, MIT) für die Aufgabenplanung. Signaturen laufen über WinVerifyTrust, Verbindungen über `GetExtendedTcpTable` (beides P/Invoke).
- **Tests auf dem Rechner des Nutzers:** Lesen und Scannen ist erlaubt. **Systemänderungen nur nach Zustimmung** des Nutzers im Chat.

## 6. Aufbau

```
src/WinWaechter.App        WPF-Oberfläche (MVVM)
src/WinWaechter.Core       Prüfungen, Aktionen, Journal, Regeln (ohne UI, testbar)
src/WinWaechter.Updater    Update-Prüfung und -Verifikation (in Core oder eigenes Projekt)
tools/ReleaseTool          Erzeugt SHA256SUMS + Signatur für ein Release (Konsolenprogramm)
installer/winwaechter.iss  Inno-Setup-Skript
tests/WinWaechter.Tests    xUnit
rules/rules.json           Wissensbasis (wird mit dem Programm ausgeliefert)
```

Jede Prüfung liefert **Befunde** (Was, Wo, Gründe, Einstufung, vorgeschlagene Aktion, vorausgewählt ja/nein). Jede Aktion kann sich selbst **sichern, ausführen und umkehren**. Für Tests gibt es eine Abstraktionsschicht über Registry, Dienste, Aufgaben und Dateisystem.

**Signaturprüfung:** WinVerifyTrust **mit Katalog-Suche** (`CryptCATAdminCalcHashFromFileHandle2` / `CryptCATAdminEnumCatalogFromHash`), weil viele Windows-Dateien nur katalogsigniert sind und sonst fälschlich als unsigniert gälten. Bei svchost-Diensten wird zusätzlich `Parameters\ServiceDll` geprüft. Kann eine Signatur nicht ermittelt werden, zählt der Eintrag als „unbekannt“, nie als „unsigniert“. Liegt ein Eintrag unter `C:\Windows`, wird er im Optimizer grundsätzlich nicht vorgeschlagen.

### 6.1 Wissensbasis (`rules.json`)

- **Geschützt:** wird nie vorgeschlagen und nie geändert. Treffer über Herausgeber der Signatur, Pfad oder Name. Dazu gehören Microsoft/Windows, Dell (alle Dell-Dienste und -Programme), Lexware/Haufe, Intel, Realtek, Treiber allgemein, Defender und Sicherheitssoftware. Diese Liste geht **allen anderen Regeln vor**.
- **Bekannt entbehrlich im Autostart** (vorausgewählt): Spiele-Launcher und Updater wie Steam, Epic, EA, Ubisoft, Battle.net, Riot, GOG Galaxy, außerdem Discord, Spotify, Teams (persönlich), Adobe- und Google-Updater-Autostarts und Ähnliches.
- **Bekannte PUAs** (vorausgewählt, Einstufung „unerwünscht“): Glary Utilities, CCleaner-Bundles, Driver-Booster-artige Tools, Bundle-Installer usw.
- **Miner:** Prozess- und Dateinamen (xmrig, nicehash, phoenixminer, t-rex, lolminer, nbminer, gminer, ethminer, cpuminer, ccminer usw.) und Pool-Ports (3333, 4444, 5555, 7777, 8888, 9999, 14444, 14433, 45700 u. a.). Ports allein ergeben nur einen schwachen Hinweis.
- **Unbekannt:** wird als Info angezeigt und **nicht** vorausgewählt.

Die Regeln werden mit jedem Programm-Update mitgeliefert, ein eigener Regel-Download ist nicht nötig.

## 7. One-Click-Optimizer

**Ablauf:** Knopf „Prüfen“ → Fortschritt → Vorschauliste, gruppiert nach Autostart, Dienste, Aufgaben und Speicher. Jeder Punkt hat ein Häkchen, eine Begründung und eine Einstufung. Darauf folgt „Ausgewählte anwenden (n)“ und eine Zusammenfassung.

| Bereich | Was wird geprüft | Aktion | Rückgängig |
|---|---|---|---|
| Autostart | HKCU/HKLM `Run`, `Run32`, Autostart-Ordner (Benutzer und alle) | Eintrag unter `...\Explorer\StartupApproved\Run` (bzw. `Run32`, `StartupFolder`) auf `03 00 00 00` + Zeitstempel setzen, so wie der Task-Manager. Der Eintrag selbst bleibt erhalten. | vorherigen Wert zurückschreiben bzw. Wert löschen, falls vorher keiner da war |
| Dienste | laufende/automatische Dienste, deren Programmdatei nicht von Microsoft signiert ist und die nicht geschützt sind | Starttyp **Automatisch → Manuell** (nie „Deaktiviert“), Dienst nicht stoppen | ursprünglichen Starttyp einschließlich „verzögert“ wiederherstellen |
| Aufgaben | Aufgaben außerhalb von `\Microsoft\`, aktiv, nicht geschützt. Verstecktes Flag und unsignierte Ziele werden markiert | XML exportieren, Aufgabe deaktivieren | Aufgabe aktivieren, falls sie gelöscht wurde aus XML neu registrieren |
| Speicher | `%TEMP%` des Nutzers, `C:\Windows\Temp`, Dateien älter als 2 Tage, gesperrte Dateien werden übersprungen | Dateien in die **Ablage** verschieben (siehe 7.2), Anzeige der frei werdenden Größe | aus der Ablage an den Originalpfad zurückverschieben |

Vorausgewählt wird nur, was die Wissensbasis als entbehrlich kennt, außerdem Temp-Dateien. Unbekannte Dienste und Aufgaben erscheinen ohne Häkchen.

### 7.1 Sicherung

Vor jedem Anwenden:

1. **Wiederherstellungspunkt** über WMI `SystemRestore.CreateRestorePoint`. Weil Windows bei der 24-Stunden-Sperre trotzdem „Erfolg“ meldet, vergleicht die App die Liste der Punkte (`SystemRestore`-Instanzen) vorher und nachher. Ist der Computerschutz aus, oder wurde kein neuer Punkt angelegt, steht das klar in der Zusammenfassung, und es geht trotzdem weiter. Die eigene Sicherung ist der Hauptweg.
2. **Eigene Sicherung** pro Sitzung unter `%ProgramData%\WinWaechter\Sitzungen\<JJJJ-MM-TT_HHMMSS>\`:
   - `journal.json`: jede Änderung mit Typ, Ziel, Zustand vorher und nachher, Zeit und Status
   - `.reg`-Exporte der betroffenen Schlüssel (`reg.exe export`) und Aufgaben-XML. Damit kann der Nutzer notfalls auch ohne das Programm zurück.
3. Das Journal wird **vor** jeder Einzeländerung geschrieben. Bricht etwas ab, ist der Stand immer nachvollziehbar.

### 7.2 Ablage statt Papierkorb

Dateien wandern nicht in den Papierkorb, denn daraus lassen sie sich nicht zuverlässig per Programm zurückholen. Sie kommen in `%ProgramData%\WinWaechter\Sitzungen\<Sitzung>\Ablage\`, die Originalpfade stehen in einer Liste neben dem Journal der Sitzung. Die Ablage wird **nach 14 Tagen** automatisch geleert (in den Einstellungen änderbar), außerdem gibt es den Knopf „Ablage jetzt leeren“. Erst dieses Leeren ist endgültig, das sagt die Oberfläche auch so. *(Meine Entscheidung.)*

### 7.3 Verlauf und Rückgängig

Die Seite „Verlauf“ listet alle Sitzungen. Pro Sitzung gibt es „Alles rückgängig“, außerdem kann jede Änderung einzeln zurückgenommen werden. Rückgängig läuft in umgekehrter Reihenfolge. Was sich inzwischen von außen geändert hat (z. B. Wert schon wieder an), wird erkannt und gemeldet, statt es blind zu überschreiben.

### 7.4 Schutz der Programmdaten

`%ProgramData%\WinWaechter` bekommt eine eigene Zugriffsliste: Schreiben dürfen nur Administratoren und SYSTEM, Benutzer nur lesen. Installer und App übernehmen dazu den Besitz (Administratoren) und **ersetzen** die Zugriffsliste mit abgeschalteter Vererbung, denn ein normaler Benutzer könnte den Ordner vorher angelegt haben. Sonst könnte ein normaler Prozess das Journal so manipulieren, dass die Admin-App beim Rückgängigmachen etwas Falsches tut.

## 8. Schädlings-Scan und Bereinigung

### 8.1 Defender (Engine)

- **Status** über WMI `root\Microsoft\Windows\Defender` (`MSFT_MpComputerStatus`): Echtzeitschutz, Signaturstand und -alter, letzter Scan, Manipulationsschutz und PUA-Schutz (`MSFT_MpPreference.PUAProtection`).
- **Funde** über `MSFT_MpThreat` und `MSFT_MpThreatDetection`: Name, Schwere, Pfad, Status (aktiv, in Quarantäne, entfernt).
- **Scans** über `MpCmdRun.exe` (Pfad aus `HKLM\SOFTWARE\Microsoft\Windows Defender\InstallLocation`): Schnellscan (`-Scan -ScanType 1`), Vollscan (`-ScanType 2`), Datei- oder Ordnerprüfung (`-ScanType 3 -File`). Vorher Signaturen aktualisieren (`-SignatureUpdate`). Fortschritt wird als „läuft“ mit Laufzeit angezeigt. Abbrechen läuft über `MpCmdRun -Scan -Cancel`, nicht über das Beenden des Prozesses.
- **Bereinigung** über Defender: **ein** Knopf „Alle aktiven Defender-Funde bereinigen“ (`Remove-MpThreat` per PowerShell-Unterprozess, das wirkt immer auf alle aktiven Funde). Eine Auswahl einzelner Funde gibt es dafür nicht. *(Meine Entscheidung.)* Die Oberfläche verspricht hier **kein** Rückgängig: Defender verschiebt meist in die Quarantäne (`MpCmdRun -Restore` wird angeboten, wo möglich), kann je nach Fund aber auch löschen.
- **PUA-Schutz** ist er aus, schlägt das Programm vor, ihn einzuschalten (`Set-MpPreference -PUAProtection Enabled`). Die Änderung kommt ins Journal und lässt sich zurücknehmen.
- Ist Defender nicht aktiv (z. B. weil ein anderer Virenscanner läuft), wird das angezeigt und die Heuristiken laufen trotzdem.

### 8.2 Eigene Heuristiken

Jeder Hinweis bekommt Punkte, die Summe ergibt die Einstufung **Info / Auffällig / Verdächtig**. Angezeigt werden immer die einzelnen Gründe.

| Hinweis | Quelle |
|---|---|
| Prozess mit Dauerlast (CPU-Anteil über ca. 25 % in zwei Messungen über ca. 10 s, bzw. GPU-Last, sofern auslesbar) | Prozess-Zeiten |
| Programmdatei in `AppData`, `Temp`, `ProgramData`, `Public` o. ä. **und** unsigniert oder Signatur ungültig | Pfad + WinVerifyTrust |
| Bekannter Miner-Name (Prozess, Datei, Befehlszeile mit `stratum+tcp`, `--donate-level`, `-o pool...`) | WMI `Win32_Process` |
| Ausgehende Verbindung zu einem Pool-Port | `GetExtendedTcpTable` mit Prozess-ID |
| Versteckte geplante Aufgabe, Ziel unsigniert oder in AppData/Temp | Aufgabenplanung |
| Autostart, der auf eine unsignierte Datei in AppData/Temp zeigt | Registry/Autostart-Ordner |
| Unsignierter Dienst außerhalb von `Program Files`/`Windows` | Dienste |
| Bekannte PUA (Wissensbasis) | Name/Herausgeber |

Geschützte Einträge (6.1) werden nie als verdächtig eingestuft.

**Aktionen je Fund** (Einzelauswahl, Vorschau, Journal wie beim Optimizer):
- „Von Defender prüfen lassen“ (Dateiprüfung, keine Änderung)
- „Prozess beenden“ (nicht umkehrbar, die Oberfläche weist darauf hin, aber harmlos)
- „Autostart/Aufgabe/Dienst deaktivieren“ (umkehrbar wie in 7)
- „Datei in die Ablage verschieben“ (umkehrbar)
- Für eigene Funde, die Defender bei der Dateiprüfung bestätigt, gilt danach der Defender-Knopf oben.

## 9. Selbst-Update

- **Prüfung** beim Start, wenn eingeschaltet (Standard: an), und per Knopf in den Einstellungen. Abgefragt wird `https://api.github.com/repos/yIrtIcIkurt/winwaechter/releases/latest`. Der User-Agent ist ein neutraler Programmname ohne Nutzerdaten.
- **Release-Inhalt:** `WinWaechter-Setup-<version>.exe`, `SHA256SUMS`, `SHA256SUMS.sig`.
- **Signatur:** ECDSA P-256 mit SHA-256 über die Datei `SHA256SUMS`. Das ist in .NET eingebaut und braucht keine Zusatzbibliothek. *(Meine Entscheidung statt Ed25519.)* Der öffentliche Schlüssel ist fest im Programm eingebaut. Der private Schlüssel liegt **nur** in `C:\Users\tahsi\dev\winwaechter-keys\` (außerhalb des Repos, nie eingecheckt). Der Nutzer soll ihn zusätzlich sichern, ohne ihn gibt es keine Updates mehr.
- **Ablauf:** Version vergleichen (SemVer) → Hinweis mit Änderungsnotizen → „Herunterladen“ → nur `https://`-URLs auf `api.github.com`, `github.com`, `objects.githubusercontent.com` und `release-assets.githubusercontent.com` (Weiterleitungen nur dorthin) → Download in einen eigenen Ordner unter `%ProgramData%\WinWaechter\Updates\` → Signatur von `SHA256SUMS` prüfen → Die signierte Datei enthält auch die Versionsnummer, die muss zum Release passen und höher sein als die installierte (Schutz gegen das Unterschieben alter Versionen) → Hash des Setups prüfen → bei Fehler löschen und klar melden → bei Erfolg „Jetzt installieren“: Setup mit `/SILENT /CLOSEAPPLICATIONS` starten, App beenden, danach startet das Setup die App neu.
- Es gibt keinen automatischen Download ohne Klick.
- **Release-Werkzeug** (`tools/ReleaseTool`): erzeugt Schlüsselpaar (einmalig), `SHA256SUMS` und `.sig`. Ein Skript `build-release.ps1` baut, veröffentlicht, erstellt den Installer, signiert und lädt mit `gh release create` hoch. Hochladen nur nach Zustimmung des Nutzers.

## 10. Installer (Inno Setup 6)

- Installation pro Rechner nach `C:\Program Files\WinWaechter` mit Adminrechten.
- Startmenü-Eintrag „WinWächter“, Desktop-Verknüpfung optional (Häkchen, Standard aus).
- Deinstallation über „Apps“ in Windows. Dabei wird gefragt, ob `%ProgramData%\WinWaechter` (Sitzungen und Ablage) erhalten bleiben soll. Standard: **behalten**, damit Rückgängig-Daten nicht versehentlich verloren gehen.
- Feste `AppId` (GUID), damit Updates über die vorhandene Installation laufen.
- Version kommt aus dem Build (eine Quelle der Wahrheit: `Directory.Build.props`).

## 11. Oberfläche

- Sprache Deutsch, Stil über Skill `frontend-design`: eigenständig, ruhig und hochwertig, keine generischen Farbverläufe und kein Standard-Dashboard-Look. Hell und dunkel folgen der Windows-Einstellung.
- Seiten: **Übersicht** (Zustand auf einen Blick: Defender, letzter Scan, offene Vorschläge, großer Knopf „Prüfen“), **Optimieren**, **Schutz**, **Verlauf**, **Einstellungen und Update**.
- Lange Vorgänge laufen asynchron mit Fortschritt und lassen sich abbrechen. Die Oberfläche friert nie ein.
- Tastaturbedienbar, ausreichend Kontrast, skaliert bei 125 % bzw. 150 % Anzeigeskalierung.

## 12. Protokoll

`%ProgramData%\WinWaechter\Logs\` enthält Textprotokolle mit Rotation (7 Tage) für die Fehlersuche. Nichts davon verlässt den Rechner.

## 13. Fertig heißt

1. `dotnet build` und `dotnet test` laufen grün, der Installer wird gebaut.
2. Der Installer installiert auf dem Rechner des Nutzers (mit Zustimmung). Startmenü-Eintrag und Deinstallation funktionieren.
3. Scan: Defender-Status, Funde und Heuristiken erscheinen, ein Schnellscan läuft durch.
4. Optimieren: Die Vorschau stimmt. Auf Zustimmung wird angewendet, der Verlauf zeigt die Sitzung, Rückgängig stellt alles wieder her. Getestet wird bevorzugt an eigens angelegten Test-Einträgen (Dummy-Autostart, Dummy-Aufgabe, Temp-Testdateien), damit am echten System möglichst wenig passiert.
5. Update: Version 0.1.0 ist installiert, auf GitHub liegt 0.1.1. Das Programm findet sie, prüft sie, verweigert eine manipulierte Datei und installiert die echte.

## 14. Dem Bauenden überlassen

- Genaue Projektaufteilung, Klassennamen, MVVM-Details.
- Konkrete Punktwerte und Schwellen der Heuristiken (solange sie die Grundsätze oben einhalten).
- Die vollständige Liste der Einträge in der Wissensbasis.
- Visuelle Gestaltung im Rahmen von Abschnitt 11.
