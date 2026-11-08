# Icon für WinWächter

## Prompt für den Bildgenerator

App icon for a Windows security and optimization utility called "WinWächter". A minimal, precise emblem: a rounded shield whose inner shape is formed by a gauge/dial with a single needle pointing slightly right of center, suggesting calm control and vigilance. Flat vector style with one subtle depth layer, no gradients except a very soft tonal shift. Color palette: deep petrol teal (#0B5C6B) as the main color, off-white (#F2F4F2) for the needle and inner details, one small warm amber accent (#E0A040) on the needle tip. Clean geometric lines, generous padding, centered, readable at 16x16 pixels. Transparent background, no text, no letters, no drop shadow, no glossy effects, no 3D, no stock-icon look.

## Zieldatei

`C:\Users\tahsi\dev\winwaechter\assets\icon\winwaechter-1024.png` (1024×1024 PNG, transparenter Hintergrund)

## Danach: ICO erzeugen

```powershell
.\tools\make-ico.ps1
```

Das Skript schreibt `assets\icon\winwaechter.ico` (16/24/32/48/64/128/256). Sobald die Datei existiert, verwenden sie automatisch die App (`ApplicationIcon`) und der Installer (`SetupIconFile`).
