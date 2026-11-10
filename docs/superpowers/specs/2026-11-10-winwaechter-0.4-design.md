# WinWächter 0.4.0 – Seite „Leistung“ (Design)

Stand: 2026-11-10. Nutzer hat die Fragerunde übersprungen („leg direkt los“); alles mit **[Entscheidung Claude]** ist eine Vorgabe von mir und kann vom Nutzer beim Lesen geändert werden.

## 1. Zweck
Eine neue Seite „Leistung“ zeigt, was den Rechner gerade bremst und wie lange der Start dauert, und bietet eine Handvoll echter Windows-Einstellungen, die Leistung, Akku oder Speicherplatz spürbar beeinflussen. Jede Einstellung einzeln, mit Erklärung, im Verlauf rückgängig zu machen – wie die Datenschutz-Seite.

Zielrechner des Nutzers (gemessen 2026-11-10): Laptop mit Akku, Intel Arc 140V, 32 GB RAM, Windows 11 Home Build 26200, Modern Standby (S0), Schnellstart an, `hiberfil.sys` 13,5 GB, Energiemodus Netz und Akku „Beste Leistung“, visuelle Effekte bereits „Optimale Leistung“, Speicheroptimierung an.

## 2. Ziele / Nicht-Ziele / Anti-Ziele
Ziele
- Live-Monitor, Startzeit-Bericht und acht Einstellungsgruppen (Abschnitt 4).
- Jede Änderung ist ein `IChange` im Journal (Plan/Apply/Undo/CheckDrift/FromRecord), Ziele nur aus festen Listen im Code.
- Nichts ist vorausgewählt; nur Unterschiede zum Ist-Zustand werden angewendet.

Nicht-Ziele (0.4.0)
- Kein Eintrag in der Übersicht; keine Benachrichtigungen; kein Dauer-Hintergrundmonitor.
- Keine Prozesse beenden auf dieser Seite (gibt es auf „Schutz“).
- Keine Regeln aus dem Netz für diese Seite (Ziele stehen fest im Code).

Anti-Ziele (wäre ein Fehlschlag, auch wenn es „funktioniert“)
- „Tuning-Tricks“: RAM-Booster/Speicher „freigeben“, Prefetch/Superfetch löschen, Dienste massenhaft abschalten, Prozessprioritäten, Core-Parking, Registry-„Beschleuniger“.
- Versprechen von Prozentzahlen („30 % schneller“).
- Eine Änderung, die sich nicht exakt zurücknehmen lässt.
- Dauerlast durch den Monitor selbst (spürbar CPU, wenn die Seite nicht sichtbar ist).

## 3. Aufbau der Seite
Navigation: neuer Eintrag „Leistung“ zwischen „Optimieren“ und „Schutz“, Glyph `` (Segoe Fluent „SpeedHigh“) **[Entscheidung Claude]**; in `docs/design-notes.md` nachtragen.

Reihenfolge (eine ScrollViewer-Seite, Gestaltung nach `docs/design-notes.md`, Bausteine wie Datenschutz/Übersicht):
1. Kopf: Titel „Leistung“, ein Satz Erklärung.
2. **Jetzt** – Messleiste mit drei Anzeigen nebeneinander (Prozessor %, Arbeitsspeicher „x,x von y GB“, Datenträger %), feine Skalenstriche, Tabellenziffern. Darunter „Größte Verbraucher“: 5 Zeilen (Programmname, CPU %, Arbeitsspeicher), gruppiert nach Programmname mit Anzahl („Chrome (12)“).
3. **Start** – „Letzter vollständiger Start: 42 s“ plus Durchschnitt der letzten bis zu 5 Messungen; darunter bis zu 5 „Bremsten den Start“ (Programm/Treiber, Verzögerung in s) aus den letzten 30 Tagen; Knopf „Autostart-Programme prüfen“ → Navigation zur Seite „Optimieren“. Hinweis bei aktivem Schnellstart: „Gemessen wird nur ein vollständiger Start (Neustart), nicht das Hochfahren nach ‚Herunterfahren‘ mit Schnellstart.“
4. **Einstellungen** – Zeilen wie Datenschutz (Titel, Erklärung, Zustands-Chip, rechts das Bedienelement: Auswahlliste oder Schalter-Checkbox). Unten fester Fußbereich mit „Änderungen anwenden (n)“ und „Zurücksetzen“ (verwirft nur die Auswahl, nicht das System).

Ohne Adminrechte (Debug/eingeschränkt): Monitor und Start-Bericht laufen; Einstellungen sichtbar, aber gesperrt mit dem vorhandenen Admin-Hinweis.

## 4. Inhalte im Einzelnen

### 4.1 Live-Monitor (nur lesen)
- Aktualisierung alle 2 s, **nur solange die Seite sichtbar und das Fenster nicht minimiert ist**; beim Verlassen stoppen.
- CPU gesamt: `GetSystemTimes`-Differenzen. RAM: `GlobalMemoryStatusEx`. Datenträger: PDH mit englischem Zählernamen (`PdhAddEnglishCounterW` `\PhysicalDisk(_Total)\% Idle Time`, Anzeige 100 − Leerlauf, begrenzt 0–100) – keine lokalisierten Zählernamen.
- Verbraucher: `Process.GetProcesses()`, CPU aus `TotalProcessorTime`-Differenz je PID / (Intervall × Kerne), RAM = `WorkingSet64`; Prozesse ohne Zugriff: CPU unbekannt, nur RAM. Gruppieren nach Prozessname, sortiert nach CPU, bei Gleichstand RAM. Leerlaufprozess ausblenden.
- Fehler einer Quelle → nur diese Anzeige „–“ (wie Sicherheits-Guards), Log-Eintrag einmal, nicht alle 2 s.

### 4.2 Startzeit-Bericht (nur lesen)
- Quelle: Ereignisprotokoll `Microsoft-Windows-Diagnostics-Performance/Operational` (braucht Admin; ohne Admin Zeile „Nur mit Adminrechten lesbar“).
  - Ereignis 100: `BootTime` (ms) → Startdauer; neueste zuerst, bis zu 5.
  - Ereignisse 101 (Programm), 102 (Treiber), 103 (Dienst) der letzten 30 Tage: `FriendlyName`/`Name` und `DegradationTime` (ms) → je Name die größte Verzögerung, Top 5.
- Lesen mit `System.Diagnostics.Eventing.Reader.EventLogReader` (Microsoft-Paket `System.Diagnostics.EventLog`), XPath-Abfrage, max. 200 Ereignisse; Werte aus dem Ereignis-XML nach Namen.
- Keine Messung vorhanden → „Noch keine Messung. Windows misst die Startzeit nach einem Neustart.“

### 4.3 Energiemodus (Netzbetrieb / Akku) – Kind `powermode`
- Zwei Zeilen, Akku-Zeile nur wenn ein Akku vorhanden ist (`GetSystemPowerStatus.BatteryFlag` ≠ 128/255).
- Auswahl: „Beste Energieeffizienz“ `961cc777-2547-4f9d-8174-7d86181b8a7a`, „Ausgewogen“ `00000000-0000-0000-0000-000000000000`, „Beste Leistung“ `ded574b5-45a0-4f42-8737-46345c09c238`. Andere GUIDs: Anzeige „Benutzerdefiniert“, nicht auswählbar, aber änderbar auf eine der drei.
- Lesen/Schreiben: `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes` `ActiveOverlayAcPowerScheme` / `ActiveOverlayDcPowerScheme` (REG_SZ GUID). Nach dem Schreiben für die gerade aktive Stromquelle zusätzlich `PowerSetActiveOverlayScheme(guid)` (powrprof), damit es sofort gilt.
- **Pflicht für den Umsetzer:** auf dem Rechner prüfen, dass Windows-Einstellungen → System → Strom und Akku nach dem Schreiben denselben Modus zeigt und nach Neustart behält. Klappt der Registry-Weg nicht, Feature-Zeile weglassen statt raten.
- Undo: alten GUID zurückschreiben (+ API, wenn aktive Quelle). Drift: aktueller Wert ≠ gesetzter Wert → überspringen. FromRecord: nur die drei GUIDs und die alten Werte aus dem Journal, die Form einer GUID haben.
- **[Entscheidung Claude]** Empfehlung: keine. Hinweistext Akku-Zeile: „Beste Leistung im Akkubetrieb leert den Akku spürbar schneller.“ (beim Nutzer aktuell so eingestellt).

### 4.4 Visuelle Effekte
- **Transparenzeffekte** – Kind `perfvalue`: `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` `EnableTransparency` 1/0. Nach Apply/Undo `WM_SETTINGCHANGE` mit `"ImmersiveColorSet"` senden (`SendMessageTimeout HWND_BROADCAST`, 2 s), damit es sofort wirkt.
- **Animationseffekte** – Kind `spi`: `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION/SPI_SETCLIENTAREAANIMATION, SPIF_UPDATEINIFILE|SPIF_SENDCHANGE)`. Wirkt auf den Benutzer des Prozesses: nur anbieten, wenn Prozess-SID = SID des angemeldeten Benutzers (`IUserContext.Sid`), sonst Zeile Grau „Nur im eigenen Benutzerkonto änderbar“.
- Link „Weitere Effekte in den Windows-Einstellungen“ → `ms-settings:easeofaccess-visualeffects` (über `SystemLinks`, Schema erlaubt). Das klassische Dialogfeld „Visuelle Effekte“ wird nicht nachgebaut.

### 4.5 Spielmodus – Kind `perfvalue`
- `HKCU\Software\Microsoft\GameBar` `AutoGameModeEnabled` DWORD 1/0; fehlt der Wert → gilt „An“ (Windows-Standard). Undo löscht den Wert, wenn er vorher fehlte (wie `RegValueChange`).

### 4.6 GPU-Planung (hardwarebeschleunigt) – Kind `perfvalue`
- `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers` `HwSchMode` DWORD 2 = an, 1 = aus.
- **[Entscheidung Claude]** Nur als Schalter anbieten, wenn der Wert existiert. Fehlt er (so beim Nutzer), Zeile ohne Schalter: „Wird von Windows für Ihre Grafikkarte automatisch geregelt“ + Link `ms-settings:display-advancedgraphics`.
- Chip/Hinweis „Gilt nach einem Neustart“.

### 4.7 Ruhezustand-Datei – Kind `hibernate`
- Zustand: `HKLM\SYSTEM\CurrentControlSet\Control\Power` `HibernateEnabled` (DWORD) und Größe von `%SystemDrive%\hiberfil.sys` (Dateiattribute lesen, `FileInfo.Length`; nicht vorhanden → 0).
- Apply „Ausschalten“: `%SystemRoot%\System32\powercfg.exe /hibernate off` über `IProcessRunner`, Exit 0 und danach `HibernateEnabled` = 0 prüfen. Undo: `/hibernate on`. Feste Argumente, keine anderen powercfg-Aufrufe.
- Erklärung: „Gibt x GB frei. Dann fehlen der Ruhezustand und der Schnellstart; der Rechner startet nach dem Herunterfahren etwas langsamer.“ Auf Geräten mit Akku zusätzlich Watch-Hinweis: „Bei leerem Akku sichert Windows Ihre Arbeit sonst über den Ruhezustand.“ **[Entscheidung Claude]** nie empfohlen.
- Hinweis: Undo legt die Datei neu an (wieder x GB).

### 4.8 Speicheroptimierung – Kind `perfvalue`
- `HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy`: `01` DWORD 1/0 (an/aus), `2048` DWORD Häufigkeit: 1 = täglich, 7 = wöchentlich, 30 = monatlich, 0 = „Bei wenig Speicherplatz“.
- Zwei Bedienelemente in einer Zeile (Schalter + Auswahl Häufigkeit, Häufigkeit nur bei „an“ bearbeitbar). Je geänderter Wert eine Change.
- **[Entscheidung Claude]** „empfohlen“-Chip bei „an“. Mehr (Papierkorb-Alter, Downloads) nur per Link `ms-settings:storagepolicies`.

### 4.9 Hintergrund-Apps – Kind `perfvalue`
- Liste der Store-Apps aus `HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\<PackageFamily…>`; je App die Werte `Disabled` und `DisabledByUser` (DWORD). „Darf im Hintergrund laufen“ = `Disabled` fehlt oder 0.
- Anzeigename: `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\<PackageFullName>` `DisplayName`, `@{…}`/`ms-resource:` über `SHLoadIndirectString` auflösen; sonst Familienname bis zum `_`.
- Aufklappbarer Block „Hintergrund-Apps (n erlaubt)“, alphabetisch, je App eine Checkbox „Im Hintergrund erlauben“. Ändern = `Disabled` und `DisabledByUser` auf 1 (bzw. zurück auf den alten Zustand).
- Whitelist: nur Werte `Disabled`/`DisabledByUser` in direkten Unterschlüsseln dieses einen Pfads, Ziel 0/1.
- **Pflicht für den Umsetzer:** auf Build 26200 prüfen, ob Windows-Einstellungen → Apps → Installierte Apps → „Erweiterte Optionen“ → „Hintergrund-App-Berechtigungen“ genau diese Werte schreibt (vorher/nachher Registry vergleichen). Wenn nicht: Abschnitt 4.9 weglassen und in der Roadmap notieren.

## 5. Technik
- Neue Change-Kinds registriert wie `FirewallChange.Register`: `powermode`, `spi`, `hibernate`, `perfvalue`.
- `perfvalue` nutzt die Logik von `RegValueChange` (vorher/nachher, Typprüfung, Drift, Löschen wenn vorher fehlend), aber mit eigener fester Whitelist `PerformanceValues` im Code (nicht `rules.privacy`). `FromRecord` lehnt alles ab, was nicht auf der Whitelist steht; Zielwerte kommen aus der Whitelist, nie aus dem Journal. Für 4.9 erlaubt die Whitelist das Muster „direkter Unterschlüssel von BackgroundAccessApplications“ (Prüfung wie `UninstallRoots.IsDirectChild`: kein `\` im Rest, kein `..`).
- Alle Systemzugriffe hinter Schnittstellen mit Fakes (Monitor-Quellen, Ereignisprotokoll, Energie-API, SPI, powercfg über vorhandenes `IProcessRunner`, Broadcast), damit alles ohne Admin testbar ist.
- Anwenden über `OptimizerEngine.ApplyAsync("Leistung", …)` (Wiederherstellungspunkt + Sitzung), `ChangeGate`, `ChangesApplied` → Verlauf. Danach Seite neu lesen.
- Neues Paket: nur `System.Diagnostics.EventLog` (Microsoft). Keine Drittanbieter.
- `ViewSmokeTests`: neue Seite hell/dunkel rendern; Shell-Konstruktor erweitern.

## 6. Was der Umsetzer selbst entscheidet
- Genaue Texte (Sie-Form, sachlich, kurz; Vorlage Datenschutz-Seite), Zeilenhöhen, ob der Monitor einen kleinen Verlauf (Sparkline) bekommt – nur wenn er in die Messinstrument-Optik passt.
- Struktur der Klassen/Ordner (Vorschlag `Core/Performance/…`, `App/ViewModels/PerformanceViewModel.cs`, `App/Views/PerformanceView.xaml`).
- Ob „Zurücksetzen“ als Knopf nötig ist oder die Auswahl einfach beim Neuladen verfällt.

## 7. Erledigt heißt
- Alle Tests grün (neue Unit-Tests je Change-Kind: Apply/Undo/Drift/FromRecord-Whitelist; Scanner/Monitor-Rechnung; VM-Tests; Smoke hell/dunkel).
- Auf dem Rechner des Nutzers (mit seiner Zustimmung): Monitor zeigt plausible Werte; Start-Bericht zeigt eine Messung; je eine Änderung aus 4.3, 4.4, 4.5, 4.8 angewendet und im Verlauf exakt zurückgenommen; 4.7 nur nach ausdrücklicher Zustimmung testen.
- Version 0.4.0, CHANGELOG, Roadmap, Release erst nach Freigabe.
