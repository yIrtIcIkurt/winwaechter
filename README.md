# WinWächter

WinWächter ist ein Prüfgerät für den eigenen Windows-Rechner (Windows 11). Es zeigt erst, was es gefunden hat und warum, und ändert erst nach Ihrer Bestätigung. Jede Änderung wird gesichert und lässt sich im Verlauf rückgängig machen. Keine Telemetrie: Die App spricht nur mit GitHub (Updates und Regeln).

## Funktionen

- **Optimieren:** Autostarts, Dienste, geplante Aufgaben, Temp-Dateien, Zwischenspeicher, alte Installer und Deinstallationsreste – jeder Vorschlag mit Begründung.
- **Schutz:** Microsoft Defender (Status, Scan, Funde) und eine Verhaltensprüfung laufender Programme.
- **Sicherheit:** Ampel für Firewall, Defender, SmartScreen, Benutzerkontensteuerung, Remotedesktop, Verschlüsselung, Windows-Updates, lauschende Programme und Browser-Erweiterungen.
- **Datenschutz:** einzelne Windows-Einstellungen gezielt ausschalten.
- **Programme:** installierte Programme mit Suche und Sortierung, offizielle Deinstallation und anschließende Suche nach Resten.
- **Verlauf:** alle Sitzungen, Rückgängig in umgekehrter Reihenfolge, Ablage statt Papierkorb.

## Installation

Das Setup `WinWaechter-Setup-x.y.z.exe` gibt es unter [GitHub Releases](https://github.com/yIrtIcIkurt/winwaechter/releases). Spätere Updates findet die App selbst; sie werden nur auf Klick geladen und vor der Installation anhand einer Signatur geprüft.

## Bauen

Voraussetzung ist das .NET 10 SDK.

```powershell
dotnet test
.\build-release.ps1 -Version x.y.z
```

`build-release.ps1` führt die Tests aus, veröffentlicht die App, baut das Setup (Inno Setup 6) und signiert die Prüfsummen. Dafür ist der private Signaturschlüssel nötig; ohne ihn lässt sich mit `dotnet test` und `dotnet build` trotzdem alles prüfen und bauen.

## Autor

Tahsin Yilmaz – [github.com/yIrtIcIkurt/winwaechter](https://github.com/yIrtIcIkurt/winwaechter)
