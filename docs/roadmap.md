# WinWächter – Roadmap

Stand: 2026-11-10 (Version 0.3.1)

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

## 0.4.0 (gebaut 2026-11-11, Branch feature/v0.4)
Design `docs/superpowers/specs/2026-11-10-winwaechter-0.4-design.md`, Plan `docs/superpowers/plans/2026-11-10-winwaechter-0.4.md`. 764 Tests, Einzel- und Gesamtprüfung.
- [ ] Auf dem Rechner: Seite öffnen (Monitor, Start-Bericht), echtes Fenster/Minimieren
- [ ] §4.3 Energiemodus: Windows-Einstellungen zeigen den gesetzten Modus, auch nach Neustart
- [ ] §4.9 Hintergrund-Apps: Windows schreibt `Disabled`/`DisabledByUser` (eine App in den Einstellungen auf „Nie“ stellen, vergleichen), Namen lesbar
- [ ] Je eine Änderung aus Energie, Effekte, Spielmodus, Speicher anwenden und im Verlauf zurücknehmen; Ruhezustand nur nach Zustimmung
Bekannte Kleinigkeiten (aus den Prüfungen): Liste wird nach Undo im Verlauf erst beim nächsten Öffnen aktualisiert; Wechsel des Energiesparplans bei offener Seite wird erst nach „Neu lesen“ erkannt; ein einzelnes kaputtes Ereignis macht den Start-Bericht unlesbar.

## 0.4.0 Plan (Entscheidung Nutzer 2026-11-10)
Neue Seite „Leistung“: Energiemodus (Netz/Akku), visuelle Effekte, Startzeit-Bericht, Live-Monitor (CPU/RAM/Datenträger/Top-Verbraucher), Hintergrund-Apps, Spielmodus + GPU-Planung, Ruhezustand-Datei (mit Hinweis), Speicheroptimierung. Alles einzeln, umkehrbar; keine „Tuning-Tricks“ (RAM-Booster, Prefetch löschen, Dienste massenhaft abschalten).

## 0.5.0 (geplant, Entscheidung Nutzer 2026-11-10)
Mehrsprachigkeit Deutsch, Englisch, Türkisch (App, Regeltexte, Installer). Startsprache wie Windows, sonst Englisch; umschaltbar in den Einstellungen. Türkische Texte vom Nutzer gegenlesen lassen.

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
