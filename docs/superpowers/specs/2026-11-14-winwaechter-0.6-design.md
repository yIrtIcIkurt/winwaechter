# WinWächter 0.6.0 – Updates, Gerät, Reparatur, Wartung, Speicherplatz (Design)

Stand: 2026-11-14. Grundlage: die vom Nutzer festgelegte Reihenfolge in `docs/roadmap.md` (§0.6.0) und das Gespräch am 2026-11-14. Der Nutzer hat entschieden: **Die automatische Wartung prüft und meldet nur; sie ändert nichts ohne Klick** („A: Nur melden“). Was mit **[Entscheidung Claude]** markiert ist, habe ich festgelegt; der Nutzer kann es beim Lesen korrigieren.

## 1. Zweck und Warum
WinWächter ist ein „Macher, kein Beobachter“ (Nutzer, 2026-11-12): Jeder Fund bekommt eine Behebung per Klick, mit Zustimmung und wenn möglich im Verlauf rückgängig. 0.6.0 füllt die Lücken, die vergleichbare Wartungsprogramme abdecken und WinWächter noch nicht: veraltete Programme, Zustand von Akku und SSD, die üblichen Windows-Reparaturen, eine regelmäßige Prüfung ohne eigenes Zutun und die Frage „Wo ist mein Speicherplatz hin?“.

Zielrechner (bekannt): Dell-Laptop mit Akku (volle Ladung zurzeit 42.818 mWh), NVMe-SSD „2650 Micron 1024GB“ (Windows meldet „Healthy“), Laufwerk C: 997 GB, davon 215 GB frei. Windows 11 Home Build 26200, Konto ist Administrator mit UAC. winget v1.29.380 ist vorhanden, die Quellvereinbarung ist schon angenommen. `winget upgrade` listet 30 Updates, darunter `Dell.Optimizer` und `Dell.DisplayAndPeripheralManager` (Dell darf nie angefasst werden), Laufzeitumgebungen (VC++, .NET Desktop Runtime 6–10) und einen Eintrag mit unbekannter Version (`Ubisoft.Connect`, „< 175.1.0.13416“).

## 2. Ziele / Nicht-Ziele / Anti-Ziele
Ziele (0.6.0), in der Reihenfolge des Nutzers
1. Programm-Updates über winget (4.1)
2. Gerätezustand: Akku, SSD/Festplatten (4.2)
3. Reparatur-Seite: Systemdateien (SFC/DISM), Windows-Update-Reparatur, Netzwerk, Druckerwarteschlange, Symbol-Cache, Datenträgerprüfung (4.3)
4. Automatische Wartung mit Benachrichtigung, **nur melden** (4.4)
5. Speicherplatz-Karte (4.5)

Nicht-Ziele (0.6.0)
- Sprachen DE/EN/TR (kommt danach).
- Automatisch etwas ändern, auch keine „sicheren“ Dinge wie Temp-Dateien oder Defender-Signaturen (Nutzerentscheidung „Nur melden“).
- Treiber-Updates, BIOS-/Firmware-Updates (Dell-Werkzeuge bleiben zuständig und unberührt).
- Updates von Store-Apps (`msstore`-Quelle) und Programmen, die winget nicht kennt. Nur die Quelle `winget` **[Entscheidung Claude]**.
- Ein Programm auf eine ältere Version zurücksetzen.
- Doppelte Dateien finden, Dateien endgültig löschen, ein eigenes Sicherungsprogramm.
- CPU-/Grafikkarten-Temperaturen, Lüftersteuerung, Akku-Ladegrenzen (die laufen auf diesem Rechner über Dell-Software).
- `netcfg -d` (entfernt Netzwerkadapter) und andere Radikal-Resets.

Anti-Ziele (wäre ein Fehlschlag, auch wenn es „funktioniert“)
- Dell, Lexware und alles aus `rules.json` → `protected` aktualisieren, reparieren, verschieben oder sonst anfassen. Das gilt auch für winget-Einträge, die nur über Name oder ID als Dell/Lexware erkennbar sind.
- Die automatische Wartung ändert etwas, öffnet ungefragt Fenster, fordert ungefragt UAC an oder meldet sich so oft, dass sie stört.
- Eine endgültige Aktion, die wie eine umkehrbare aussieht. Updates und Reparaturen lassen sich nicht über den Verlauf zurücknehmen; die Bestätigung sagt das ausdrücklich.
- Daten des Nutzers verlieren: In der Speicherplatz-Karte wird nichts endgültig gelöscht, nichts aus OneDrive-Ordnern verschoben (sonst verschwindet es auf allen Geräten), nichts unter Windows, Programmen oder geschützten Pfaden (z. B. `\Lexware\`) verschoben.
- Ein Knopf, der nur in die Windows-Einstellungen schickt, wo WinWächter es selbst könnte.
- Neue Drittanbieter-Pakete oder Dienste, die Daten sammeln.

## 3. Grundsätze (gelten wie in 0.5 weiter)
- **Änderung** (`IChange`, im Journal, rückgängig im Verlauf) vs. **Aktion** (`SystemAction` über `ActionRunner`: Bestätigung, `ChangeGate`, Protokoll). Fast alles in 0.6.0 ist eine Aktion. Einzige Änderungen: „Datei in die Ablage verschieben“ in 4.5 (vorhandenes `MoveToAblageChange`) und die winget-Telemetrie in 4.1.
- Jede endgültige Aktion hat eine Bestätigung mit „endgültig“ (`Final`), nennt die Dauer, ob ein Neustart nötig ist und was verloren geht. Harmlose Aktionen (DNS-Cache leeren, Laufwerk optimieren, Datenträger nur prüfen) brauchen nur den Klick.
- **Wiederherstellungspunkt** vor Programm-Updates (einer je Durchgang), vor „Windows Update reparieren“ und vor „Netzwerk komplett zurücksetzen“, wenn der Computerschutz an ist **[Entscheidung Claude]**.
  - Windows legt höchstens einen Punkt in 24 Stunden an. Gibt es schon einen jüngeren als 24 h, geht es ohne Rückfrage weiter mit dem Hinweis „Ein Wiederherstellungspunkt von <Zeit> besteht schon“. Dafür muss `RestorePointService` den jüngsten Punkt lesen können.
  - Nur bei einem echten Fehler fragt WinWächter, ob ohne Punkt weitergemacht werden soll.
- **Sperren:** Lange Aktionen (Programm-Updates, DISM/SFC, `chkdsk /scan`, Laufwerk optimieren) halten **nicht** das app-weite `ChangeGate` für ihre ganze Dauer. Sonst wären Anwenden und Rückgängig auf allen Seiten bis zu 30 Minuten blockiert. Sie nutzen eigene Sperren je Art (`UpdateActivity`, `RepairActivity`). Kurze Aktionen laufen wie bisher über `ActionRunner` mit `ChangeGate`. Dafür bekommt `ActionRunner` eine Option „ohne ChangeGate, mit eigener Sperre“ **[Entscheidung Claude]**.
- Schutzliste überall (`RuleSet.Classify` → `Protected`, dazu WinWächter selbst).
- Ohne Adminrechte: alles sichtbar, was lesbar ist, Knöpfe gesperrt mit dem vorhandenen Hinweis.
- Alle Systemzugriffe hinter Schnittstellen mit Fakes (wie `IProcessRunner` / `FakeProcessRunner`), damit alles ohne Admin testbar ist.
- Lange Vorgänge zeigen Fortschritt, die Oberfläche bleibt bedienbar, und es läuft immer nur ein Vorgang derselben Art zugleich (Muster `DefenderActivity` aus 0.5.1).
- Texte: Sie-Form, kurz, je Zeile ein Satz „warum“.
- Nur Windows-eigene Werkzeuge und Schnittstellen (winget, DISM, sfc, chkdsk, defrag, netsh, ipconfig, WMI/CIM, Aufgabenplanung, Windows-Benachrichtigungen).

## 4. Inhalte im Einzelnen

### 4.1 Programm-Updates (winget)
**Ort:** Programme-Seite, neuer Kasten oben „Updates verfügbar (n)“ **[Entscheidung Claude]**. Zusätzlich erscheint „n Programm-Updates verfügbar“ in „Rechner in Ordnung bringen“ unter „Außerdem empfohlen“, mit Sprung auf die Programme-Seite. Updates kommen nie in den Sammelknopf.

**Einlesen** **[Entscheidung Claude]**
- `winget upgrade --source winget --disable-interactivity` über `IProcessRunner` und einen neuen `IWingetClient`. winget hat keine JSON-Ausgabe für Updates, also liest ein Parser die Tabelle.
  - Die Spaltengrenzen kommen aus der Kopfzeile über der Strichlinie (`-----`). Die Spaltenreihenfolge (Name, ID, Version, Verfügbar, Quelle) ist in allen Sprachen gleich; die Überschriften selbst werden nicht ausgewertet.
  - Fortschrittsbalken und Drehzeichen vor der Tabelle sowie Fußzeilen („30 Aktualisierungen verfügbar.“, Hinweise auf unbekannte Versionen oder fixierte Pakete) werden übersprungen.
  - Eine Zeile mit abgeschnittener ID („…“) wird verworfen und protokolliert, nie geraten.
- Zusätzlich `… --scope user`, um Pakete im Benutzerbereich zu erkennen (siehe Installieren).
- Pakete mit unbekannter installierter Version („<“, „Unknown“) werden mit `--include-unknown` gelesen. Sie erscheinen mit dem Hinweis „Installierte Version unbekannt“ und sind nicht vorausgewählt.
- **Schutz:** Die allgemeine Schutzliste `protected` taugt hier nicht. Sie enthält auch Microsoft, Intel, Realtek, NVIDIA und AMD (`rules/rules.json:5-6`), damit WinWächter deren Autostarts und Dienste nicht abschaltet. Für Updates würde sie aber alle Laufzeitumgebungen, PowerToys usw. ausblenden.
  - Deshalb gibt es einen eigenen lokalen Regelabschnitt `updateBlock` (wie `privacy`/`bloatApps`: kommt nie aus dem Netz) mit Matchern für winget-ID-Präfix, Name und Herausgeber **[Entscheidung Claude]**.
    - Inhalt: Dell, Lexware und Haufe (nie anfassen).
    - Dazu Intel, NVIDIA, AMD und Realtek, weil deren winget-Pakete meist Treiber oder Treiber-Werkzeuge sind und Treiber kein Ziel sind.
    - Microsoft-Pakete (VC++, .NET, PowerToys, Terminal) werden angeboten.
  - Ein Paket wird ausgeblendet, wenn
    - es auf `updateBlock` passt, oder
    - es per Name einem installierten Programm (`ProgramInventory`) entspricht, dessen Herausgeber, Name oder Pfad auf `updateBlock` passt, oder
    - es WinWächter selbst ist.
  - Ausgeblendete Pakete zählen nicht mit. Ein Hinweis „n Programme bewusst ausgelassen (geschützt)“ ist aufklappbar.
- winget fehlt oder ist zu alt: Zeile „winget (App-Installer) fehlt“ mit Knopf „App-Installer im Microsoft Store öffnen“ (`ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1`).
- Quellvereinbarung noch nicht angenommen (winget meldet das): Zeile mit Knopf „Bedingungen der winget-Quelle annehmen und suchen“. Die Bestätigung zeigt, dass damit die Bedingungen der Microsoft-Quelle angenommen werden.
- Die Liste wird beim ersten Öffnen der Seite geladen (Hintergrund, mit Ladeanzeige) und über „Neu suchen“ aktualisiert, nicht bei jedem Seitenwechsel.

**Anzeige**
- Je Zeile: Häkchen, Name, „installiert → neu“, „Aktualisieren“.
- Darüber: „Ausgewählte aktualisieren (n)“ und „Alle auswählen“. Vorausgewählt sind alle Updates mit bekannter Version.

**Installieren**
- Je Paket nacheinander: `winget upgrade --id <ID> --exact --source winget --silent --disable-interactivity --accept-package-agreements --accept-source-agreements`.
  - Pakete aus der `--scope user`-Liste laufen im Benutzerkontext, alle anderen im erhöhten WinWächter-Prozess **[Entscheidung Claude]**.
  - Der vorhandene `UserTokenLauncher` startet nur und wartet nicht (`UserLauncher.cs`, `UserProcess.cs`). Dafür braucht es eine neue Variante, die mit dem Benutzer-Token startet, wartet und Rückgabecode sowie Ausgabe liefert.
  - Die ID kommt nur aus der zuletzt gelesenen Liste, nie aus freier Eingabe. Unmittelbar vor dem Start wird die Schutzprüfung wiederholt.
- **Bestätigung (endgültig):**
  - „n Programme werden aktualisiert. Ein Update lässt sich nicht über den Verlauf zurücknehmen.“
  - „Schließen Sie die betroffenen Programme vorher; manche Installationen starten sie neu oder verlangen einen Neustart.“
  - „Mit dem Klick nehmen Sie die Lizenzbedingungen der Updates an.“
- **Fortschritt:** „Paket 3 von 12: Notepad++ …“. „Abbrechen“ wirkt nach dem laufenden Paket; ein laufendes Setup wird nie abgeschossen.
- **Ergebnis je Paket:** aktualisiert / fehlgeschlagen (Grund aus dem winget-Rückgabecode, so weit bekannt, sonst Code und Hinweis auf das Protokoll) / Neustart nötig. Danach wird die Liste neu gelesen.
- Ins Protokoll (`FileLog`) kommen Paket-ID, alte und neue Version, Rückgabecode. Einen Verlaufseintrag gibt es nicht, wie bei allen Aktionen seit 0.5 (der Verlauf zeigt nur umkehrbare Änderungen) **[Entscheidung Claude]**.

**winget-Telemetrie** **[Entscheidung Claude]**
- winget sendet standardmäßig Nutzungsdaten an Microsoft, der Nutzer will keine Datensammlung.
- Auf der Datenschutz-Seite gibt es eine neue Zeile „winget-Nutzungsdaten abschalten“. Sie setzt in der winget-Einstellungsdatei des Benutzers (`…\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\settings.json`) den Wert `telemetry.disable = true`.
- Das ist eine Änderung mit Undo: Vorher-Zustand ins Journal, andere Einträge der Datei bleiben unverändert.
- Die Datei ist JSON mit Kommentaren. Gelesen wird mit übersprungenen Kommentaren. Geschrieben wird als gezielte Text-Änderung, die Kommentare und Formatierung erhält. Lässt sich die Stelle nicht eindeutig finden, wird nicht geschrieben und die Zeile sagt warum.
- Die Zeile gibt es nur, wenn der angemeldete Benutzer auch das erhöhte Konto ist (vorhandene Prüfung `ownAccount`).
- Die Zeile ist empfohlen, also auch in „Rechner in Ordnung bringen“ dabei.

### 4.2 Gerätezustand (neue Seite „Gerät“)
**Akku** (nur wenn vorhanden, `PowerModes.HasBattery`)
- Werte aus WMI `root\wmi`: `BatteryStaticData.DesignedCapacity`, `BatteryFullChargedCapacity`, `BatteryCycleCount`, wenn verfügbar. Fallback: `powercfg /batteryreport /xml` in eine temporäre Datei lesen.
- Anzeige: „Kapazität heute x mWh von ursprünglich y mWh (z % Verschleiß)“, Ladezyklen.
- Stufen **[Entscheidung Claude]**: unter 20 % Verschleiß „gut“; 20–40 % „gealtert“; über 40 % „Akku stark gealtert – ein Austausch lohnt sich, wenn die Laufzeit nicht mehr reicht“.
- Behebung: technisch nicht möglich. Das wird ehrlich so gesagt, ohne „Ansehen“-Knopf als Ersatz.
- Einziger Knopf: „Ausführlichen Akkubericht speichern“ (`powercfg /batteryreport` als HTML in den Downloads-Ordner, danach öffnen). Er ist ein Zusatz, keine Behebung.

**SSD und Festplatten**
- Werte aus CIM `root\Microsoft\Windows\Storage`:
  - `MSFT_PhysicalDisk`: Name, Medientyp, `HealthStatus`, `OperationalStatus`;
  - `MSFT_StorageReliabilityCounter`: `Wear`, `Temperature`, `TemperatureMax`, `ReadErrorsTotal`, `WriteErrorsTotal`, `PowerOnHours`. Der Zugriff braucht Admin; WinWächter läuft erhöht.
- Stufen **[Entscheidung Claude]**: `HealthStatus` ≠ Healthy oder `Wear` ≥ 90 % → rot, „Sichern Sie Ihre Daten bald“. `Wear` ≥ 70 % oder nicht korrigierte Lese-/Schreibfehler > 0 → gelb. Es gibt keinen Vergleich mit früheren Werten, weil nichts gespeichert wird. Temperatur nur anzeigen; über `TemperatureMax` gelb.
- Fehlt ein Wert (manche Laufwerke liefern keinen), steht „nicht verfügbar“, ohne Warnung.

**Laufwerke (Volumes)**
- **Optimierung**
  - Zuletzt optimiert: Datum aus der Windows-Statistik, die Quelle wählt der Umsetzer.
  - Älter als 30 Tage → Knopf „Laufwerk optimieren“ (`defrag <LW>: /O /U`). Auf SSDs ist das TRIM, auf Festplatten Defragmentieren. Keine Bestätigung, Fortschritt sichtbar.
- **Fehlerstatus**
  - Das „Dirty“-Bit kommt aus `fsutil dirty query <LW>:`.
  - Fehlermeldungen von `Ntfs`/`disk` im Ereignisprotokoll System der letzten 30 Tage kommen dazu. `System.Diagnostics.EventLog` ist schon im Projekt.
  - Fund → Knopf „Datenträger prüfen“ (Aktion aus 4.3.6).
- Funde der Seite (gelb/rot, Optimierung überfällig, Laufwerksfehler) gehen als Aktionen in „Rechner in Ordnung bringen“ → „Außerdem empfohlen“.

### 4.3 Reparatur (neue Seite „Reparatur“)
**Aufbau**
- Eine Liste von Werkzeugen. Je Zeile: wofür („Wenn …“), Dauer, ob ein Neustart nötig ist, wann zuletzt ausgeführt und Ergebnis, dazu ein Knopf.
- „Zuletzt ausgeführt“ kommt aus einer eigenen kleinen Datei `<DataRoot>\aktionen.json` (Aktions-ID, Zeitpunkt, Ergebnis-Kurztext). Das Protokoll taugt dafür nicht, weil `FileLog` nach 7 Tagen löscht.
- Alle Werkzeuge sind Aktionen über `ActionRunner`.
- Es läuft immer nur eine Reparatur zugleich (eigene Sperre wie `DefenderActivity`). Die anderen Knöpfe warten mit dem Hinweis „Eine Reparatur läuft gerade“.

**Werkzeuge**
1. **Systemdateien prüfen und reparieren**
   - Ablauf: `DISM /Online /Cleanup-Image /RestoreHealth`, danach `sfc /scannow`.
   - Fortschritt aus der Prozentangabe der Ausgabe. Dauer meist 10–30 Minuten. Kein Abbrechen mitten in DISM; „Abbrechen“ wirkt erst vor `sfc`.
   - Ergebnis in Klartext: keine Fehler / Fehler repariert / nicht reparierbar (mit Pfad zu `CBS.log`) / Neustart empfohlen.
   - Bestätigung nennt Dauer und Internetbedarf, denn DISM holt Ersatzdateien über Windows Update.
   - **Fund:** Vorher reicht ein schnelles `DISM /Online /Cleanup-Image /CheckHealth` (Sekunden). Meldet es „reparierbar“, steht die Zeile als Empfehlung oben und in „Rechner in Ordnung bringen“.
2. **Windows Update reparieren** (endgültig, Wiederherstellungspunkt)
   - Dienste `wuauserv`, `bits`, `cryptsvc` anhalten.
   - `%windir%\SoftwareDistribution` → `SoftwareDistribution.bak-JJJJMMTT` und `%windir%\System32\catroot2` → `catroot2.bak-JJJJMMTT` umbenennen.
   - Dienste in ihren vorherigen Zustand zurück (Starttyp wird nicht geändert).
   - Ältere `.bak`-Ordner von WinWächter (nur solche mit genau diesem Namensmuster) werden dabei entfernt, es bleibt einer.
   - Bestätigung: „Der Update-Verlauf in den Einstellungen erscheint danach leer; installierte Updates bleiben.“
   - Schlägt ein Umbenennen fehl, werden die Dienste trotzdem wieder gestartet und der Fehler gemeldet.
3. **Netzwerk**, drei Stufen als eigene Zeilen:
   - (a) „DNS-Cache leeren“ (`ipconfig /flushdns`, nur Klick);
   - (b) „Verbindung neu aufbauen“ (`ipconfig /release` + `/renew`, Bestätigung: „Die Verbindung ist kurz weg“);
   - (c) „Netzwerk komplett zurücksetzen“ (`netsh winsock reset`, `netsh int ip reset`; endgültig, Wiederherstellungspunkt, Neustart nötig). Bestätigung: „Von Hand eingetragene IP-Adressen und DNS-Server werden gelöscht. VPN- und Proxy-Einstellungen müssen danach eventuell neu eingerichtet werden. Gespeicherte WLAN-Passwörter bleiben.“ Hat ein Adapter eine feste IP oder feste DNS-Server, nennt die Bestätigung ihn und die Werte, damit der Nutzer sie notieren kann.
4. **Druckerwarteschlange leeren** (endgültig)
   - Dienst `Spooler` anhalten, Dateien in `%windir%\System32\spool\PRINTERS` löschen, `Spooler` wieder starten.
   - Bestätigung: „Wartende Druckaufträge werden gelöscht.“ Ohne Dateien in dem Ordner: „Die Warteschlange ist leer“, nichts weiter.
5. **Symbol- und Miniaturansichten neu aufbauen**
   - `%LocalAppData%\IconCache.db` sowie `…\Microsoft\Windows\Explorer\iconcache_*.db` und `thumbcache_*.db` des angemeldeten Benutzers löschen.
   - Explorer wird dafür beendet; Windows bringt ihn selbst zurück (wie „Explorer neu starten“ in 0.5, inklusive 15-s-Hinweis).
   - Es sind reine Zwischenspeicher, deshalb wird gelöscht und nicht in die Ablage verschoben **[Entscheidung Claude]**. Bestätigung: „Der Bildschirm flackert kurz, Fenster des Explorers schließen sich.“
6. **Datenträger prüfen**
   - Schritt 1: `chkdsk <LW>: /scan` (online, ohne Neustart, nur Klick). Ergebnis: keine Fehler / Fehler gefunden.
   - Schritt 2 (nur bei gefundenen Fehlern): Knopf „Beim nächsten Neustart reparieren“ (endgültig). Er plant die Reparatur beim Start ein, z. B. `chkdsk /spotfix` bzw. Dirty-Bit; den sicheren Weg wählt der Umsetzer.
   - Bestätigung: „Der nächste Start dauert länger; schalten Sie den Rechner dabei nicht aus.“

**Gemeinsam**
- Reparaturen schreiben ihre vollständige Ausgabe ins Protokoll.
- Die Oberfläche zeigt nur die Zusammenfassung. Den Knopf „Protokoll öffnen“ gibt es schon.

### 4.4 Automatische Wartung (nur melden)
**Einstellung**
- In „Einstellungen“ neuer Bereich „Automatische Prüfung“: Schalter, Wochentag, Uhrzeit, Standard Sonntag 11:00.
- Standard **aus**. Einmalig erscheint auf der Übersicht ein Hinweis „Wöchentliche Prüfung einschalten“ mit Knopf. Das ist eine dauerhafte Systemeinstellung (geplante Aufgabe), deshalb nur auf Klick **[Entscheidung Claude]**.
- Die Aufgabe läuft für den angemeldeten Benutzer. Ist er nicht das erhöhte Konto (`ownAccount` falsch, also ein Standardbenutzer mit fremdem Admin-Kennwort), ist der Schalter gesperrt mit Hinweis. „Höchste Rechte“ ohne gespeichertes Kennwort geht nur für Administratoren **[Entscheidung Claude]**.

**Aufgabe**
- Name `\WinWaechter\Wöchentliche Prüfung`, Anlage und Entfernung über `ITaskStore`.
- Auslöser: wöchentlich. Verpasste Termine werden nachgeholt (`StartWhenAvailable`), nur bei angemeldetem Benutzer, mit höchsten Rechten (kein UAC-Fenster, weil der Nutzer Administrator ist), nur mit Netzwerk für winget.
- Ausführung: höchstens 30 Minuten, niedrige Priorität.
- Startet `WinWaechter.App.exe --pruefen`.
- Der Deinstaller entfernt die Aufgabe (Inno `[UninstallRun]`).
- Schalter aus → Aufgabe wird gelöscht.

**Prüfmodus `--pruefen`**
- **Läuft unsichtbar:**
  - kein Fenster;
  - eine eigene Einzelinstanz-Sperre, damit eine offene App nicht stört und auch nicht übernommen wird;
  - kein `ChangeGate`;
  - keine Änderung, kein Wiederherstellungspunkt.
- **Prüft:**
  - alle Lesesammler von „Rechner in Ordnung bringen“ (`ImprovementCollector`);
  - winget-Updates (4.1);
  - Gerätezustand (4.2);
  - `DISM /CheckHealth` (4.3.1);
  - WinWächter-Update, wenn `CheckUpdatesOnStart` an ist.
- **Ergebnis:** `<DataRoot>\wartung\letzte-pruefung.json` mit Zeitpunkt, Zahlen je Bereich und Titeln der Funde.
  - Die Übersicht zeigt „Letzte automatische Prüfung: <Datum> – n Empfehlungen“ oder „alles in Ordnung“.
  - Die Datei liegt in der geschützten Datenablage.
- **Benachrichtigung:** nur wenn etwas zu tun ist, und nur, wenn sich das Ergebnis seit der letzten Meldung geändert hat oder die letzte Meldung mindestens 7 Tage her ist **[Entscheidung Claude]**.
  - Inhalt: „WinWächter: 6 Empfehlungen, 12 Programm-Updates“, Knopf „Ansehen“.
  - Klick öffnet WinWächter direkt bei „Rechner in Ordnung bringen“. Die App verlangt dann wie immer UAC; das hat der Nutzer selbst ausgelöst.
  - Schlägt die Prüfung selbst fehl, gibt es keine Fehlermeldung, nur einen Eintrag im Protokoll und „Letzte Prüfung fehlgeschlagen“ auf der Übersicht.

**Technik der Benachrichtigung**
- Windows-Benachrichtigungen (WinRT `ToastNotificationManager`) mit eigener AppUserModelID.
- Die Verknüpfung im Startmenü bekommt dieselbe ID (Inno `AppUserModelID:` in `[Icons]`).
- **Klick auf die Meldung:** Protokoll-Aktivierung **[Entscheidung Claude]**.
  - Der Installer registriert das Schema `winwaechter:` für den Benutzer und entfernt es beim Deinstallieren.
  - Die Meldung startet `winwaechter:verbessern`. Windows öffnet die EXE darüber per ShellExecute, das löst bei `requireAdministrator` das normale UAC-Fenster aus.
  - Ein COM-Aktivator scheidet aus: Er kann die erhöhte EXE nicht starten (Fehler 740, vgl. `winwaechter.iss:57`).
  - Läuft WinWächter schon, übergibt die neue Instanz das Sprungziel an die laufende und beendet sich. Der Weg dafür (z. B. Named Pipe) ist Sache des Umsetzers.
- Das Windows-Zielframework (`net10.0-windows10.0.19041.0` o. ä.) statt eines Zusatzpakets gilt zentral in `Directory.Build.props` für alle Projekte, weil das Testprojekt App referenziert. Die WinRT-Umsetzung von `INotifier` liegt in App, die Schnittstelle in Core.
- **Risiko:** Benachrichtigungen aus einem erhöhten Prozess erscheinen unter Umständen nicht. Der Umsetzer prüft das zuerst. Klappt es nicht, startet der Prüfmodus die Meldung über einen kleinen nicht erhöhten Helfer im Benutzerkontext (`UserTokenLauncher`, z. B. dieselbe EXE mit `--melden`, Manifest `asInvoker` über eine zweite kleine EXE).

### 4.5 Speicherplatz-Karte (neue Seite „Speicherplatz“)
**Aufbau** **[Entscheidung Claude]**
- Oben je festem Laufwerk ein Balken „belegt / frei“. Ein Laufwerk wird gewählt, Standard C:.
- „Speicherplatz untersuchen“ startet den Scan.
- Darunter die **Karte**: Kacheln (Treemap), Fläche = Größe, für die Ordner der aktuellen Ebene.
- Daneben bzw. darunter dieselben Ordner als sortierte Liste mit Balken und Größe; die Liste ist auch per Tastatur und Bildschirmleser bedienbar.
- Klick auf Kachel oder Zeile geht eine Ebene tiefer, oben steht eine Pfad-Leiste zum Zurückgehen.
- Ein zweiter Reiter „Größte Dateien“ zeigt die 100 größten Dateien ab 100 MB.

**Scan**
- Hintergrund mit Fortschritt („n Dateien, x GB“) und „Abbrechen“.
- Verknüpfungspunkte und Junctions werden nicht verfolgt (nichts doppelt).
- Cloud-Platzhalter (OneDrive „nur online“, Attribute `RecallOnDataAccess`/`Offline`) zählen mit 0 Byte, weil sie lokal keinen Platz belegen.
- Nicht lesbare Ordner werden als „kein Zugriff“ gezählt, nicht abgebrochen.
- Ein Scan des ganzen Laufwerks C: darf einige Minuten dauern. MFT-Direktlesen ist kein Ziel.
- Das Ergebnis bleibt bis zum Schließen der App im Speicher, nicht auf der Platte.

**Macher-Teil:** Je Ordner oder Datei bietet WinWächter, was zu ihm passt:
- **Bekannte Orte:**
  - Temp, Browser-Caches, Windows-Update-Downloads, Installer-Reste → „Aufräumen“ (springt zu Optimieren mit dieser Gruppe vorausgewählt).
  - Papierkorb → „Papierkorb leeren“ (endgültig, Bestätigung).
  - `hiberfil.sys` → Verweis auf die Leistung-Zeile Ruhezustand.
  - `pagefile.sys` und `WinSxS` → nur erklären („von Windows verwaltet, nicht anfassen“).
- **Ordner eines installierten Programms** (`InstallLocation` aus `ProgramInventory`) → „Programm deinstallieren“ (vorhandene Deinstallation). Geschützte Programme bekommen keinen Knopf.
- **Große Dateien und Ordner im Benutzerprofil** (Downloads, Desktop, Dokumente, Videos, Bilder, Musik), außer OneDrive-Ordnern und geschützten Pfaden:
  - „In die Ablage verschieben“ (vorhandenes `MoveToAblageChange`, im Verlauf rückgängig);
  - Hinweis „Der Platz wird frei, wenn die Ablage geleert wird (nach n Tagen)“, plus Knopf „Ablage jetzt leeren“, falls es den noch nicht gibt.
  - Unter Windows, Programme, ProgramData und fremden Benutzerprofilen gibt es keinen Verschiebe-Knopf.
- **Immer:** „Im Explorer zeigen“.

## 5. Technik
- Neue Bereiche in Core: `Updates/` (`IWingetClient`, Parser, `WingetUpdateService`), `Device/` (`IBatteryInfo`, `IDiskHealth`, `IVolumeInfo`), `Repair/` (je Werkzeug eine `SystemAction`-Fabrik, `RepairActivity`-Sperre), `Maintenance/` (Prüfmodus, Ergebnisdatei, Aufgaben-XML, `INotifier`), `Storage/Map/` (Scanner, Baum, Ortserkennung). Die Namen sind Vorschläge.
- App: neue Seiten `DeviceView`, `RepairView`, `StorageMapView` (+ ViewModels) in der Navigation.
  - Reihenfolge: Übersicht, Optimieren, Speicherplatz, Leistung, Gerät, Schutz, Sicherheit, Datenschutz, Programme, Reparatur, Verlauf **[Entscheidung Claude]**.
  - Programme-Seite um den Update-Kasten erweitert. Einstellungen um „Automatische Prüfung“ erweitert.
  - `App.OnStartup` wertet `--pruefen` (und ggf. `--melden`, Sprungziel aus der Benachrichtigung) aus.
- Neues Change-Kind `wingettelemetry` (oder als neuer Eintrag in vorhandener Datenschutz-Logik). `FromRecord` nimmt nur genau diesen Wert an.
- `ImprovementCollector`: neue Quellen für Aktionen (Programm-Updates, Gerät, Reparatur-Empfehlung, Laufwerk optimieren).
- `AppSettings`: `MaintenanceEnabled`, `MaintenanceDay`, `MaintenanceTime`, `LastNotified*`. Beim Laden prüfen und auf gültige Werte begrenzen wie `AblageDays`.
- Installer: `AppUserModelID` an der Verknüpfung, Schema `winwaechter:`, Aufgabe beim Deinstallieren entfernen.
- Erweiterungen vorhandener Schnittstellen:
  - `ITaskStore` bekommt `Delete`.
  - `IProcessRunner` bekommt eine Variante mit Zeilen-Rückmeldung während des Laufs (für Fortschritt von DISM, winget, defrag).
  - Neuer wartender Benutzerkontext-Starter (siehe 4.1).
- `rules.json` bekommt den Abschnitt `updateBlock`. Die Datei ändert sich, also muss `rules.json.sig` beim Release neu signiert werden.
- Keine neuen NuGet-Pakete. Erlaubt ist nur der Wechsel auf das Windows-Zielframework für WinRT.
- Tests:
  - winget-Parser mit erfundenen Beispielausgaben auf Deutsch und Englisch, mit Fortschrittszeichen, Fußzeilen, unbekannter Version und abgeschnittener ID. Die echte Liste des Nutzers kommt nicht ins öffentliche Repo.
  - Schutzfilter (Dell per ID und per Name, Lexware, WinWächter); Microsoft-Laufzeitumgebungen werden angeboten.
  - Befehlszeilen jeder Reparatur über `FakeProcessRunner`.
  - Stufen von Akku/SSD.
  - Aufgaben-XML (Auslöser, Rechte, Nachholen).
  - Prüfmodus ändert nichts (keine Aufrufe an Schreib-Fakes).
  - Benachrichtigungsregel (nur bei Änderung oder nach 7 Tagen).
  - Speicher-Scanner auf einem Testordner mit Junction und Platzhalter-Attribut.
  - Ablage-Knopf nur an erlaubten Orten.
  - VM-Tests für „endgültig“-Bestätigungen und Sperre ohne Admin.
  - `ViewSmokeTests` für die neuen Seiten (hell/dunkel, niedriges Fenster wie bei Programme in 0.5.1).

## 6. Was der Umsetzer selbst entscheidet
- Genaue Texte, Klassen- und Ordnerstruktur, Treemap-Verfahren (z. B. „squarified“), Farben im vorhandenen Design.
- Welche winget-Rückgabecodes eine eigene Klartext-Meldung bekommen (die häufigen: Programm läuft, Neustart nötig, kein passendes Installationsprogramm, Download fehlgeschlagen).
- Woher „zuletzt optimiert“ gelesen wird, und der sichere Weg für „Datenträger beim Neustart reparieren“.
- Reihenfolge der Umsetzung: Vorschlag 4.1 → 4.3 → 4.2 → 4.5 → 4.4, weil 4.4 alles andere einsammelt. Die Benachrichtigungs-Prüfung aus 4.4 wird früh als kleiner Versuch gemacht, weil sie die Architektur des Prüfmodus bestimmt.

## 7. Erledigt heißt
- Alle Tests grün, Release-Build ohne Warnungen; Einzelprüfung je Aufgabe und Gesamtprüfung wie bei 0.5.
- Auf dem Rechner des Nutzers, nur mit seiner Zustimmung:
  - die Update-Liste stimmt mit `winget upgrade` überein, ohne die beiden Dell-Pakete (und ohne etwaige Treiber-Pakete aus `updateBlock`); ein unkritisches Paket (z. B. 7-Zip) wird über WinWächter aktualisiert;
  - die Seite „Gerät“ zeigt Akku- und SSD-Werte plausibel;
  - DNS-Cache leeren und `chkdsk /scan` laufen durch (nur lesend bzw. harmlos); die übrigen Reparaturen nur, wenn der Nutzer es will;
  - die wöchentliche Prüfung wird eingeschaltet, einmal von Hand ausgelöst (Aufgabe „Jetzt ausführen“), die Benachrichtigung erscheint, und der Klick öffnet die richtige Stelle; danach ausschalten → Aufgabe ist weg;
  - die Speicherplatz-Karte von C: lädt in vertretbarer Zeit; eine Testdatei in Downloads wird in die Ablage verschoben und zurückgeholt.
- Version 0.6.0, CHANGELOG, Roadmap, Release erst nach Freigabe.
