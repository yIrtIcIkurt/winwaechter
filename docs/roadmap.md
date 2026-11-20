# WinWächter – Roadmap

Stand: 2026-11-13 (Version 0.5.0, unveröffentlicht)

## 0.2.0 (veröffentlicht 2026-11-09)
- [x] Gebaut und geprüft (464 Tests, Einzel- und Gesamtprüfung), Release v0.2.0, Selbst-Update 0.1.1 → 0.2.0 auf dem Rechner
- [x] Neue Optimieren-Gruppen auf dem Rechner (2026-11-10): Chrome offen → Info, Update-Cache gesperrt, Installer nie vorausgewählt – wie vorgesehen
- [x] Verwaiste Ordner/Einträge: keine angeboten (Glary-Rest wegen Ordner-Zeitstempel erst nach 30 Tagen – siehe 0.2.1)
- [x] Cache-Bereinigung: 5,0 GB (7.301 Dateien) in die Ablage, 0 übersprungen (2026-11-10)
- [x] Datenschutz: „Feedback-Abfragen“ + „Sprachliste für Websites“ angewendet und exakt rückgängig gemacht (2026-11-10)
- [ ] Automatisches Leeren der Cache-Ablage nach 1 Tag prüfen (ab 2026-11-11)
- [ ] „Wird frei“-Kachel hell/dunkel ansehen

## 0.2.1 (veröffentlicht 2026-11-10, af643a6)
- Pfadanzeige: bei Opera und „Absturzabbilder (Windows)“ die echten Cache-Ordner statt des gemeinsamen Oberordners (`C:\Users\…\AppData`, `C:\windows`) zeigen.
- Verwaiste Ordner: „30 Tage unberührt“ nach den Dateizeiten bewerten, nicht nach dem Ordner-Zeitstempel (Deinstallation ändert ihn).
Für später (aus den Prüfungen): Browser/Update-Prüfung auch beim Anwenden wiederholen; Browser an anderen Installationsorten (pro Benutzer, Store); `regvalue` ohne Plan (Absturzfenster); Auto-Leeren nur beim App-Start; Startmenü-Unterordner ohne Leserecht werden still übersprungen; fremde Aufgaben mit Benutzervariablen; Gruppensumme zählt auch blockierte Zeilen.

## Abschlusstest auf dem echten Rechner
- [x] A: Installieren, Startmenü, „Apps“-Name, Zugriffsrechte `C:\ProgramData\WinWaechter` (2026-11-08)
- [x] A2: Start direkt nach der Installation (Fehler 740 behoben, 2df3905)
- [x] B: Optimieren + Rückgängig an Test-Einträgen – Autostart, Aufgabe, Temp-Dateien exakt wiederhergestellt; zusätzlich echte Sitzung mit Diensten/Aufgabe rückgängig gemacht (2026-11-09)
- [x] C: Defender-Schnellscan (44 s) und Abbrechen (2026-11-09)
- [x] D: GitHub-Repo `yIrtIcIkurt/winwaechter`, Releases v0.1.0 + v0.1.1, Selbst-Update 0.1.0 → 0.1.1 (2026-11-09)
- [ ] Dunkles Design, 150 % Skalierung, Tastaturbedienung im echten Fenster
- [ ] Deinstallation (Daten bleiben standardmäßig)

Beim Test gefunden und behoben: Start nach Setup (740), zu schnelles Scrollen mit Touchpad, „Prozess beenden“ blieb klickbar, Verlauf wählte nach dem Anwenden die alte Sitzung.

## 0.3.0 (veröffentlicht 2026-11-10, 10791df)
App-Übersicht + offizielles/erzwungenes Deinstallieren mit Reste-Suche, Sicherheits-Check (Ampel), Regeln-Update von GitHub, Feinschliff + Icon. Design: `docs/superpowers/specs/2026-11-10-winwaechter-0.3-design.md`.

Dazu: Autor „Tahsin Yilmaz“ (Über-Bereich, Installer, Dateieigenschaften, README).
- [x] Selbst-Update 0.2.1 → 0.3.0 auf dem Rechner, Dateieigenschaften + Apps-Eintrag mit Autor
- [x] Regel-Update live: Regeln Version 2 (Restoro) geladen, Cache in `ProgramData\WinWaechter\Regeln` (Besitzer Administratoren, gleiche Bytes wie GitHub), nach Neustart „Regeln: Version 2“ (2026-11-10)
- [ ] Programme- und Sicherheit-Seite hell/dunkel, 150 %, Tastatur

## 0.3.1 (Fehlerbehebung)
- Geräteverschlüsselung war immer „Nicht prüfbar“: WMI-Abfrage ohne Schlüssel `DeviceID` → `GetConversionStatus` scheiterte.
- Firewall bei fremdem Schutzprogramm bleibt grün, auch wenn die Windows-Firewall nicht lesbar ist.
- WMI-Ergebnislisten werden freigegeben; Regel-Tests zählen ab der mitgelieferten Version.
- [x] Auf dem Rechner geprüft (2026-11-10): „Geräteverschlüsselung“ zeigt Grün („Systemlaufwerk verschlüsselt“).

## 0.3.2
- Benutzer-Programme (HKCU, AppData) startet WinWächter direkt mit den normalen Rechten des angemeldeten Benutzers (Explorer-Token, `CreateProcessWithTokenW`); Umweg über die Einstellungen nur noch bei Fehler 740 o. Ä. Spec 0.3 §7.1 ergänzt.
- Touchpad-Scrollen auch in der Programmliste gleichmäßig; Pfeiltasten/Bild/Pos1/Ende/Leertaste scrollen.
- Sortierfeld zeigt „Name“ statt `ProgramSortOption { … }`.
- [x] Live-Test 2026-11-10: VAVOO direkt deinstalliert (erster Versuch: Fehler 5, Token-Rechte ergänzt), Scroll-Tempo vom Nutzer bestätigt.
- Offen aus der Sicherheitsprüfung (klein): Selbst-Elevation verlässt den Job; Kommandozeile > 1024 Zeichen fällt auf Einstellungen zurück; HKLM-Deinstaller im Profil eines anderen Benutzers läuft als angemeldeter Benutzer.

## 0.4.0 (veröffentlicht 2026-11-11)
Design `docs/superpowers/specs/2026-11-10-winwaechter-0.4-design.md`, Plan `docs/superpowers/plans/2026-11-10-winwaechter-0.4.md`. 783 Tests, Einzel- und Gesamtprüfung.
- [x] Auf dem Rechner: Seite öffnet, Datenträger-Ersatzmessung (IOCTL) ohne Warnung im Protokoll
- [x] §4.3 Energiemodus (Akku → Ausgewogen) erscheint in den Windows-Einstellungen (über die powrprof-API; der Registry-Weg ist auch für Admins gesperrt)
- [x] Browser-Erweiterungen: eigene Einschätzung, Knopf „In {Browser} entfernen“ für alle außer Richtlinie. Chromium nimmt `edge://`-Adressen von außen nicht an → Adresse wird kopiert, Browser mit `--new-window` geöffnet, Nutzer fügt ein (vom Nutzer bestätigt)
- [ ] Vom Nutzer übersprungen (veröffentlicht auf Wunsch ohne): Rückgängig des Energiemodus, Energiemodus nach Neustart, §4.9 Hintergrund-Apps (`Disabled`/`DisabledByUser`), je eine Änderung aus Effekte/Spielmodus/Speicher, Ruhezustand, Start-Bericht gegen das echte Protokoll
Bekannte Kleinigkeiten (aus den Prüfungen): Liste wird nach Undo im Verlauf erst beim nächsten Öffnen aktualisiert; Wechsel des Energiesparplans bei offener Seite wird erst nach „Neu lesen“ erkannt; ein einzelnes kaputtes Ereignis macht den Start-Bericht unlesbar.

## 0.4.0 Plan (Entscheidung Nutzer 2026-11-10)
Neue Seite „Leistung“: Energiemodus (Netz/Akku), visuelle Effekte, Startzeit-Bericht, Live-Monitor (CPU/RAM/Datenträger/Top-Verbraucher), Hintergrund-Apps, Spielmodus + GPU-Planung, Ruhezustand-Datei (mit Hinweis), Speicheroptimierung. Alles einzeln, umkehrbar; keine „Tuning-Tricks“ (RAM-Booster, Prefetch löschen, Dienste massenhaft abschalten).

## 0.5.0 (veröffentlicht 2026-11-14)
Design `docs/superpowers/specs/2026-11-12-winwaechter-0.5-design.md`, Plan `docs/superpowers/plans/2026-11-12-winwaechter-0.5.md`. 1050 Tests (vorher 783), Einzelprüfungen je Aufgabe und Gesamtprüfung (mit Nachbesserungen). Inhalt: „Rechner in Ordnung bringen“, Sicherheit behebt selbst, Leistungsbremsen, Werbe-Apps, Windows-Werbung, Computerschutz, Browser-Hintergrund, Rechtsklick-Menü, kleinere Lücken (siehe CHANGELOG).

Nur lesende Prüfungen auf dem Rechner des Nutzers (Build 26200, nicht erhöht):
- Computerschutz: `VSS` und `swprv` stehen beide auf Manuell/Gestoppt (keiner deaktiviert, die Ursache von 0x80070422 ist damit nicht sichtbar); `RPSessionInterval`=0 (`DiskPercent` 15); kein `DisableSR`, kein `SystemRestore`-Richtlinienschlüssel; die Liste der Wiederherstellungspunkte ist ohne Administrator nicht lesbar.
- Werbe-Apps: alle 8 Kandidaten installiert, mit genau diesen Namen: `Clipchamp.Clipchamp`, `Microsoft.Copilot`, `Microsoft.OutlookForWindows`, `MSTeams`, `Microsoft.MicrosoftOfficeHub`, `Microsoft.Windows.DevHome`, `MicrosoftCorporationII.QuickAssist`, `Microsoft.MicrosoftStickyNotes`; keiner ist NonRemovable/System, keiner entfällt. `GamingApp`/`GamingServices` sind installiert und werden nie angeboten. `Get-AppxProvisionedPackage` braucht Administratorrechte. Die Store-Namen von TikTok, Spotify, Disney+, Facebook, Instagram, LinkedIn und WhatsApp sind bekannte Namen und auf diesem Rechner nicht prüfbar.
- Windows-Werbung: alle 3 geprüften Schlüssel existieren, kein Kandidat entfällt. Vorhandene Werte: `SystemPaneSuggestionsEnabled`=1, `RotatingLockScreenOverlayEnabled`=0, `ShowSyncProviderNotifications`=0, `ScoobeSystemSettingEnabled`=0; `Start_AccountNotifications` fehlt (Schlüssel vorhanden), deshalb Vorher/Nachher-Vergleich in der Liste unten.
- Browser-Hintergrund: Edge hat 11 fremde Richtlinien, darunter `StartupBoostEnabled`=0 → Zeile zeigt „Von einer Richtlinie festgelegt“; Edge `BackgroundModeEnabled` nicht gesetzt (wird angeboten); Chrome ohne Richtlinienschlüssel (wird angeboten, mit „verwaltet“-Hinweis); Opera: kein gleichwertiger Schalter.

Offen, nur mit Zustimmung des Nutzers, nichts davon hat der Umsetzer allein gemacht:
- [ ] 1. Computerschutz über WinWächter einschalten; danach klappt ein Wiederherstellungspunkt (kein 0x80070422 mehr). Vorher prüfen, ob `RPSessionInterval`=0 in Systemeigenschaften → Computerschutz „Aus“ entspricht (sonst bietet die Zeile „Einschalten“ an, obwohl der Schutz an ist)
- [ ] 2. „Rechner in Ordnung bringen“ einmal komplett durchlaufen, Ergebnis ansehen, im Verlauf als Ganzes zurücknehmen. Wurde im laufenden Programm noch nie geklickt (Unteragenten können die Oberfläche nicht bedienen); das ist der erste echte Lauf
- [ ] 3. Windows-Werbung: je Kandidat Vorher/Nachher-Vergleich in den Windows-Einstellungen (Schalter umlegen, Wert lesen, zurücklegen); nicht passende Einträge vor dem Release entfernen; eine Werbe-Änderung anwenden und zurücknehmen
- [ ] 4. Sicherheit: eine Behebung (z. B. Firewall-Regel für ein lauschendes Programm) anwenden und zurücknehmen
- [ ] 5. Werbe-Apps: eine entfernen und zurücknehmen (bzw. Store-Knopf prüfen)
- [ ] 6. Browser: Edge-/Chrome-Hintergrund ausschalten, „verwaltet“-Hinweis prüfen, zurücknehmen; die fremde `StartupBoostEnabled`-Richtlinie bleibt unangetastet
- [ ] 7. Rechtsklick: einen Eintrag abschalten, „Explorer neu starten“, zurücknehmen; Windows muss die Oberfläche innerhalb von 15 s selbst zurückbringen (WinWächter startet sie nicht mehr; sonst erscheint der Hinweis auf Strg+Umschalt+Esc)
- [ ] 8. Updates installieren nur, wenn der Nutzer es will
- [ ] 9. Neue/geänderte Seiten hell/dunkel, 150 %, Tastatur ansehen
- [ ] Vor dem Release: `rules/rules.json` hat sich geändert (`rulesVersion` 3, `bloatApps`, Werbe-Einträge) → `rules/rules.json.sig` muss beim Release neu signiert werden (`build-release.ps1`), vor dem Merge nach main, sonst lehnen Clients das Regel-Update ab
- [ ] Vor dem Test des neuen Builds die installierte App schließen (lief beim Bauen erhöht, PID 1808)
- [ ] Release und Veröffentlichung erst nach Freigabe des Nutzers

## 0.5.1 (veröffentlicht 2026-11-14)
Branch `feature/v0.5.1`, 1066 Tests, Prüfung mit Nachbesserung. Inhalt siehe CHANGELOG: die vier für 0.5.1 vorgemerkten Punkte (Schutzprüfung bei „Erneut versuchen“ für Firewall-Regel, Rechtsklick und Richtlinienwerte; Besitz-Regel im Datenschutz; ein Defender-Vorgang zugleich über alle Seiten; Aufräumen in AppServices) und der vom Nutzer gemeldete Fehler, dass die Programmliste in einem niedrigen Fenster nicht erreichbar war (Seite scrollt jetzt als Ganzes, Liste ohne Virtualisierung, Suche mit 200 ms Verzögerung).

Offen, nur mit Zustimmung des Nutzers:
- [ ] Programme-Seite im echten Fenster ansehen (niedriges Fenster, 150 %, Mausrad über Apps-Kasten und Liste, Tastatur)
- [ ] Release erst nach Freigabe des Nutzers (`rules.json` unverändert seit 0.5.0, die Signatur passt)

Bekannt, nicht behoben: Eine abgelehnte Wiederholung erscheint im Protokoll als Fehler mit dem Titel „Erneut versuchen fehlgeschlagen“ (nur Wortwahl).

## 0.6.0 (gebaut, unveröffentlicht, Stand 2026-11-20)
Design `docs/superpowers/specs/2026-11-14-winwaechter-0.6-design.md`, Plan `docs/superpowers/plans/2026-11-14-winwaechter-0.6.md`, Branch `feature/v0.6`. Nutzer-Entscheidung 2026-11-14: Die automatische Wartung prüft und meldet nur, sie ändert nichts. Inhalt siehe CHANGELOG: Programm-Updates (winget), Gerät, Reparatur, automatische Prüfung, Speicherplatz-Karte, winget-Nutzungsdaten.

Gesamtprüfung (unabhängiger Prüfer) mit „With fixes“: zwei kritische Funde (winget-Ausgabe im Fenster falsch dekodiert; Speicherplatz hätte Ordner mit Lexware-Daten oder ganze Bibliotheksordner verschieben können) und acht wichtige Funde behoben, je mit Test. Alle Entscheidungen und zurückgestellten Kleinigkeiten: `docs/superpowers/plans/2026-11-14-winwaechter-0.6-ledger.md`. 1278 Tests, fünfmal hintereinander grün.

Nur lesende Prüfungen auf dem Rechner des Nutzers (nicht erhöht): `winget upgrade` liest sich vollständig (31 Zeilen = Fußzeile, nur 4 Spalten bei `--source winget`); SSD „2650 Micron 1024GB“ Healthy, Akku-Vollkapazität lesbar; Scan von C: 75 s für 1,8 Mio. Dateien; die Defrag-Statistik `Dfrg\Statistics\Volume{…}\LastRunTime` ist lesbar.

Offen, nur mit Zustimmung des Nutzers, nichts davon hat der Umsetzer allein gemacht:
- [x] 1. Den 0.6.0-Build installieren (vorher die laufende App schließen)
- [x] 2. Programme: Update-Liste entspricht `winget upgrade` ohne die Dell-Pakete (und ohne etwaige Treiber-Pakete aus `updateBlock`), Namen mit Umlauten und „…“ korrekt (nach dem UTF-8-Fix nur im echten Fenster prüfbar); 7-Zip über WinWächter aktualisieren — 27 = 29 − 2 Dell; 7-Zip 26.00→26.06; Lauf 2026-11-16: 24 aktualisiert, 1 fehlgeschlagen (VirtualDesktop.Streamer, 0x8A15008E); Lauf 2026-11-20: 6 aktualisiert, 2 fehlgeschlagen (ChatGPT 0x80073D28, VirtualDesktop.Streamer 0x8A15008E)
- [x] 3. Gerät: Akku (≈42,8 Wh voll) und SSD-Werte plausibel — 47,3/63,1 Wh, 25 % Verschleiß, 279 Zyklen; SSD 0 %, 52 °C
- [x] 4. Reparatur: „DNS-Cache leeren“ und „Datenträger prüfen“ (`/scan`); andere Reparaturen nur auf Wunsch — beide ok; „Empfohlen“ bei Systemdateien war echt (DISM: reparierbar); Lauf: 1197 von 1198 repariert, Rest `smbdirect.sys` 26100.1301 ohne Quelle (0x800F0915), sfc ohne Fehler
- [ ] 5. Automatische Prüfung einschalten, die Aufgabe einmal in der Aufgabenplanung mit „Ausführen“ starten, Meldung erscheint, „Ansehen“ öffnet „Rechner in Ordnung bringen“ (mit UAC); danach ausschalten → Aufgabe ist weg
- [ ] 6. Speicherplatz-Karte von C: lädt; eine Testdatei in Downloads in die Ablage verschieben und im Verlauf zurückholen
- [ ] 7. winget-Nutzungsdaten abschalten und zurücknehmen; die `settings.json` von winget behält ihre Kommentare
- [ ] 8. Neue Seiten hell/dunkel, 150 %, Tastatur ansehen
- [ ] 9. Neuen Build (mit den Behebungen unten) installieren und nachprüfen: Stand pro Zeile bei einem Update-Lauf, ChatGPT-Update, Reparatur-Zeile „Systemdateien“ (Knopf „Jetzt neu installieren“, kein „Empfohlen“), Seite „Sicherheit“ → Computerschutz

Aus der Checkliste, vor dem Release behoben (Wunsch Nutzer, gebaut 2026-11-20 in 7 Commits, je mit Test; 1305 Tests grün). Ohne eigenen Test: Zeilenstand auf „Leistung“ (nur Verdrahtung, Grundlage getestet) und die Protokoll-Zusammenfassung (globales FileLog). Die Computerschutz-Prüfung pro Laufwerk ist nur mit Fake-Registry getestet (`SPP\Clients` ist ohne Administrator nicht lesbar). 0x8A15008E-Pakete werden weiter vorausgewählt, scheitern aber mit Klartext.
- [x] Stapel-Listen zeigen den Stand je Zeile (wartet / läuft mit Balken / ✓ fertig / ✗ fehlgeschlagen mit Grund): Programm-Updates, Vorinstallierte Apps entfernen, „Rechner in Ordnung bringen“, Optimieren/Datenschutz beim Anwenden mehrerer Einstellungen
- [x] Programme: die untere Liste der installierten Programme lädt nach einem Update-Lauf neu (zeigte 7-Zip weiter mit 26.00)
- [x] Reparatur: das Ergebnis steht doppelt unter der Zeile („Zuletzt: … – X“ und darunter nochmal X)
- [x] Reparatur: DISM-Fehler verständlich statt „Code -2146498283“ (0x800F0915/0x800F081F/0x800F0906 = Reparaturdateien nicht ladbar) mit Knopf zu Einstellungen → Wiederherstellung („Jetzt neu installieren“, `ms-settings:recovery`)
- [x] Reparatur: „Empfohlen“ nicht erneut zeigen, wenn der letzte Lauf genau daran scheiterte (sonst Dauerempfehlung, die nicht hilft) — bestätigt 2026-11-20: nach Neuladen weiter „Empfohlen“; gilt auch für die wöchentliche Prüfung und „Rechner in Ordnung bringen“ (Vorschlag `repair-sfc`). Die doppelte Ergebniszeile erscheint nur direkt nach dem Lauf
- [x] winget 0x8A15008E (UPDATE_INSTALL_TECHNOLOGY_MISMATCH) in Klartext: „Das Update nutzt eine andere Installationsart; bitte im Programm selbst aktualisieren“, solche Pakete künftig nicht vorauswählen
- [x] winget 0x80073D28 bei ChatGPT (MSIX `OpenAI.Codex`, 2026-11-20): AppX-Protokoll „Zum Installieren des Paketdienstes sind Administratorrechte erforderlich“ — die neue Fassung bringt einen Paketdienst mit, der Lauf als Benutzer reicht nicht. Prüfen, wie WinWächter MSIX-Pakete startet (UserProcessRunner); bei 0x80073D28 erhöht wiederholen, sonst Klartext
- [x] Wiederherstellungspunkt scheiterte mit 0x80070422 (Dienst deaktiviert): Ursache finden (Computerschutz/Richtlinie?), verständliche Meldung samt Behebung — vermutete Ursache: Schutz nur für C: aus (`RPSessionInterval`=1 täuschte „an“); jetzt Prüfung pro Laufwerk, Rückfrage nennt den Grund. Geräteprüfung 2026-11-20: Computerschutz zeigt „Eingeschaltet“ (RPSessionInterval inzwischen 1; bei 0.5.0 war er 0, also am 15.11. vermutlich noch aus und seither eingeschaltet), der Update-Lauf um 21:53 lief ohne Rückfrage zum Wiederherstellungspunkt. ChatGPT-Update per erhöhter Wiederholung geklappt, Virtual Desktop zeigt den Klartext. Nachgebessert: alte Reparatur-Einträge von vor der Behebung („Code -2146498283“, ohne Link) werden wie neue gelesen
- [x] Protokoll: gesperrte Temp-Dateien beim Aufräumen nicht je Datei als WARN mit Stacktrace, sondern eine Zusammenfassung
- [x] Reparatur-Seite mit dem neuen Installer nachgeprüft (2026-11-20): kein „Empfohlen“, Klartext 0x800F0915, Knopf „Jetzt neu installieren“
- [x] Lizenz: MIT (Entscheidung Nutzer 2026-11-20), `LICENSE` im Repo und als `LICENSE.txt` im Programmordner; README mit Datenschutz-Satz und „Code signing policy“

- [ ] Vor dem Release: `rules/rules.json` hat sich geändert (`rulesVersion` 4, `updateBlock`) → `rules.json.sig` neu signieren (`build-release.ps1`); Release erst nach Freigabe

Bekannt, offen: Der Testprozess stürzte in vollen Testläufen manchmal ab (Zugriffsverletzung in coreclr bzw. Fail-Fast in ucrtbase), immer während des Tests mit 1200 echten Dateiverschiebungen, wenn gleichzeitig der echte PDH-Messtest lief. Beide laufen jetzt allein (eigene Testsammlung), seither stabil. Die native Ursache ist nicht gefunden; dafür braucht es einen Absturz-Dump und einen Debugger.

## Code-Signatur (Entscheidung Nutzer 2026-11-20)
Anlass: Auf einem anderen Rechner blockierte Windows `WinWaechter.App.exe` nach dem Setup („ShellExecuteEx schlug fehl; Code 4551. Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert“), vermutlich die intelligente App-Steuerung. App und Setup sind nicht Authenticode-signiert. Weg: kostenlose Open-Source-Signatur der SignPath Foundation (Herausgeber im Zertifikat ist dann „SignPath Foundation“). 0.6.0 erscheint noch unsigniert.
- [x] OSI-Lizenz (MIT), Datenschutz-Satz und „Code signing policy“ im README
- [ ] Nutzer: Zwei-Faktor-Anmeldung bei GitHub einschalten
- [ ] Nutzer: Antrag auf signpath.org/apply stellen (Angaben bereitet Claude vor)
- [x] GitHub-Actions-Ablauf auf GitHub-Runnern (`.github/workflows/build.yml`, windows-2025): Tests, Publish, bei Tag `v*` Programmdateien signieren lassen, Setup bauen, Setup signieren lassen; Prüfsummen und `rules.json.sig` signiert weiter der lokale Schlüssel über `release-ci.ps1` (bleibt aus GitHub heraus). Artefakt-Konfigurationen für SignPath in `.signpath/artifact-configurations/` (`app`, `setup`). Gebaut 2026-11-20, lokal nur Syntax geprüft
- [x] GitHub-Lauf grün (2026-11-21, Lauf 44835463862 auf e3ed885: Tests, Publish, Setup; Artefakt `WinWaechter-Setup-0.6.0`, 52 MB). Branch `feature/v0.6` ist seit 2026-11-20 auf GitHub (Freigabe Nutzer)
- [ ] Setup aus dem GitHub-Artefakt einmal installieren (unsigniert, also nur auf einem Rechner ohne eingeschaltete App-Steuerung)
- [x] Risiko beseitigt (2026-11-21, Entscheidung Claude nach Rückfrage; Nutzer will keine Funktion verlieren): `Microsoft.Win32.TaskScheduler.dll` (NuGet, unsigniert) entfernt; `WinTaskStore` spricht die Aufgabenplanung jetzt über Windows' eigene COM-Schnittstelle (`Schedule.Service`) an. Echttest: Anlegen/Aus/Ein/Export/Neu-Registrieren/Löschen einer Testaufgabe; `List()` liefert dieselben 194 Aufgaben wie `Get-ScheduledTask`
- [x] Erster GitHub-Lauf 2026-11-20: 2 von 1306 Tests scheiterten nur am GitHub-Rechner (UTC-Zeitzone; Windows Server ohne Diagnostics-Performance-Protokoll) → Tests maschinenunabhängig gemacht; Nullability-Warnung des neueren SDK behoben
- [ ] Nach der Zusage: SignPath-Projekt einrichten (Zwei-Faktor auch dort), README-Satz „Free code signing provided by SignPath.io, certificate by SignPath Foundation“ auf Startseite und Release-Seiten, erste signierte Version auf einem Rechner mit eingeschalteter App-Steuerung prüfen

## Danach
Mehrsprachigkeit Deutsch, Englisch, Türkisch (App, Regeltexte, Installer), früher für 0.5.0 geplant (Entscheidung Nutzer 2026-11-10). Startsprache wie Windows, sonst Englisch; umschaltbar in den Einstellungen. Türkische Texte vom Nutzer gegenlesen lassen.

## Nach 0.1.x (Entscheidung Nutzer 2026-11-08)
- Signierte `rules.json` direkt aus dem eigenen GitHub-Repo aktualisieren (ohne neues Release, ohne Drittanbieter-Listen). Defender bleibt die Scan-Engine.
- Weitere Ideen: Sicherheits-Check, App-Übersicht, große Dateien/Duplikate, winget-Updates, wöchentliche Prüfung.

## Bekannte kleine Punkte
- Tag `v0.1.1` auf GitHub zeigt auf aa249ae statt 7114d33 (nur Quellcode-ZIP betroffen; Korrektur braucht Force-Push durch den Nutzer).
- Wiederherstellungspunkt scheitert, solange der Computerschutz-Dienst aus ist (0x80070422) – App meldet es, eigene Sicherung greift.
- Langes Zurückverschieben vieler Temp-Dateien zeigt keinen Fortschritt.
- History-Ansicht bricht ab, wenn ein Ablage-Ordner nicht lesbar ist; Link-Warnung bei jeder Vorschau im Log.
- Installer wertet Rückgabecodes von icacls nicht aus (App sichert den Ordner beim Start selbst).
- Zwischen Pfadprüfung und Verschieben bleibt ein sehr kleines Zeitfenster (Lösung: handle-basiertes Verschieben).
- Von Angreifern vorab angelegte Dateien wie `settings.json` bleiben in deren Besitz (Auswirkung: Ablage frühestens nach 1 Tag geleert).
- Rechteprüfung des Datenbaums läuft beim Start im UI-Thread.
- Unsignierte Programme mit geschütztem Herstellernamen in AppData: nur „Von Defender prüfen lassen“ möglich.
- Fehlgeschlagener Datei-Datensatz zeigt einen Rückgängig-Knopf, der dann „nicht mehr vorhanden“ meldet.
- Pfade über OneDrive-Platzhalter, eingebundene Ordner oder Netzlaufwerke werden beim Verschieben übersprungen.
- App-Icon fehlt: Bild-Prompt in `assets/README.md`, danach `tools/make-ico.ps1`.
