# WinWächter

WinWächter ist ein Prüfgerät für den eigenen Windows-Rechner (Windows 11). Es zeigt erst, was es gefunden hat und warum, und ändert erst nach Ihrer Bestätigung. Jede Änderung wird gesichert und lässt sich im Verlauf rückgängig machen. Keine Telemetrie (siehe [Datenschutz](#datenschutz)).

## Funktionen

- **Optimieren:** Autostarts, Dienste, geplante Aufgaben, Temp-Dateien, Zwischenspeicher, alte Installer und Deinstallationsreste – jeder Vorschlag mit Begründung.
- **Speicherplatz:** Karte der größten Ordner eines Laufwerks; selten genutzte Dateien lassen sich in die Ablage verschieben und zurückholen.
- **Leistung:** Energieeinstellungen, Hintergrund-Apps und Programme, die den Rechner gerade bremsen.
- **Gerät:** Zustand von Akku und Datenträgern.
- **Schutz:** Microsoft Defender (Status, Scan, Funde) und eine Verhaltensprüfung laufender Programme.
- **Sicherheit:** Ampel für Firewall, Defender, SmartScreen, Benutzerkontensteuerung, Remotedesktop, Verschlüsselung, Computerschutz, Windows-Updates, lauschende Programme und Browser-Erweiterungen.
- **Datenschutz:** einzelne Windows-Einstellungen gezielt ausschalten.
- **Programme:** installierte Programme mit Suche und Sortierung, Programm-Updates über winget, offizielle Deinstallation und anschließende Suche nach Resten.
- **Reparatur:** die üblichen Windows-Reparaturen mit einem Klick (Systemdateien, Windows Update, DNS, Netzwerk, Druckerwarteschlange, Symbole, Datenträger).
- **Wöchentliche Prüfung (optional):** meldet sich, wenn es etwas zu tun gibt; sie ändert selbst nichts.
- **Verlauf:** alle Sitzungen, Rückgängig in umgekehrter Reihenfolge, Ablage statt Papierkorb.

Änderungen am System macht WinWächter nur nach einer Bestätigung, die sagt, was passiert; was sich nicht rückgängig machen lässt, ist als endgültig gekennzeichnet. Das Setup bringt eine Deinstallation mit (Windows-Einstellungen → Apps).

## Installation

Das Setup `WinWaechter-Setup-x.y.z.exe` gibt es unter [GitHub Releases](https://github.com/yIrtIcIkurt/winwaechter/releases). Spätere Updates findet die App selbst; sie werden nur auf Klick geladen und vor der Installation anhand einer Signatur geprüft.

## Datenschutz

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

Im Einzelnen: WinWächter sammelt und sendet keine Nutzungs- oder Gerätedaten. Verbindungen gibt es nur zu GitHub – für die Suche nach neuen Versionen (beim Start, in den Einstellungen abschaltbar), für Update-Downloads auf Klick und für die signierte Regelliste. Programm-Updates über winget laden die Pakete nur auf Ihren Klick aus den winget-Quellen.

## Code signing policy

Die Programmdateien und das Setup sind derzeit noch nicht mit einem Code-Signing-Zertifikat signiert; eine kostenlose Signatur über die [SignPath Foundation](https://signpath.org) ist geplant. Bis dahin kann die intelligente App-Steuerung von Windows 11 WinWächter blockieren. Die Update-Dateien prüft die App unabhängig davon selbst anhand einer eigenen Signatur.

Rollen (alle: Tahsin Yilmaz, [@yIrtIcIkurt](https://github.com/yIrtIcIkurt)):

- Committer und Prüfer (Reviewer): [@yIrtIcIkurt](https://github.com/yIrtIcIkurt)
- Freigabe jeder Signatur (Approver): [@yIrtIcIkurt](https://github.com/yIrtIcIkurt)

Beiträge von außen werden vor dem Zusammenführen geprüft.

## Bauen

Voraussetzung ist das .NET 10 SDK.

```powershell
dotnet test
.\build-release.ps1 -Version x.y.z
```

`build-release.ps1` führt die Tests aus, veröffentlicht die App, baut das Setup (Inno Setup 6) und signiert die Prüfsummen. Dafür ist der private Signaturschlüssel nötig; ohne ihn lässt sich mit `dotnet test` und `dotnet build` trotzdem alles prüfen und bauen.

Jeder Push baut WinWächter außerdem auf GitHub (`.github/workflows/build.yml`: Tests, Publish, Setup als Artefakt). Bei einem Versions-Tag `vx.y.z` lässt der Ablauf die Programmdateien und das Setup über SignPath signieren, sobald das eingerichtet ist (Konfigurationen in `.signpath/`). Die Update-Prüfsummen signiert danach `release-ci.ps1` auf dem Rechner des Autors; der private Schlüssel liegt nie auf GitHub.

## Lizenz

[MIT](LICENSE) – © 2026 Tahsin Yilmaz

## Autor

Tahsin Yilmaz – [github.com/yIrtIcIkurt/winwaechter](https://github.com/yIrtIcIkurt/winwaechter)
