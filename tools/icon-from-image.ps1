# Turns a square icon image with a plain light background into a transparent PNG.
# Only background connected to the image border is removed (flood fill), so light
# details inside the motif stay. Edges get soft alpha based on distance to white.
param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$Target,
    [int]$Size = 1024,
    [int]$Padding = 40,      # transparent border in output pixels
    [int]$Tolerance = 38     # how far from pure white still counts as background
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class IconCut {
  public static Bitmap Cut(Bitmap src, int tol, out Rectangle bounds) {
    int w = src.Width, h = src.Height;
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, w, h);
    var data = bmp.LockBits(new Rectangle(0,0,w,h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var px = new byte[w*h*4]; Marshal.Copy(data.Scan0, px, 0, px.Length);
    Func<int,int> dist = i => 255 - Math.Min(px[i], Math.Min(px[i+1], px[i+2]));   // 0 = white
    var bg = new bool[w*h]; var q = new Queue<int>();
    for (int x = 0; x < w; x++) { q.Enqueue(x); q.Enqueue((h-1)*w + x); }
    for (int y = 0; y < h; y++) { q.Enqueue(y*w); q.Enqueue(y*w + w-1); }
    while (q.Count > 0) {
      int p = q.Dequeue(); if (bg[p] || dist(p*4) > tol) continue; bg[p] = true;
      int x = p % w, y = p / w;
      if (x > 0) q.Enqueue(p-1); if (x < w-1) q.Enqueue(p+1); if (y > 0) q.Enqueue(p-w); if (y < h-1) q.Enqueue(p+w);
    }
    int minX = w, minY = h, maxX = 0, maxY = 0;
    for (int p = 0; p < w*h; p++) {
      int i = p*4;
      if (bg[p]) { px[i+3] = 0; continue; }
      // soft edge: pixels next to background fade with their whiteness
      int x = p % w, y = p / w;
      bool edge = (x>0&&bg[p-1])||(x<w-1&&bg[p+1])||(y>0&&bg[p-w])||(y<h-1&&bg[p+w]);
      if (edge) { int d = dist(i); px[i+3] = (byte)Math.Min(255, d * 255 / Math.Max(1, tol * 3)); }
      if (x<minX) minX=x; if (y<minY) minY=y; if (x>maxX) maxX=x; if (y>maxY) maxY=y;
    }
    Marshal.Copy(px, 0, data.Scan0, px.Length); bmp.UnlockBits(data);
    bounds = Rectangle.FromLTRB(minX, minY, maxX+1, maxY+1);
    return bmp;
  }
}
"@
$src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
try {
    $bounds = [System.Drawing.Rectangle]::Empty
    $cut = [IconCut]::Cut($src, $Tolerance, [ref]$bounds)
    $side = [Math]::Max($bounds.Width, $bounds.Height)
    $inner = $Size - 2 * $Padding
    $scale = $inner / $side
    $out = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'HighQuality'
    $dw = [int]($bounds.Width * $scale); $dh = [int]($bounds.Height * $scale)
    $dest = New-Object System.Drawing.Rectangle ([int](($Size - $dw) / 2)), ([int](($Size - $dh) / 2)), $dw, $dh
    $g.DrawImage($cut, $dest, $bounds, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $out.Save($Target, [System.Drawing.Imaging.ImageFormat]::Png)
    "Gespeichert: $Target ($Size x $Size, Motiv $($bounds.Width)x$($bounds.Height) px aus der Quelle)"
} finally { $src.Dispose() }
