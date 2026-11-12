# WinWächter 0.5.0 – „Macher statt Beobachter“ (Design)

Stand: 2026-11-12. Grundlage: Gespräch mit dem Nutzer am 2026-11-12. Was mit **[Entscheidung Claude]** markiert ist, habe ich festgelegt; der Nutzer hat den Gesamtvorschlag angenommen („wir machen es so wie du sagst“), die Einzelheiten aber nicht einzeln bestätigt.

## 1. Zweck und Warum
Der Nutzer will „keinen Beobachter, sondern einen Macher“: WinWächter soll nicht nur prüfen und sagen, was nicht stimmt, sondern es **beheben**, den Rechner verbessern, Leistungsbremsen finden und beseitigen – „einfach einen aufgeräumten Rechner“. Heute zeigen Übersicht, Sicherheit, Leistung („Jetzt“, „Start“) und Schutz vieles nur an oder schicken in die Windows-Einstellungen. Dieselbe Kritik kam schon bei der Deinstallation (0.3.2) und den Browser-Erweiterungen (0.4.0).

Leitfrage für jede Zeile in der App: **Kann WinWächter das selbst beheben – mit Zustimmung, und wenn möglich im Verlauf rückgängig?** Nur wenn das technisch unmöglich oder unsicher ist, wird auf Windows verwiesen, und dann so direkt wie möglich.

Zielrechner (bekannt): Laptop, Windows 11 Home Build 26200, Edge/Chrome/Opera, Malwarebytes Browser Guard war installiert, Spiele über Steam, EA, Ubisoft und die Xbox-App, Lexware (Buchhaltung) und Dell-Software, Computerschutz/Wiederherstellungspunkte scheitern mit 0x80070422 (Dienst deaktiviert), Edge zeigt „Von Ihrer Organisation verwaltet“ für einige Einstellungen.

## 2. Ziele / Nicht-Ziele / Anti-Ziele
Ziele (0.5.0)
1. „Rechner in Ordnung bringen“ auf der Übersicht (4.1).
2. Sicherheit behebt selbst, wo es sicher geht (4.2).
3. Leistungsbremsen finden und beseitigen (4.3).
4. Fünf neue Werkzeuge, die vergleichbare Programme haben: Werbe-Apps entfernen (4.4), Windows-Werbung abschalten (4.5), Computerschutz einschalten (4.6), versteckte Hintergrund-Bremsen der Browser (4.7), Rechtsklick-Menü entrümpeln (4.8).
5. Kleinere Lücken schließen (4.9).

Nicht-Ziele (0.5.0) – kommen später laut Nutzer-Reihenfolge
- 0.6.0: Programm-Updates (winget), Gerätezustand (Akku, SSD), Reparatur-Seite (SFC/DISM, Update-Reparatur, Netzwerk-Reset, Druckerwarteschlange, Symbol-Cache, Datenträgerprüfung), automatische Wartung, Speicherplatz-Karte.
- Danach: Sprachen DE/EN/TR.
- Keine Geräteverschlüsselung einschalten (Wiederherstellungsschlüssel muss der Nutzer selbst sichern) – bleibt Verweis.
- Keine Apps für neue Benutzerkonten entfernen (kein Deprovisionieren des Windows-Abbilds).
- Keine Dauerüberwachung im Hintergrund, keine Benachrichtigungen (kommt mit der automatischen Wartung in 0.6.0).

Anti-Ziele (wäre ein Fehlschlag, auch wenn es „funktioniert“)
- Ändern ohne Zustimmung. Jede Änderung braucht einen Klick des Nutzers mit Bestätigung, die sagt, was passiert.
- „Tuning-Tricks“: Registry-Reiniger, RAM-Booster, „Internet-Booster“, Treiber aus fremden Datenbanken, Dienste massenhaft abschalten, Prozessprioritäten.
- Dell, Lexware (und alles auf der Schutzliste `rules.json` → `protected`) anfassen – auch nicht im neuen Rechtsklick-, Hintergrund- oder Werbe-App-Werkzeug.
- Eine endgültige Aktion, die wie eine rückgängig machbare aussieht. Was nicht über den Verlauf zurückgeht, ist in der Bestätigung ausdrücklich als „endgültig“ oder „nur teilweise rückgängig“ gekennzeichnet.
- Ein Knopf, der den Nutzer wieder nur in die Windows-Einstellungen schickt, wo WinWächter es selbst könnte.
- Spiele kaputtmachen (Xbox-/Game-Pass-Anmeldung, Game Bar), Lexware oder Dell-Werkzeuge stören.

## 3. Grundsätze für alle neuen Funktionen
- **Zwei Arten von Handlungen** **[Entscheidung Claude]**:
  - *Änderung* = `IChange` im Journal (Plan/Apply/Undo/CheckDrift/FromRecord), angewendet über `OptimizerEngine.ApplyAsync` (Wiederherstellungspunkt + Sitzung), `ChangeGate`, danach `ChangesApplied`. Rückgängig im Verlauf.
  - *Aktion* = einmalige Handlung ohne Undo (Updates installieren, Neustart, Programm beenden, Virendefinitionen aktualisieren, Explorer neu starten, Deinstallation). Immer mit Bestätigung, die „endgültig“ sagt (Ausnahme: harmlose Aktionen wie Definitionen aktualisieren oder Explorer neu starten brauchen nur den Klick), immer im Protokoll (`FileLog`).
- Ziele kommen aus **festen Listen** (Code-Whitelist oder lokale `rules.json`-Abschnitte, die nicht aus dem Netz kommen dürfen – wie heute `privacy`). `FromRecord` lehnt alles ab, was nicht auf der jeweiligen Liste steht; Zielwerte kommen aus der Liste, nie aus dem Journal.
- **Richtlinien respektieren** **[Entscheidung Claude]**: Gilt nur für Werte unter `HKLM\SOFTWARE\Policies\…` und `HKCU\SOFTWARE\Policies\…` (der UAC-Ort `…\CurrentVersion\Policies\System` ist der normale Windows-Speicherort und zählt nicht als Richtlinie). Geprüft wird je Wert: Ein Richtlinienwert gilt als „von WinWächter“, wenn im Journal ein angewendeter Datensatz für genau diesen Wert steht; sonst ist er fremd. Steuert ein fremder Richtlinienwert die Einstellung, bietet WinWächter keine Behebung an, sondern sagt „Von einer Richtlinie festgelegt“. Fremde Richtlinien aufräumen ist kein Ziel von 0.5.0.
- **Schutzliste gilt überall** (Herausgeber, Namen, Pfade aus `rules.json` → `protected`, plus WinWächter selbst).
- Ohne Adminrechte: alles sichtbar, Behebungen gesperrt mit dem vorhandenen Admin-Hinweis (wie heute).
- Texte: Sie-Form, sachlich, kurz, je Zeile ein Satz „warum“ (Vorlage: Datenschutz-Seite). Keine Prozent-Versprechen.
- Alle Systemzugriffe hinter Schnittstellen mit Fakes, damit alles ohne Admin testbar ist (wie in 0.4).

## 4. Inhalte im Einzelnen

### 4.1 „Rechner in Ordnung bringen“ (Übersicht)
- Großer Hauptknopf auf der Übersicht „Rechner in Ordnung bringen“ (ersetzt das heutige „Prüfen“ als Hauptaktion; „Prüfen“ entfällt, weil der neue Knopf selbst prüft).
- Ablauf: Alle Prüfungen laufen (Optimieren inkl. Speicher, Datenschutz, Leistung, Sicherheit, Schutz/Heuristik, plus 4.4–4.8). Danach erscheint **eine** Liste „Empfohlene Verbesserungen“, gruppiert nach Bereich (Aufräumen, Autostart & Hintergrund, Datenschutz & Werbung, Sicherheit, Leistung). Jede Zeile: was, ein Satz warum, Häkchen.
- Was hinein kommt (vorausgewählt): alles, was die Bereiche heute schon als vorausgewählt/empfohlen kennzeichnen (Optimieren: `Preselected`; Datenschutz: `recommended`; Leistung: `Recommended`; Heuristik: `Verdaechtig` und nicht geschützt), dazu die neuen Empfehlungen aus 4.2–4.8, jeweils nur, wenn sie eine *Änderung* (`IChange`) haben. Die Liste zeigt **nur Empfehlungen**; Funde, die ein Bereich bloß als „Info“ führt, bleiben auf der Bereichsseite, sonst wird die Liste lang und unklar **[Entscheidung Claude]**.
- Ein Klick „Jetzt verbessern (n)“ → eine Bestätigung mit Zusammenfassung → eine Sitzung „Rechner in Ordnung bringen“ im Verlauf (als Ganzes oder einzeln rückgängig).
- Scheitert eine Änderung, macht 4.1 mit den übrigen weiter (anders als die Bereichsseiten, die heute beim ersten Fehler anhalten). Umsetzung: Option „weitermachen bei Fehler“ in `OptimizerEngine.ApplyAsync`, nur von 4.1 genutzt; die anderen Seiten bleiben unverändert **[Entscheidung Claude]**.
- Ist der Computerschutz aus (4.6) und ausgewählt, läuft diese Behebung als eigener erster Schritt *vor* der Sitzung, damit der Wiederherstellungspunkt der Sitzung schon klappt **[Entscheidung Claude]**.
- *Aktionen* (Updates installieren, Neustart nötig, Virendefinitionen veraltet) stehen getrennt darunter als „Außerdem empfohlen“ mit eigenem Knopf, nie im Sammelknopf.
- Ergebnis: „n verbessert, m übersprungen (warum)“. Danach neu prüfen; die Übersicht zeigt den neuen Stand.
- Die Kacheln der Übersicht werden anklickbar und führen in den jeweiligen Bereich; „Offene Vorschläge“ zählt alle Bereiche (heute nur Optimieren + Heuristik).
- Ist nichts zu tun: „Ihr Rechner ist in Ordnung“ mit Datum der Prüfung.

### 4.2 Sicherheit behebt selbst
Neue Behebungen statt „Einstellungen öffnen“ (bestehende Prüfregeln in `SecurityChecks.cs` bleiben, sie bekommen eine Änderung):

| Zeile | Behebung | Art |
|---|---|---|
| SmartScreen aus | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer` `SmartScreenEnabled` = `"Warn"` (REG_SZ). Ist `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\EnableSmartScreen` gesetzt → keine Behebung (Richtlinie). | Änderung `secvalue` |
| Benutzerkontensteuerung | `EnableLUA`=1 (Hinweis: wirkt erst nach Neustart), `ConsentPromptBehaviorAdmin`=5, `PromptOnSecureDesktop`=1 unter `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` – je nach gefundenem Mangel nur der betroffene Wert. | Änderung `secvalue` |
| Remotedesktop an | `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server` `fDenyTSConnections`=1. | Änderung `secvalue` |
| Defender-Echtzeitschutz aus (kein fremdes Schutzprogramm aktiv) | Echtzeitschutz einschalten über die Defender-Schnittstelle (`MSFT_MpPreference`, `DisableRealtimeMonitoring`=false), danach Zustand neu lesen. **Kein Undo** (WinWächter schaltet Virenschutz nie ab), daher Aktion, nicht im Sammelknopf von 4.1, sondern unter „Außerdem empfohlen“. Mit Manipulationsschutz blockiert Windows das oft ohne Fehlermeldung: Zeigt das Neulesen weiter „aus“, verständliche Meldung + direkter Verweis auf Windows-Sicherheit **[Entscheidung Claude]**. | Aktion |
| Manipulationsschutz aus | Lässt sich von Programmen absichtlich nicht einschalten → Verweis bleibt, Text erklärt das. | Verweis |
| Virendefinitionen alt | „Jetzt aktualisieren“ (vorhandene Funktion der Schutz-Seite wiederverwenden). | Aktion |
| Updates ausstehend | „Updates installieren“ in der App über die Windows-Update-Schnittstelle (WUA-COM): nur Updates, die Windows selbst automatisch installieren würde (nicht optional, keine Funktions-Upgrades, keine optionalen Treiber), mit Fortschritt (Suchen, Laden, Installieren). Bestätigung: „Updates lassen sich nicht über WinWächter zurücknehmen; einzelne können Sie in Windows unter Updateverlauf deinstallieren.“ | Aktion |
| Neustart nötig | „Jetzt neu starten“ (Bestätigung: „Speichern Sie offene Arbeit“). | Aktion |
| Lauschendes Programm (gelb) | „Eingehende Verbindungen sperren“: Firewall-Regel (eingehend, blockieren, für genau diese Programmdatei) mit Namen „WinWächter: <Programm>“ über die Firewall-COM-Schnittstelle; Undo löscht genau diese Regel. Nie für geschützte Hersteller, Windows-Ordner, Microsoft-signierte Dateien. „Von Defender prüfen lassen“ bleibt zusätzlich. | Änderung `fwrule` |
| Verschlüsselung | Bleibt Verweis; Text erklärt, warum (Wiederherstellungsschlüssel). | Verweis |

- `secvalue`: wie `perfvalue` (gemeinsame Logik `RegDword`), aber eigene Whitelist `SecurityValues` im Code; zusätzlich REG_SZ für SmartScreen. Ein Wert, der vorher fehlte, wird beim Undo gelöscht.
- Die Zeilen „Prüfen“-Chip: wo eine Behebung existiert, heißt der Knopf „Beheben“ bzw. die konkrete Handlung („Einschalten“, „Ausschalten“, „Installieren“).

### 4.3 Leistungsbremsen finden und beseitigen
- **Start-Bericht:** Jeder Eintrag „Bremsten den Start“ wird einem Fund aus Optimieren zugeordnet (Autostart, Aufgabe, Dienst – über Programmpfad bzw. Dateiname). Gelingt das, steht daneben „Nicht mehr mit Windows starten“ (wendet die vorhandene Änderung an: Autostart aus, Aufgabe deaktivieren, Dienst auf manuell). Gelingt es nicht: „Teil von Windows oder ein Treiber – nicht änderbar“ bzw. für geschützte Hersteller „Geschützt (z. B. Dell)“, ohne Knopf. Der Knopf „Autostart-Programme prüfen“ bleibt als Sprung.
- **Jetzt / Größte Verbraucher:** je Zeile „Beenden“ (Aktion; erst sanft schließen, nach 5 s Rückfrage „Erzwingen?“; nie Windows-Prozesse, Dienste, geschützte Hersteller, WinWächter selbst; Bestätigung „Nicht gespeicherte Arbeit geht verloren“) und, falls das Programm einen Autostart hat, „Nicht mehr mit Windows starten“ (Änderung `autostart`).
- **Schnellstart:** neue Zeile in Leistung → Einstellungen: `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power` `HiberbootEnabled` 0/1 über die `perfvalue`-Whitelist (das Lesen gibt es schon: `PerformanceScanner.FastStartupActive`, `ShowFastStartupNote`; es fehlt nur der Whitelist-Eintrag und die Zeile). Nicht empfohlen in beide Richtungen; Text erklärt Vor- und Nachteile **[Entscheidung Claude]**. Der Hinweis im Start-Bericht verweist auf diese Zeile.
- **Energiesparplan:** neue Zeile „Energiesparplan“ (Auswahl aus den vorhandenen Plänen) – Änderung `powerplan` (aktiven Plan setzen über die Energie-API; Undo setzt den vorherigen). Ist nicht „Ausgewogen“ aktiv, steht bei den gesperrten Energiemodus-Zeilen „Auf ‚Ausgewogen‘ umstellen“ statt nur des Hinweises.
- **Datenträger fast voll** (Systemlaufwerk < 10 % oder < 15 GB frei **[Entscheidung Claude]**): Hinweisband auf Übersicht und Leistung mit dem, was am meisten bringt (Cache, Temp, Ablage leeren, ggf. Ruhezustand-Datei) und Knopf „Platz schaffen“ → führt in die Liste aus 4.1 bzw. zu Optimieren/Speicher mit vorausgewählten sicheren Funden.

### 4.4 Werbe-Apps entfernen (neu)
- Ort: Seite „Programme“, eigener Abschnitt oben „Vorinstallierte Apps“ **[Entscheidung Claude]**; Empfehlungen auch in 4.1.
- Liste fester Paketnamen in `rules.json` → neuer lokaler Abschnitt `bloatApps` (nicht aus dem Netz, wie `privacy`): je Eintrag Paketfamilie (mit Platzhalter), Anzeigename, `recommended` ja/nein.
- **Verhältnis zur Schutzliste** **[Entscheidung Claude]**: Die Schutzliste enthält „Herausgeber Microsoft“ und `%ProgramFiles%\WindowsApps\`, fast alle Werbe-Apps sind aber Microsoft-Pakete dort. Für genau die Pakete, die namentlich in `bloatApps` stehen, gilt diese Microsoft-/WindowsApps-Regel deshalb nicht; alle anderen Schutzeinträge (Dell, Lexware, Namen, übrige Pfade) gelten weiter, und die Nie-Liste unten ist absolut. Was nicht in `bloatApps` steht, wird nie angeboten.
- Startliste (Umsetzer prüft Namen auf Build 26200):
  - empfohlen: Candy Crush/King, TikTok, Spotify, Disney+, Facebook/Instagram/LinkedIn/WhatsApp (sofern als echtes Paket installiert), Microsoft Solitaire Collection, Microsoft News (BingNews), Wetter (BingWeather), Tipps (Getstarted), Feedback-Hub, Hilfe anfordern (GetHelp), Kontakte (People), Clipchamp, Dev Home (von Microsoft eingestellt);
  - angeboten, aber nicht empfohlen (viele nutzen sie): Copilot, neues Outlook (`Microsoft.OutlookForWindows`), Teams privat (`MSTeams`), Microsoft 365 (`Microsoft.MicrosoftOfficeHub`), Quick Assist, Kurznotizen.
  - Auf dem Rechner des Nutzers sind (2026-11-12) davon installiert: Clipchamp, Copilot, Outlook (neu), Teams, Microsoft 365, Dev Home, Quick Assist, Kurznotizen; die klassischen Werbe-Apps (Candy Crush usw.) nicht.
- **Nie** in der Liste: Xbox-App (`Microsoft.GamingApp`), Xbox-Identity, Game Bar, Gaming Services, Xbox-Overlays (Nutzer spielt; Anmeldung für Spiele hängt daran), Store, App Installer (winget), Fotos, Rechner, Editor, Terminal, Paint, Snipping Tool, Kamera, Uhr, Windows-Sicherheit, Edge, Systempakete (`SignatureKind` System), Frameworks, Lexware/Dell-Apps.
- **Startmenü-Platzhalter** (angeheftete Kacheln wie „Spotify“, die erst beim Klick installieren, ohne echtes Paket) sind nicht Teil von 0.5.0 **[Entscheidung Claude]**: anderes Verfahren (Startmenü-Layout), kein sauberes Undo.
- **Benutzerkonto:** Entfernt wird nur für das angemeldete Benutzerkonto. WinWächter läuft erhöht; das wirkt nur richtig, wenn das erhöhte Konto dasselbe ist wie das angemeldete (SID-Vergleich mit `InteractiveUserContext`). Sonst ist der Abschnitt gesperrt mit Hinweis **[Entscheidung Claude]**. Beim Nutzer ist es dasselbe Konto (Administrator mit Benutzerkontensteuerung).
- **Schnittstelle:** Paket-Manager-API von Windows (WinRT `PackageManager`; dafür darf das Projekt auf ein versioniertes Windows-Ziel wie `net10.0-windows10.0.19041.0` umgestellt werden – das ist Microsofts eigene Windows-SDK-Anbindung, kein Drittanbieter). Alternativ darf der Umsetzer die eingebauten Appx-Befehle von Windows PowerShell nutzen, wenn das einfacher und genauso sicher ist **[Entscheidung Claude]**.
- Änderung `appx`:
  - Undo: Ist das Paket im Windows-Abbild vorhanden (Dateien bleiben in `WindowsApps`), wird es neu registriert (`RegisterPackageByFullNameAsync` bzw. aus dem Manifest) → echtes Undo.
  - Sonst (aus dem Store installiert, Dateien gelöscht): Undo nicht möglich; der Verlauf zeigt „Im Store neu installieren“ (Store-Link auf genau dieses Paket). Die Bestätigung kennzeichnet solche Einträge vorab als „nur über den Store wiederherstellbar“.
- Laufende App wird vorher nicht beendet; scheitert das Entfernen, klare Meldung.

### 4.5 Windows-Werbung abschalten (neu)
- Neue Gruppe „Werbung & Vorschläge“ auf der Datenschutz-Seite, Werte in `rules.json` → `privacy` (vorhandene Änderung `regvalue`, lokal, nicht aus dem Netz).
- **Einiges gibt es schon:** `privacy` enthält bereits `SubscribedContent-338389/338388/353694/353696Enabled`, `SoftLandingEnabled` und `Start_IrisRecommendations`. Diese Einträge behalten ihre IDs (alte Journal-Datensätze verweisen darauf) und werden nur in die neue Gruppe einsortiert; keine doppelten Einträge (`RuleSet.FindPrivacyValue` nimmt den ersten Treffer).
- Neu hinzu kommen nur fehlende Werte. Kandidaten (Umsetzer prüft auf Build 26200 per Vorher/Nachher-Vergleich in den Windows-Einstellungen, ob genau diese Werte geschrieben werden; was nicht passt, fällt weg): Konto-Hinweise im Startmenü (`Start_AccountNotifications`), `SystemPaneSuggestionsEnabled`, Sperrbildschirm-Tipps (`RotatingLockScreenOverlayEnabled`), Werbung im Explorer (`ShowSyncProviderNotifications`), „Begrüßungserlebnis nach Updates“ (`UserProfileEngagement` `ScoobeSystemSettingEnabled`). Alle `recommended`.

### 4.6 Computerschutz einschalten (neu)
- Prüfung (neue Zeile auf der Seite Sicherheit, Gruppe „Absicherung“): Ist der Computerschutz für das Systemlaufwerk aus, oder ist ein dafür nötiger Dienst deaktiviert (bei diesem Rechner: Wiederherstellungspunkt scheitert mit 0x80070422 = „Dienst deaktiviert“)? Der Umsetzer stellt auf dem Rechner (nur lesend) fest, welcher Dienst deaktiviert ist (Kandidaten: Volumeschattenkopie `VSS`, `swprv`).
- Behebung: Änderung `sysprotect` – betroffenen Dienst von „Deaktiviert“ auf seinen Windows-Standard (manuell) setzen, Computerschutz für C: einschalten (WMI `SystemRestore.Enable`), Speicherplatz-Grenze auf Windows-Standard lassen.
- **Undo nur teilweise** **[Entscheidung Claude]**: Undo setzt nur den Dienst-Starttyp zurück. Den Computerschutz schaltet WinWächter nie wieder ab, weil Windows dabei alle Wiederherstellungspunkte löscht (auch spätere) – das wäre endgültig. Bestätigung und Verlauf kennzeichnen das als „nur teilweise rückgängig“.
- Keine Behebung nur dann, wenn eine Richtlinie den Computerschutz abschaltet (`HKLM\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore` `DisableSR`/`DisableConfig`); ob ein anderes Programm den Dienst abgeschaltet hat, lässt sich nicht erkennen und ist kein Ausschlussgrund.
- Auf der Sicherheitsseite und in 4.1 läuft `sysprotect` als eigener Schritt vor allen anderen Änderungen (siehe 4.1).
- Empfohlen (in 4.1). Danach meldet WinWächter beim nächsten Anwenden keinen Fehler mehr beim Wiederherstellungspunkt.

### 4.7 Versteckte Hintergrund-Bremsen der Browser (neu)
- Neue Gruppe „Im Hintergrund“ auf Leistung → Einstellungen.
- Edge: „Start-Boost“ aus, „Erweiterungen und Apps im Hintergrund weiter ausführen, wenn Edge geschlossen ist“ aus. Chrome: „Apps im Hintergrund weiter ausführen“ aus. Opera nur, wenn es einen gleichwertigen sauberen Schalter gibt (Umsetzer prüft).
- Umsetzung **[Entscheidung Claude]**: über die offiziellen Browser-Richtlinien (`HKLM\SOFTWARE\Policies\Microsoft\Edge` `StartupBoostEnabled`=0, `BackgroundModeEnabled`=0; `HKLM\SOFTWARE\Policies\Google\Chrome` `BackgroundModeEnabled`=0), Whitelist in `perfvalue`. Grund: Die eigenen Einstellungsdateien der Browser sind geschützt bzw. nur bei geschlossenem Browser änderbar. Gab es für den Browser vorher noch keine Richtlinie, sagen Zeile und Bestätigung deutlich: „Edge zeigt danach ‚Von Ihrer Organisation verwaltet‘; das ist normal und verschwindet beim Rückgängigmachen.“ Undo löscht die Werte (fehlte ein Wert vorher) bzw. stellt den alten Wert her.
- Auf dem Rechner des Nutzers stehen bereits 11 fremde Edge-Richtlinien (u. a. `StartupBoostEnabled`, `HideFirstRunExperience`; vermutlich von einem früheren Tuning-Programm), deshalb zeigt Edge dort schon „verwaltet“. Ein fremder `StartupBoostEnabled`-Wert fällt unter die Richtlinien-Regel aus Abschnitt 3: WinWächter zeigt den Zustand an, ändert ihn aber nicht.
- Empfohlen: ja für beide (spart Arbeitsspeicher und Akku, Browser startet nur Sekundenbruchteile langsamer).
- Teams, Steam, EA, Ubisoft usw. im Hintergrund bleiben Sache des Autostarts (Optimieren), hier nur ein Hinweis mit Sprung, wenn solche Autostarts aktiv sind.

### 4.8 Rechtsklick-Menü entrümpeln (neu)
- Ort: Seite „Optimieren“, neue Gruppe „Rechtsklick-Menü“ **[Entscheidung Claude]**.
- Liste: Kontextmenü-Erweiterungen (`shellex\ContextMenuHandlers` unter `*`, `AllFilesystemObjects`, `Directory`, `Directory\Background`, `Folder`, `Drive`) und statische Befehle (`shell\<verb>`) von Programmen; je Zeile Programmname/Hersteller (aus der COM-Registrierung bzw. dem Befehl), Ort („Dateien“, „Ordner“, „Hintergrund“). Ausgeblendet: Windows/Microsoft, geschützte Hersteller, WinWächter.
- Abschalten: Erweiterungen über den offiziellen Sperrmechanismus `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` (Wert = CLSID); statische Befehle über den Wert `LegacyDisable`. Änderung `shellext`; Undo entfernt genau den gesetzten Wert.
- Nichts vorausgewählt, nicht in 4.1 (Geschmackssache). Nach dem Anwenden Angebot „Explorer neu starten“ (Aktion), damit es sofort wirkt.
- Hinweis im Text: Windows 11 zeigt viele dieser Einträge erst unter „Weitere Optionen anzeigen“; Einträge aus App-Paketen (neues Menü) lassen sich so nicht abschalten und werden nicht gelistet **[Entscheidung Claude]**.

### 4.9 Kleinere Lücken
- **Browser-Cache gesperrt** (Browser läuft): Knopf „Browser schließen und leeren“ – fragt nach, schließt den Browser sanft (Fenster schließen), wartet bis 10 s, bietet dann „Erzwingen“ an (Hinweis: offene Eingaben gehen verloren); danach Cache neu prüfen und vorausgewählt anbieten. Aktion.
- **Unerwünschte Programme gesammelt entfernen** (Programme mit Markierung „Unerwünscht“/„Miner“): Knopf „Alle unerwünschten entfernen (n)“ – nacheinander die vorhandene Deinstallation je Programm (mit allen heutigen Regeln inkl. Benutzerrechte-Start), danach die Restesuche. Aktion (endgültig), Bestätigung mit Liste.
- **Verlauf:** Fehlgeschlagene Einträge bekommen „Erneut versuchen“ (neue Sitzung mit derselben Änderung aus dem Datensatz, gleiche Whitelist-Prüfung).
- **Schutz-Seite:** Echtzeitschutz „Aus“ bekommt dieselbe Behebung wie 4.2; „Letzter Scan“-Kachel bekommt „Jetzt scannen“ (vorhandene Funktion).

## 5. Technik
- Neue Change-Kinds, registriert wie `FirewallChange.Register`: `secvalue`, `fwrule`, `powerplan`, `appx`, `sysprotect` (Undo nur teilweise), `shellext`. Defender-Echtzeitschutz ist eine Aktion, kein Change-Kind. `perfvalue` bekommt neue Whitelist-Einträge (Schnellstart, Browser-Richtlinien).
- Neue lokale `rules.json`-Abschnitte `bloatApps` (und neue `privacy`-Einträge): dürfen wie `privacy` nie aus dem Netz kommen; `RuleSet` erzwingt das. Schutzliste wird bei allen neuen Scannern angewendet.
- Ein neuer zentraler „Empfehlungs-Sammler“ für 4.1, der die Scanner der Bereiche aufruft und `Finding`s mit Änderung zusammenführt; die Bereichsseiten nutzen weiter ihre eigenen Scanner (keine doppelte Logik).
- Aktionen laufen über eine kleine gemeinsame Schicht (Bestätigung, Protokoll, Fehlermeldung), nicht über das Journal.
- Nur Windows-eigene Schnittstellen (Registry, WMI, COM für Firewall/Windows Update, Paket-Manager-API, Energie-API). Keine Drittanbieter-Pakete.
- Tests: je Change-Kind Apply/Undo/Drift/FromRecord-Whitelist; Scanner mit Fakes; VM-Tests (Bestätigungstexte, „endgültig“-Kennzeichnung, Sperre ohne Admin); `ViewSmokeTests` hell/dunkel für geänderte Seiten.

## 6. Was der Umsetzer selbst entscheidet
- Genaue Texte, Reihenfolge der Gruppen, Klassen-/Ordnerstruktur.
- Ob 4.1 als eigenes Fenster/Panel oder als Bereich auf der Übersicht erscheint – Hauptsache ein Knopf, eine Liste, ein Anwenden.
- Welche der Kandidatenwerte in 4.5/4.7 auf Build 26200 wirklich wirken (nur die, die nachweislich dem Windows-/Browser-Schalter entsprechen).
- Die Reihenfolge der Umsetzung (Vorschlag: 4.2 und 4.6 zuerst, dann 4.3, 4.4–4.8, 4.9, zuletzt 4.1, weil 4.1 alles andere einsammelt).

## 7. Erledigt heißt
- Alle Tests grün; Einzel- und Gesamtprüfung wie bei 0.4.
- Auf dem Rechner des Nutzers (nur mit seiner Zustimmung): Computerschutz eingeschaltet und Wiederherstellungspunkt klappt; „Rechner in Ordnung bringen“ einmal komplett durchlaufen und im Verlauf zurückgenommen; je eine Behebung aus 4.2 (z. B. Firewall-Regel), 4.4 (eine Werbe-App), 4.5, 4.7, 4.8 angewendet und zurückgenommen; Updates installieren nur, wenn der Nutzer es will.
- Version 0.5.0, CHANGELOG, Roadmap (Reihenfolge 0.6.0/Sprachen nachtragen), Release erst nach Freigabe.
