# WinWächter 0.2.0 – Design: Cleaner, Deinstallationsreste, Datenschutz

Stand: 2026-11-09 · baut auf `2026-11-08-winwaechter-design.md` (0.1.x) auf; alle Grundsätze dort gelten weiter (erst zeigen, dann ändern, alles umkehrbar, Schutzliste geht vor, keine Telemetrie).
Der Nutzer hat die Rückfragen übersprungen („leg los“); alle Entscheidungen unten sind **Entscheidungen des Bauenden**, außer wo „Nutzer“ steht.

## 1. Absicht
Nutzer-Wunsch: einen Cleaner für überflüssige Dateien, das Säubern von Deinstallationsresten und einen Datenschutz-Check (vom Nutzer aus der Ideenliste gewählt). Rechner: Windows 11 Home 25H2, Copilot+-Laptop (Recall vorhanden), Browser Edge, Chrome, Opera. Messung 2026-11-09: Windows-Update-Downloadcache ~4 GB, `%LOCALAPPDATA%\CrashDumps` ~1 GB.

## 2. Ziele
1. **Erweiterter Cleaner** im Bereich „Optimieren“: neue Gruppen mit Größenangabe, alles über die Ablage (umkehrbar).
2. **Deinstallationsreste**: verwaiste Ordner und verwaiste Einträge (Autostart/Aufgabe/Dienst ohne Programmdatei).
3. **Datenschutz-Check**: neue Seite „Datenschutz“ mit einzelnen Windows-Einstellungen, jede einzeln umschaltbar und im Verlauf rückgängig zu machen.

## 3. Nicht-Ziele / Anti-Ziele
- Kein Registry-Cleaner, kein Löschen von Cookies, Verlauf, Passwörtern oder Sitzungen der Browser – nur Cache.
- Nichts endgültig löschen (Papierkorb leeren, `DISM /ResetBase`, Übermittlungsoptimierung-Cache per Cmdlet) – nicht in 0.2.0.
- Keine Änderung an Datenschutz-Einstellungen ohne Anzeige und Bestätigung; keine „alles aus“-Voreinstellung ohne Häkchen-Vorschau.
- Nichts, was Lexware, Dell, Treiber oder Microsoft-Komponenten beschädigt (Schutzliste aus 0.1 gilt).
- Kein Anfassen von Benutzerdokumenten außer alten Installern in „Downloads“, und die nie vorausgewählt.

## 4. Erweiterter Cleaner
Neue Speicher-Ziele (je eine Finding pro Ziel, Gruppe in der Optimieren-Liste, Größe angezeigt). Technisch: Varianten von `MoveToAblageChange` (Ablage, Sidecar, Junction-Prüfung, Undo wie 0.1).

| Ziel | Pfade | Filter | Vorausgewählt | Bedingung |
|---|---|---|---|---|
| Browser-Cache Edge | `%LOCALAPPDATA%\Microsoft\Edge\User Data\<Profil>\{Cache,Code Cache,GPUCache}` und `...\User Data\{ShaderCache,GrShaderCache}` | alle Dateien | ja | nur wenn kein `msedge.exe` läuft, sonst Info „Edge schließen“ ohne Change |
| Browser-Cache Chrome | `%LOCALAPPDATA%\Google\Chrome\User Data\<Profil>\{Cache,Code Cache,GPUCache}` + Shader-Caches | alle | ja | kein `chrome.exe` |
| Browser-Cache Opera | `%LOCALAPPDATA%\Opera Software\Opera Stable\<Profil oder Wurzel>\{Cache,Code Cache,GPUCache}` (Opera legt Profil `Default` an) | alle | ja | kein `opera.exe` |
| Browser-Cache Firefox | `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2` | alle | ja | kein `firefox.exe` (falls installiert) |
| Windows-Update-Downloads | `%SystemRoot%\SoftwareDistribution\Download` | älter als 7 Tage | ja | nur wenn Dienst `wuauserv` nicht läuft **oder** keine Datei jünger als 1 Stunde (Update gerade aktiv → Info „Windows Update arbeitet gerade“) |
| Absturzabbilder | `%LOCALAPPDATA%\CrashDumps`, `%SystemRoot%\Minidump`, `%SystemRoot%\MEMORY.DMP` | älter als 2 Tage | ja | – |
| Fehlerberichte | `%ProgramData%\Microsoft\Windows\WER\{ReportArchive,ReportQueue}`, `%LOCALAPPDATA%\Microsoft\Windows\WER\{ReportArchive,ReportQueue}` | älter als 2 Tage | ja | – |
| Alte Installer | Ordner „Downloads“ des Nutzers (über Known Folder, nicht fest verdrahtet), nur oberste Ebene, Endungen `.exe .msi .msix .msixbundle .appx` | älter als 30 Tage | **nein** (Info) | eine Finding **pro Datei** mit Größe, Herausgeber und Signaturstatus |

- Profil-Erkennung Chromium: Unterordner von `User Data`, die `Default` oder `Profile N` heißen oder eine `Preferences`-Datei enthalten.
- Prozess-Erkennung über die Prozessliste (Name + Pfad unter dem Browser-Installationsort); läuft der Browser, keine Change, Grund „Browser ist geöffnet – zum Aufräumen schließen“.
- Pfade unter `C:\Windows` sind hier ausdrücklich erlaubt (wie schon `Windows\Temp` in 0.1).
- Gesperrte Dateien werden wie bisher übersprungen und gezählt.

## 5. Deinstallationsreste
### 5.1 Verwaiste Einträge (vorausgewählt, Rating „Entbehrlich“)
Autostart-, Aufgaben- und Dienst-Einträge, deren Programmdatei **nicht existiert** (Pfad aufgelöst wie in 0.1, Datei fehlt, nicht unter `C:\Windows`, nicht geschützt). Grund: „Programmdatei existiert nicht mehr (Deinstallationsrest)“. Aktion = vorhandene Change-Typen (Autostart aus, Aufgabe deaktivieren, Dienst auf Manuell). Umsetzung: Erweiterung der bestehenden Scanner (eigene Reason + Vorauswahl), keine neue Change-Art.

### 5.2 Verwaiste Ordner (nie vorausgewählt, Rating „Info“)
- Durchsucht werden nur die **oberste Ebene** von `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramData%`, `%LOCALAPPDATA%`, `%APPDATA%`, `%LOCALAPPDATA%\Programs`.
- Ein Ordner gilt als verwaist, wenn **alle** zutreffen:
  - kein installiertes Programm passt: Vergleich (normalisiert, Groß/Klein egal, Wortanfang) des Ordnernamens mit `DisplayName`, `Publisher`, `InstallLocation`, `DisplayIcon`, `UninstallString` aller Einträge unter `HKLM\...\Uninstall`, `HKLM\WOW6432Node\...\Uninstall`, `HKU\<SID>\...\Uninstall`, plus Appx-Paketnamen/-Herausgeber des Nutzers;
  - nicht auf der Ausschlussliste (fest: `Microsoft*`, `Windows*`, `Common Files`, `Packages`, `Temp`, `Programs`, `Package Cache`, `Comms`, `ConnectedDevicesPlatform`, `D3DSCache`, `PlaceholderTileLogoFolder`, `Publishers`, `Mozilla`, `Google`, `Opera Software`, `npm`, `pip`, `uv`, `NuGet`, `.NET`-Ordner, `dotnet`, `JetBrains`, `Steam`… — als Liste in `rules.json` unter `leftoverIgnore`);
  - nicht geschützt (Schutzliste, Pfad-Präfixe, Namen) und kein laufender Prozess hat sein Programm darin;
  - jüngste Änderung irgendeiner Datei darin älter als 30 Tage;
  - enthält keine Reparse-Points (sonst übersprungen).
- Anzeige: Ordner, Größe, letzte Änderung, Grund „Kein installiertes Programm gefunden“. Aktion: **ganzen Ordner in die Ablage** (neue Variante `MoveToAblageChange.ForFolder`, Undo stellt den Ordner vollständig wieder her).
- Deutlicher Hinweis auf der Gruppe: „Vor dem Verschieben prüfen – manche Programme hinterlassen absichtlich Daten (z. B. Spielstände, Lizenzen).“

## 6. Datenschutz-Check (neue Seite „Datenschutz“)
Jede Einstellung = eine Zeile mit Titel, kurzer Erklärung (Deutsch), aktuellem Zustand („An“/„Aus“/„Nicht vorhanden“) und Häkchen „ausschalten“. Vorausgewählt ist nichts – oben ein Knopf „Empfohlene auswählen“, der alle als „empfohlen“ markierten anhakt; dann „Ausgewählte anwenden (n)“ mit Bestätigung, Journal, Verlauf, Undo.

Technik: neue Change-Art **`regvalue`** – setzt genau einen Registry-Wert; Before = alter Wert + Typ oder „fehlte“, After = neuer Wert; Undo stellt exakt her bzw. löscht; Drift wenn der Wert inzwischen anders ist. Nur Werte aus der **festen Liste** in `rules.json` (`privacy`), nie beliebige Schlüssel. HKCU-Werte über `HKU\<SID>` (wie 0.1). `IRegistry` braucht dafür das Lesen des Werttyps (Erweiterung `GetKind`).

Liste für 0.2.0 (alle „empfohlen“, außer markiert):

| Titel | Schlüssel / Wert → Ziel |
|---|---|
| Werbe-ID für Apps | `HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo` `Enabled` → 0 |
| Maßgeschneiderte Angebote aus Diagnosedaten | `HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy` `TailoredExperiencesWithDiagnosticDataEnabled` → 0 |
| Werbung/Vorschläge im Startmenü und in Einstellungen | `HKCU\...\ContentDeliveryManager` `SystemPaneSuggestionsEnabled`, `SubscribedContent-338388Enabled`, `SubscribedContent-338393Enabled`, `SubscribedContent-353694Enabled`, `SubscribedContent-353696Enabled` → 0 (eine Zeile, mehrere Werte = mehrere Changes) |
| Automatisch installierte Vorschlags-Apps | `HKCU\...\ContentDeliveryManager` `SilentInstalledAppsEnabled` → 0 |
| Tipps und Hinweise | `HKCU\...\ContentDeliveryManager` `SubscribedContent-338389Enabled`, `SoftLandingEnabled` → 0 |
| Empfehlungen im Startmenü | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` `Start_IrisRecommendations` → 0 |
| Programmstarts verfolgen (für Startmenü) | `HKCU\...\Explorer\Advanced` `Start_TrackProgs` → 0 (nicht empfohlen – ändert „Meistverwendet“) |
| Bing-/Webvorschläge in der Suche | `HKCU\Software\Policies\Microsoft\Windows\Explorer` `DisableSearchBoxSuggestions` → 1 |
| Sprachliste für Websites | `HKCU\Control Panel\International\User Profile` `HttpAcceptLanguageOptOut` → 1 |
| Feedback-Abfragen | `HKCU\Software\Microsoft\Siuf\Rules` `NumberOfSIUFInPeriod` → 0 |
| Recall (Momentaufnahmen speichern) | `HKCU\Software\Policies\Microsoft\Windows\WindowsAI` `DisableAIDataAnalysis` → 1 |
| Aktivitätsverlauf | `HKLM\SOFTWARE\Policies\Microsoft\Windows\System` `PublishUserActivities` → 0 und `UploadUserActivities` → 0 |
| Diagnosedaten auf Minimum | `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection` `AllowTelemetry` → 1 (Home kennt kein 0; Erklärung: „nur erforderliche Diagnosedaten“) |

- Zustand „An“ = Wert fehlt oder ≠ Ziel; „Aus“ = Wert = Ziel.
- Hinweis-Zeile: „Manche Änderungen wirken erst nach Ab-/Anmelden oder Neustart.“
- Policy-Werte unter `...\Policies\...` können von Windows-Updates nicht zurückgesetzt werden; normale Werte manchmal schon – die Seite zeigt immer den aktuellen Zustand.
- Copilot-App: nicht in 0.2.0 (Deinstallation ist keine Einstellung; App-Übersicht kommt später).

## 7. Oberfläche
- Optimieren: neue Gruppen „Browser-Cache“, „Windows-Reste“ (Update-Downloads, Absturzabbilder, Fehlerberichte), „Alte Installer“, „Verwaiste Einträge“ (die bestehenden Autostart/Aufgaben/Dienste-Findings mit dem neuen Grund werden dort einsortiert), „Verwaiste Ordner“ (mit Hinweistext). Je Gruppe Summe der Größen.
- Neue Navigationsseite **Datenschutz** zwischen „Schutz“ und „Verlauf“, gleiche Gestaltung wie die anderen Seiten (Design-Notizen `docs/design-notes.md` gelten).
- Übersicht: zusätzliche Kachel „Frei werdender Speicher“ nach einer Prüfung.

## 8. Rahmen
- Version 0.2.0 (`Directory.Build.props`), CHANGELOG-Abschnitt, Release über `build-release.ps1 -Version 0.2.0 -Publish` nach Zustimmung.
- Tests wie in 0.1: Fakes/Temp-Ordner, keine echten Systemänderungen im Test.
- Fertig heißt: baut, Tests grün, Installer gebaut, auf dem Rechner (mit Zustimmung) Cleaner + Undo, ein verwaister Testordner + Undo, zwei Datenschutz-Einstellungen + Undo geprüft, Update 0.1.1 → 0.2.0 über die App.

## 9. Entscheidungen nach der Prüfung (verbindlich, gehen den Abschnitten 4–7 vor)
1. **Aufbewahrung je Art:** Reine Caches (Browser-Cache, Windows-Update-Downloads, Absturzabbilder, Fehlerberichte; Change-Kind `cache`) werden aus der Ablage **automatisch nach 1 Tag** geleert, sonst gilt die Einstellung (14 Tage). **Verwaiste Ordner** (Kind `folder`) werden **nie automatisch** geleert, nur über „Ablage jetzt leeren“; der Bestätigungsdialog nennt die Zahl der enthaltenen Ordner ausdrücklich.
2. **Freier Speicher ehrlich anzeigen:** Die Kachel heißt „Wird frei“ und erklärt: „Caches werden nach 1 Tag endgültig entfernt, oder sofort über Verlauf → Ablage jetzt leeren.“
3. **Verwaiste Ordner eng gefasst:** In 0.2.0 nur `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%LOCALAPPDATA%\Programs`. AppData-, ProgramData-Wurzeln werden **nicht** durchsucht. Zusätzlich Pflicht: der Ordner enthält mindestens eine `.exe` oder `.dll` (positiver Hinweis auf ein Programm). Ordner, die selbst ein Reparse-Point sind oder den Zugriff verweigern, werden übersprungen.
4. **Ordner in einem Schritt:** `ForFolder` verschiebt den ganzen Ordner mit einem `Directory.Move` (gleiches Laufwerk; Kette auf Reparse-Points geprüft wie in 0.1). Scheitert das (Datei in Benutzung), wird nichts verschoben und der Grund gemeldet. Undo verschiebt den Ordner zurück, wenn der Originalpfad frei ist, sonst Konflikt-Meldung.
5. **Windows Update:** Der Update-Cache wird **nicht** angeboten, wenn ein Neustart aussteht (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` existiert oder `PendingFileRenameOperations` gesetzt) oder `TrustedInstaller` läuft. Alter = jüngerer Wert aus Erstell- und Änderungszeit, älter als 7 Tage. Ein erneuter Download durch Windows ist akzeptiert.
6. **Datenschutz-Zustände:** Eine Zeile kann mehrere Werte haben. Zustand „Aus“ = alle Werte haben den Zielwert, „Teilweise“ = einige, „An“ = keiner. Anwenden erzeugt je Wert eine `regvalue`-Change (Verlauf zeigt sie einzeln, „Alles rückgängig“ wie gewohnt). Diagnosedaten: liest zusätzlich den wirksamen Wert `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection` `AllowTelemetry`; ist der ≤ 1, Zustand „Aus (bereits minimal)“ ohne Change. Recall-Zeile nur, wenn die Recall-Komponente vorhanden ist.
7. **Edge im Hintergrund:** Läuft `msedge.exe` (exakt dieser Dateiname im Edge-Installationsordner; `msedgewebview2.exe` zählt nicht), keine Change, Grund: „Edge läuft (auch im Hintergrund). Edge vollständig schließen und erneut prüfen.“ Die App beendet keine Browser.

Technische Korrekturen:
- Opera: Cache unter `%LOCALAPPDATA%\Opera Software\Opera Stable\Default\…`, Shader-Caches (`ShaderCache`, `GrShaderCache`, `GraphiteDawnCache`) unter `%APPDATA%\Opera Software\Opera Stable\`. Profil-Erkennung für Opera: feste Pfade, keine `Preferences`-Prüfung unter Local.
- Chromium zusätzlich `GraphiteDawnCache`, `DawnGraphiteCache`, `DawnWebGPUCache` (Edge, Chrome, Opera).
- Absturzabbilder: `MEMORY.DMP` als Einzeldatei (`ForFile`, Kind `cache`), zusätzlich `%SystemRoot%\LiveKernelReports` (älter als 2 Tage).
- Datenschutz-Liste: Zeile „Werbung/Vorschläge im Startmenü“ wird zu „Vorschläge in Einstellungen und auf dem Sperrbildschirm“ (Werte 338393, 353694, 353696, 338388; `SystemPaneSuggestionsEnabled` entfällt). Zeile „Aktivitätsverlauf“ entfällt (Timeline gibt es nicht mehr). Erklärungstexte nennen Nebenwirkungen: Richtlinien-Werte lassen die Einstellungen-App „Von deiner Organisation verwaltet“ anzeigen und sperren den Schalter; Recall-Richtlinie verhindert auch das Einschalten; Websuche-Richtlinie entfernt auch zuletzt gesuchte Vorschläge im Explorer und wirkt nach Explorer-Neustart/Abmelden.
- Verwaiste Einträge: Aufgaben mit COM-Handler-Aktion (ohne Programmdatei) zählen nie als verwaist; Pfade werden vorher mit `PathNormalizer` aufgelöst; Pfade auf nicht bereiten Laufwerken oder UNC gelten als „unbekannt“, nicht „fehlt“.

## 10. Dem Bauenden überlassen
Klassennamen, genaue Aufteilung der Scanner, Gruppensortierung, Texte im Detail (Deutsch, sachlich, nicht alarmierend), vollständige `leftoverIgnore`-Liste.
