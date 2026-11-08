# WinWächter – Roadmap

Stand: 2026-11-08 (Version 0.1.0)

## Offener Abschlusstest (mit Zustimmung des Nutzers)
- [x] A: Installieren, Startmenü, „Apps“-Name, Zugriffsrechte `C:\ProgramData\WinWaechter` (geprüft 2026-11-08)
- [ ] A2: Start direkt nach der Installation (Fehler 740 behoben in 2df3905, Installer neu gebaut, noch nicht erneut getestet)
- [ ] B: Optimieren + Rückgängig an Test-Einträgen (`tests/e2e/create-fixtures.ps1` / `remove-fixtures.ps1`)
- [ ] C: Defender-Schnellscan, Abbrechen; optional FileZilla-Bundle-Installer bereinigen
- [ ] D: GitHub-Repo `yIrtIcIkurt/winwaechter` (öffentlich), Release v0.1.0 + v0.1.1, Update-Test
- [ ] Dunkles Design, 150 % Skalierung, Tastaturbedienung im echten Fenster
- [ ] Deinstallation (Daten bleiben standardmäßig)

## Nach 0.1.0 (Entscheidung Nutzer 2026-11-08)
- Signierte `rules.json` direkt aus dem eigenen GitHub-Repo aktualisieren (ohne neues Release, ohne Drittanbieter-Listen). Defender bleibt die Scan-Engine.

## Bekannte kleine Punkte für 0.1.1
- History-Ansicht bricht ab, wenn ein Ablage-Ordner nicht lesbar ist; Link-Warnung bei jeder Vorschau im Log.
- Installer wertet Rückgabecodes von icacls nicht aus (App sichert den Ordner beim Start selbst).
- Zwischen Pfadprüfung und Verschieben bleibt ein sehr kleines Zeitfenster (Lösung: handle-basiertes Verschieben).
- Von Angreifern vorab angelegte Dateien wie `settings.json` bleiben in deren Besitz (Auswirkung: Ablage frühestens nach 1 Tag geleert).
- Rechteprüfung des Datenbaums läuft beim Start im UI-Thread (bei großer Ablage langsamer Start).
- Unsignierte Programme mit geschütztem Herstellernamen (z. B. „Dell…“) in AppData: nur „Von Defender prüfen lassen“ möglich.
- Fehlgeschlagener Datei-Datensatz zeigt einen Rückgängig-Knopf, der dann „nicht mehr vorhanden“ meldet.
- Pfade über OneDrive-Platzhalter, eingebundene Ordner oder Netzlaufwerke werden beim Verschieben übersprungen.
- App-Icon: Bild-Prompt in `assets/README.md`, danach `tools/make-ico.ps1`.
