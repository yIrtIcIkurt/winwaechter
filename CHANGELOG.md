# Changelog

## 0.4.0
- Neue Seite „Leistung“:
  - „Jetzt“: Prozessor, Arbeitsspeicher und Datenträger live sowie die größten Verbraucher. Gemessen wird nur, solange die Seite offen ist.
  - „Start“: Dauer der letzten vollständigen Starts und was den Start gebremst hat, mit Sprung zu den Autostart-Programmen.
  - „Einstellungen“: Energiemodus für Netzbetrieb und Akku, Transparenz- und Animationseffekte, Spielmodus, GPU-Planung, Ruhezustand-Datei, Speicheroptimierung und Hintergrund-Apps. Jede Einstellung einzeln, nichts vorausgewählt, alles im Verlauf rückgängig zu machen.
- Sicherheit, Browser-Erweiterungen: eigene Einschätzung statt „Prüfen“. Je Erweiterung Version, Installationsdatum, Herkunft und Rechte in Klartext; Gelb mit Empfehlung, wenn sie nicht aus dem Store stammt, und Knopf „In {Browser} entfernen“: Er kopiert die Adresse der Erweiterung und öffnet den Browser mit Ihren normalen Benutzerrechten; dort Strg+L, Strg+V, Enter und „Entfernen“ (Browser lassen andere Programme diese Seite nicht direkt öffnen). Den Knopf gibt es auch bei grünen und ausgeschalteten Erweiterungen, nur nicht bei solchen, die eine Richtlinie vorgibt. Sync-Platzhalter ohne installierte Erweiterung erscheinen nicht mehr; ausgeschaltete Erweiterungen stehen als „Deaktiviert“ da.
- Bewusst ohne „Tuning-Tricks“ wie RAM-Booster oder das massenhafte Abschalten von Diensten.

## 0.3.2
- Programme: Für Ihr Benutzerkonto installierte Programme (z. B. in AppData) deinstalliert WinWächter jetzt direkt – mit Ihren normalen Benutzerrechten, wie Windows selbst. Der Umweg über die Windows-Einstellungen entfällt; er bleibt nur, wenn ein solches Deinstallationsprogramm Adminrechte verlangt.
- Listen scrollen mit dem Touchpad gleichmäßig (auch die Programmliste); Pfeiltasten, Bild↑/↓, Pos1/Ende und Leertaste scrollen die Seite.

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
