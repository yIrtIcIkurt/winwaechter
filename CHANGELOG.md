# Changelog

## 0.5.1
- Programme: Die Liste der installierten Programme war in einem niedrigen Fenster nicht erreichbar, weil „Vorinstallierte Apps“ und die Suche den ganzen Platz belegten. Jetzt scrollt die ganze Seite in einem Stück; die Suche filtert kurz nach dem Tippen.
- Verlauf, „Erneut versuchen“: Vor dem neuen Versuch läuft dieselbe Schutzprüfung wie beim ersten Angebot. Eine Firewall-Regel wird nie für Microsoft-Programme, WinWächter selbst oder geschützte Hersteller angelegt. Ein Rechtsklick-Eintrag wird nur abgeschaltet, solange er noch angeboten wird. Ein Registry-Wert wird nicht überschrieben, wenn inzwischen eine fremde Richtlinie ihn festlegt.
- Datenschutz: Einstellungen, die eine Richtlinie festlegt, die nicht von WinWächter stammt, werden nicht mehr überschrieben. Die Zeile zeigt dann „Von einer Richtlinie festgelegt“; dieselbe Regel gilt seit 0.5.0 schon für Leistung und Sicherheit.
- Defender: Es läuft immer nur ein Defender-Vorgang zugleich, auch über die Seiten „Schutz“, „Sicherheit“ und „Rechner in Ordnung bringen“ hinweg. Läuft gerade ein Scan oder eine Aktualisierung, warten die anderen Defender-Knöpfe.

## 0.5.0
- Übersicht: Neuer Hauptknopf „Rechner in Ordnung bringen“. Er prüft alle Bereiche und zeigt eine einzige Liste „Empfohlene Verbesserungen“, nach Bereich gruppiert, jede Zeile mit Begründung und Häkchen. Ein Klick auf „Jetzt verbessern“ wendet die gewählten Änderungen als eine Sitzung an; im Verlauf lässt sie sich als Ganzes oder einzeln zurücknehmen. Scheitert eine Änderung, geht es mit den übrigen weiter, am Ende steht „n verbessert, m übersprungen“ mit Gründen. Die Kacheln der Übersicht führen jetzt in ihren Bereich, „Offene Vorschläge“ zählt alle Bereiche, und bei fast vollem Systemlaufwerk erscheint ein Hinweisband mit „Platz schaffen“.
- Sicherheit behebt jetzt selbst, statt nur die Windows-Einstellungen zu öffnen:
  - SmartScreen, Benutzerkontensteuerung (wirkt erst nach einem Neustart) und Remotedesktop per „Beheben“, im Verlauf rückgängig zu machen. Legt eine Richtlinie den Wert fest, bleibt es bei der Anzeige.
  - Lauschende Programme: „Eingehende Verbindungen sperren“ legt eine Firewall-Regel `WinWächter: <Programm>` an; Zurücknehmen löscht genau diese Regel. Geschützte Hersteller, der Windows-Ordner und Microsoft-signierte Dateien bleiben ausgenommen.
  - Aktionen, die sich nicht zurücknehmen lassen („endgültig“): Echtzeitschutz einschalten (blockiert der Manipulationsschutz das, steht es verständlich da, mit Sprung zu Windows-Sicherheit), Windows-Updates installieren (nur die, die Windows selbst automatisch installieren würde; einzelne Updates können Sie in Windows unter Updateverlauf deinstallieren) und Neustart. Dazu kommt „Virendefinitionen aktualisieren“; das ist harmlos und braucht nur den Klick. Alle stehen getrennt unter „Außerdem empfohlen“, nie im Sammelknopf.
  - Manipulationsschutz und Geräteverschlüsselung bleiben Verweise; der Text erklärt, warum.
- Computerschutz: neue Zeile auf der Seite „Sicherheit“. WinWächter schaltet den Computerschutz für das Systemlaufwerk ein und stellt einen deaktivierten Dienst auf den Windows-Standard, damit Wiederherstellungspunkte möglich werden. Nur teilweise rückgängig: Zurücknehmen setzt den Dienst zurück, der Computerschutz bleibt eingeschaltet, weil Windows beim Ausschalten alle Wiederherstellungspunkte löschen würde. Schaltet eine Richtlinie den Computerschutz ab, gibt es keine Behebung.
- Leistungsbremsen beseitigen:
  - Start: Was den Start gebremst hat, lässt sich per „Nicht mehr mit Windows starten“ abstellen; bei Teilen von Windows, Treibern und geschützten Herstellern steht stattdessen, warum nicht.
  - Größte Verbraucher: „Beenden“ (endgültig; erst sanft, nach 5 Sekunden mit Rückfrage „Erzwingen?“; nicht gespeicherte Arbeit geht verloren; Windows, Dienste, geschützte Hersteller und WinWächter selbst sind ausgenommen) und, wo es einen Autostart gibt, „Nicht mehr mit Windows starten“.
  - Neu unter „Einstellungen“: Schnellstart (nicht empfohlen, mit Vor- und Nachteilen erklärt) und Energiesparplan; ist nicht „Ausgewogen“ aktiv, bietet die Seite „Auf ‚Ausgewogen‘ umstellen“ an.
- Browser-Hintergrund: Neue Gruppe „Im Hintergrund“ unter „Leistung“. Edge-Start-Boost, Edge im Hintergrund und Chrome im Hintergrund lassen sich über die offiziellen Browser-Richtlinien ausschalten. Danach zeigt der Browser „Von Ihrer Organisation verwaltet“; das ist normal und verschwindet beim Zurücknehmen. Eine fremde Richtlinie (auf manchen Rechnern setzt ein früheres Tuning-Programm sie) zeigt WinWächter als „Von einer Richtlinie festgelegt“ an und ändert sie nicht. Für Opera gibt es keinen gleichwertigen Schalter.
- Werbe-Apps (Seite „Programme“, Abschnitt „Vorinstallierte Apps“): Clipchamp, Copilot, neues Outlook, Teams (privat), Microsoft 365, Dev Home, Quick Assist und Kurznotizen sowie weitere bekannte Werbe-Apps aus einer festen, lokalen Liste lassen sich für Ihr Benutzerkonto entfernen; auf der Seite ist nichts vorausgewählt. Copilot, Outlook, Teams, Microsoft 365, Quick Assist und Kurznotizen sind nicht empfohlen, weil viele sie nutzen, und kommen deshalb nicht in „Rechner in Ordnung bringen“; dort stehen Apps, die sich nur über den Store wiederherstellen lassen, zwar in der Liste, sind aber nicht vorausgewählt. Xbox, Store, Fotos, Rechner, Edge und Systempakete werden nie angeboten. Zurücknehmen klappt voll, wenn Windows das Paket noch als Abbild hat; sonst nur über den Store („Im Store neu installieren“, die Bestätigung nennt das vorab). Entfernt wird nur für das angemeldete Konto.
- Windows-Werbung: Neue Gruppe „Werbung & Vorschläge“ auf der Seite „Datenschutz“ mit weiteren Schaltern (Startmenü-Konto-Hinweise, Vorschläge in Einstellungen, Sperrbildschirm-Tipps, Werbung im Explorer, Begrüßungserlebnis nach Updates); bisherige Einträge sind hier einsortiert. Alle einzeln im Verlauf rückgängig zu machen.
- Rechtsklick-Menü: Neue Gruppe auf „Optimieren“ listet Erweiterungen und Befehle von Programmen im Rechtsklick-Menü (ohne Windows, Microsoft und geschützte Hersteller). Abschalten über den offiziellen Sperrmechanismus von Windows, genau dieser Eintrag ist rückgängig zu machen; nichts ist vorausgewählt und nichts steckt im Sammelknopf. „Explorer neu starten“ (harmlos, nur ein Klick) lässt die Änderung sofort wirken; Windows holt die Oberfläche selbst zurück. Einträge neuer Windows-11-Apps lassen sich so nicht abschalten und fehlen deshalb.
- Kleinere Lücken:
  - Browser-Cache gesperrt: „Browser schließen und leeren“ (sanft schließen, bis 10 Sekunden warten, dann „Erzwingen?“; offene Eingaben gehen verloren).
  - Programme: „Alle unerwünschten entfernen“ deinstalliert nacheinander mit Bestätigungsliste (endgültig) und sucht danach Reste.
  - Verlauf: Fehlgeschlagene Änderungen bekommen „Erneut versuchen“. Das gibt es nicht für Autostarts, Dienste und Aufgaben.
  - Schutz: Echtzeitschutz „Aus“ hat dieselbe Behebung wie auf der Seite „Sicherheit“; die Kachel „Letzter Scan“ bekommt „Jetzt scannen“.
- Die Regeldatei (Version 3) enthält die Liste der Werbe-Apps und die neuen Werbe-Schalter.

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
