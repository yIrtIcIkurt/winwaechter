# Changelog

## 0.3.1
- Sicherheit: Die Prüfung „Geräteverschlüsselung“ funktioniert jetzt (zeigte bisher immer „Nicht prüfbar“).
- Sicherheit: Schützt ein anderes Programm die Firewall, bleibt die Firewall-Zeile grün, auch wenn die Windows-Firewall gerade nicht lesbar ist.
- Kleinere Aufräumarbeiten im Hintergrund.

## 0.3.0
- Neue Seite „Programme“: installierte Programme mit Suche und Sortierung; offizielle Deinstallation (bei Bedarf über die Windows-Einstellungen), danach gezielte Suche nach Resten.
- Notfall „Erzwungen entfernen“ für Programme ohne funktionierendes Deinstallationsprogramm – alles gesichert und im Verlauf rückgängig zu machen.
- Neue Seite „Sicherheit“: Ampel für Firewall, Defender, SmartScreen, Benutzerkontensteuerung, Remotedesktop, Verschlüsselung, Windows-Updates, lauschende Programme und Browser-Erweiterungen; Übersicht zeigt die Anzahl.
- Regeln lassen sich signiert von GitHub aktualisieren; neue Regeln können den Schutz nur erweitern.
- Rückgängig zeigt den Fortschritt; Defender-Funde mit unbekanntem Status werden verständlich benannt; App-Icon in Fenster und Seitenleiste.
- Einstellungen: Abschnitt „Über WinWächter“ mit Autor und Projektadresse (zum Kopieren).

## 0.2.1
- Aufräumen: Bei Zwischenspeicher-Funden steht jetzt der tatsächlich verschobene Ordner als Ort (bei mehreren der erste plus „+n weitere“), nicht mehr der gemeinsame Oberordner.
- Deinstallationsreste: Ob ein Programmordner 30 Tage unberührt ist, richtet sich nach dem Alter der Dateien darin, nicht nach dem Zeitstempel des Ordners selbst.

## 0.2.0
- Aufräumen: Browser-Cache (Edge, Chrome, Opera, Firefox), Windows-Update-Downloads, Absturzabbilder und Fehlerberichte; Caches werden nach 1 Tag aus der Ablage entfernt.
- Alte Installer im Download-Ordner werden angezeigt (nie vorausgewählt).
- Deinstallationsreste: verwaiste Autostarts, Aufgaben und Dienste sowie verwaiste Programmordner (ganzer Ordner in die Ablage, rückgängig machbar).
- Neue Seite „Datenschutz“: einzelne Windows-Einstellungen ausschalten, jede im Verlauf rückgängig zu machen.
- Übersicht zeigt „Wird frei“.

## 0.1.1
- Defender-Funde zeigen jetzt, ob ein Fund noch aktiv oder schon erledigt ist.

## 0.1.0
- Erste Version: Aufräumen, Autostart- und Dienste-Optimierung, Sicherheitsprüfung, Ablage mit Rückgängig, signierte Updates.
