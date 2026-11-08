# WinWächter – Roadmap

Stand: 2026-11-09 (Version 0.1.1)

## Abschlusstest auf dem echten Rechner
- [x] A: Installieren, Startmenü, „Apps“-Name, Zugriffsrechte `C:\ProgramData\WinWaechter` (2026-11-08)
- [x] A2: Start direkt nach der Installation (Fehler 740 behoben, 2df3905)
- [x] B: Optimieren + Rückgängig an Test-Einträgen – Autostart, Aufgabe, Temp-Dateien exakt wiederhergestellt; zusätzlich echte Sitzung mit Diensten/Aufgabe rückgängig gemacht (2026-11-09)
- [x] C: Defender-Schnellscan (44 s) und Abbrechen (2026-11-09)
- [x] D: GitHub-Repo `yIrtIcIkurt/winwaechter`, Releases v0.1.0 + v0.1.1, Selbst-Update 0.1.0 → 0.1.1 (2026-11-09)
- [ ] Dunkles Design, 150 % Skalierung, Tastaturbedienung im echten Fenster
- [ ] Deinstallation (Daten bleiben standardmäßig)

Beim Test gefunden und behoben: Start nach Setup (740), zu schnelles Scrollen mit Touchpad, „Prozess beenden“ blieb klickbar, Verlauf wählte nach dem Anwenden die alte Sitzung.

## Nach 0.1.x (Entscheidung Nutzer 2026-11-08)
- Signierte `rules.json` direkt aus dem eigenen GitHub-Repo aktualisieren (ohne neues Release, ohne Drittanbieter-Listen). Defender bleibt die Scan-Engine.
- Ideen für 0.2.0 (Nutzer noch nicht entschieden): erweiterter Cleaner (Browser-Caches, Update-Cache, alte Installer in Downloads), Deinstallationsreste (verwaiste Ordner/Einträge, kein Registry-Cleaner), Datenschutz-Check, Sicherheits-Check, App-Übersicht, große Dateien/Duplikate, winget-Updates, wöchentliche Prüfung.

## Bekannte kleine Punkte
- Tag `v0.1.1` auf GitHub zeigt auf aa249ae statt 7114d33 (nur Quellcode-ZIP betroffen; Korrektur braucht Force-Push durch den Nutzer).
- Defender-Status 106 ist nicht dokumentiert und wird als „Status 106“ angezeigt.
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
