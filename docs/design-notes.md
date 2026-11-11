# WinWächter – Gestaltungsnotizen

Stand: 2026-11-11 · Skill `frontend-design`

## Richtung: Messinstrument statt Dashboard

WinWächter ist ein Prüfgerät für den eigenen Rechner: Es misst, zeigt Befunde mit Gründen und ändert erst nach Bestätigung. Die Oberfläche soll wirken wie ein ruhiges, präzises Messinstrument (Prüfstand, Schalttafel), nicht wie ein Sicherheits-Dashboard mit Schild-Grafik, Neonfarben und Kachelraster.

- **Ein Blickfang:** Auf der Übersicht steht ein Zustandssatz in großer DIN-Schrift („Defender ist aktiv. Noch nicht geprüft.“) mit einer kleinen gefüllten Zustandsmarke. Alles andere bleibt leise.
- **Ablesewerte statt Kacheln:** Messwerte stehen nebeneinander in einer Leiste, getrennt durch feine Skalenstriche, mit Tabellenziffern. Keine Karten mit Schatten.
- **Skala als Fortschritt:** Lange Vorgänge zeigen eine Strichskala (Vektor, `DrawingBrush`) mit Laufzeit, keine generische Ladeleiste.
- **Farbe ist Information:** Die Akzentfarbe Petrol markiert nur Bedienbares und den aktiven Ort. Ocker und Ochsenblut gibt es nur für Einstufungen. Kein Alarmrot, keine Verläufe, keine Glas-Effekte.
- **Sprache:** sachlich, aktiv, Satzanfang groß, keine Großbuchstaben-Etiketten.
- **Grafik:** nur Schrift, Vektorformen (`Path`, `Ellipse`) und Glyphen aus Segoe Fluent Icons (Rückfall Segoe MDL2 Assets). App-Icon: `assets/icon` (mit Gemini erstellt), auch in der Seitenleiste.

## Schrift

| Rolle | Schrift | Größe / Schnitt |
|---|---|---|
| Zustandssatz | Bahnschrift (DIN 1451) | 34, Light |
| Seitentitel | Bahnschrift | 26, SemiLight (350) |
| Abschnitt | Bahnschrift | 17, SemiBold, SemiCondensed |
| Messwert | Bahnschrift | 28, Light, Tabellenziffern |
| Fließtext | Segoe UI Variable Text | 14, Regular |
| Nebentext | Segoe UI Variable Text | 12.5, Regular |
| Chip / Einstufung | Segoe UI Variable Small | 11.5, SemiBold |

Bahnschrift ist bei Windows 10/11 dabei und trägt die deutsche DIN-Anmutung von Schildern und Messgeräten. Zeilenlänge der Erklärtexte max. ca. 75 Zeichen (`MaxWidth` 620).

## Abstände und Formen

- Raster 4: 4 · 8 · 12 · 16 · 24 · 32 · 48.
- Seitenrand 32 links/rechts, 28 oben.
- Radien nach Rang: Fläche 6, Knopf 4, Chip 3, Fokusrahmen 6.
- Linien 1 px, Skalenstriche 1 px.
- Fokus: 2 px Rahmen in Akzentfarbe mit 2 px Abstand, auf jedem bedienbaren Element.

## Farben (Tokens)

| Token | Hell | Dunkel | Zweck |
|---|---|---|---|
| `Canvas` | `#E8ECEA` | `#1A1F22` | Fenstergrund (Mineralgrau) |
| `Surface` | `#F6F8F7` | `#222A2D` | Flächen, Listen |
| `Rail` | `#DDE3E0` | `#151A1C` | Navigationsleiste |
| `Line` | `#C5CDC9` | `#354044` | Linien, Skalenstriche |
| `Ink` | `#1C2427` | `#E2E8E6` | Text |
| `InkMuted` | `#4F5C5A` | `#A2AEAB` | Nebentext (≥ 4,5 : 1 auf Surface) |
| `Accent` | `#0B5D6B` | `#6CC0CE` | Petrol: Bedienbares, aktiver Ort, Fokus |
| `OnAccent` | `#FFFFFF` | `#0A272C` | Text auf Akzent |
| `AccentSoft` | `#D2E5E7` | `#1E3A3F` | Auswahl, Hover |
| `Good` | `#2E6A3E` | `#7EC690` | Zustand in Ordnung |
| `Watch` / `WatchSoft` | `#835500` / `#F2E5C6` | `#E2B65C` / `#3A3121` | Auffällig, Entbehrlich |
| `Alarm` / `AlarmSoft` | `#962B23` / `#F2DAD5` | `#F08B7F` / `#43251F` | Verdächtig, Unerwünscht |

Hell/Dunkel folgt `AppsUseLightTheme` und wechselt live. Die Titelleiste bleibt die von Windows (Snap-Layouts, Tastatur), bekommt aber per DWM den dunklen Modus und die Farbe von `Canvas`.

## Abgelehnte Standardlösungen

- Schild-Logo mit großem Prozent-Score → ersetzt durch Zustandssatz.
- Kachelraster mit Schatten → Messleiste mit Skalenstrichen.
- Nachtblau mit Neon-Akzent → Mineralgrau mit Petrol.

## Umsetzung (WPF)

- Tokens: `src/WinWaechter.App/Theme/Tokens.xaml` (Schrift, Größen, Radien), Paletten `Light.xaml`/`Dark.xaml` (gleiche Schlüssel, inkl. Skalen-Pinsel), Stile `Controls.xaml` (nur `DynamicResource` für Farben).
- Navigation: Glyphen Übersicht `E80F`, Optimieren `E90F`, Leistung `EC4A` (SpeedHigh), Schutz `EA18`, Sicherheit `E8D7`, Datenschutz `E72E`, Programme `E71D`, Verlauf `E81C`, Einstellungen `E713`.
- Anrede in Texten: „Sie“, sonst unpersönlich.
- Endgültige Aktionen (Bereinigen, Prozess beenden, Ablage leeren) nutzen den Stil `Button.Final` (Ochsenblut-Rahmen); im Dialog liegt der Fokus dann auf „Abbrechen“.

## Sicherheit (Ampel)

- Kein neuer Farbwert: Grün = `Good`, Gelb = `Watch`, Rot = `Alarm`, Grau = `InkMuted`. Der Chip daneben nutzt die Töne `Good`/`Watch`/`Alarm`/neutral; die Farbe ist nie das einzige Signal, der Chip nennt den Zustand („In Ordnung“, „Prüfen“, „Handlungsbedarf“, „Nicht prüfbar“).
- Zeile: gefüllter Punkt (10 px, `Ellipse`), Titel + Chip, Befund, Erklärung (`Text.Small`, `InkMuted`, max. 620), Aktionsknopf rechts im Standardstil (nie `Button.Final`). Unterzeilen sind 24 px eingerückt.
- Eine Zeile darf ihren Zustand selbst benennen (`SecurityItem.Chip`): Erweiterungen zeigen „In Ordnung“, „Ansehen“ (Ton `Watch`) oder „Deaktiviert“ (Grau mit Ton `Calm`). Details stehen zeilenweise in der Erklärung (Version · Datum, Rechte, Empfehlung).
- Übersicht: Ablesewert „Sicherheit“ in der Messleiste, Zustandsmarke + „n rot · n gelb“ bzw. „Alles in Ordnung“.

## Leistung

- Messleiste „Jetzt“ wie auf der Übersicht: Skalenlinie, drei Ablesewerte (Prozessor, Arbeitsspeicher, Datenträger) mit Tabellenziffern, getrennt durch 1-px-Linien; unter jedem Wert eine Strichskala (`Scale`), gefüllt bis zum Messwert.
- „Größte Verbraucher“ als Tabelle in einer `SurfaceBox`, Zahlen rechtsbündig mit Tabellenziffern.
- Einstellungen: Zeilen wie Datenschutz, rechts immer eine Auswahlliste (ein Bedienelement für alle Zeilen); Chips für Zustand, „empfohlen: …“, „Gilt nach einem Neustart“ und „geändert“. Hintergrund-Apps stecken in einem aufklappbaren Block.
