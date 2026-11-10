# Génère icon\app.ico (icône de l'exe, du menu Démarrer et de la recherche
# Windows) à partir des tracés de DuckLogo.cs : le canard plein de l'app
# (corps blanc, œil évidé, bec orange, aile verte) sur une tuile sombre,
# lisible sur les thèmes clair et sombre.
# Usage : powershell -File icon\make-app-icon.ps1 [-Preview chemin.png]

param([string]$Preview)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$src = Get-Content (Join-Path $root 'DuckLogo.cs') -Raw
function PathOf($name) {
    if ($src -notmatch "$name\s*=\s*""([^""]+)""") { throw "$name introuvable dans DuckLogo.cs" }
    $Matches[1]
}

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

public static class DuckIcon
{
    static GraphicsPath Parse(string d)
    {
        var p = new GraphicsPath(FillMode.Alternate);
        var cur = PointF.Empty;
        int i = 0;
        while (i < d.Length)
        {
            char cmd = d[i++];
            int end = i;
            while (end < d.Length && !char.IsLetter(d[end])) end++;
            var parts = d.Substring(i, end - i).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var n = Array.ConvertAll(parts, s => float.Parse(s, CultureInfo.InvariantCulture));
            i = end;
            switch (cmd)
            {
                case 'M': p.StartFigure(); cur = new PointF(n[0], n[1]); break;
                case 'L': { var nx = new PointF(n[0], n[1]); p.AddLine(cur, nx); cur = nx; break; }
                case 'C': { var nx = new PointF(n[4], n[5]); p.AddBezier(cur, new PointF(n[0], n[1]), new PointF(n[2], n[3]), nx); cur = nx; break; }
                case 'Z': p.CloseFigure(); break;
            }
        }
        return p;
    }

    static Pen Round(Color c, float w)
    {
        return new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    }

    public static byte[] Png(int size, string outline, string eye, string wing, string beak)
    {
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // Tuile sombre aux coins arrondis (22 % du côté).
                float r = size * 0.22f;
                using (var tile = new GraphicsPath())
                {
                    float s = size, dd = 2 * r;
                    tile.AddArc(0, 0, dd, dd, 180, 90);
                    tile.AddArc(s - dd, 0, dd, dd, 270, 90);
                    tile.AddArc(s - dd, s - dd, dd, dd, 0, 90);
                    tile.AddArc(0, s - dd, dd, dd, 90, 90);
                    tile.CloseFigure();
                    using (var b = new SolidBrush(Color.FromArgb(0x1B, 0x1D, 0x22))) g.FillPath(b, tile);
                }

                // Canard centré : boîte de 560 ramenée à ~78 % de la tuile
                // (la silhouette, trait compris, va de x 54 à 506, y 4 à 547).
                float k = size * 0.78f / 560f;
                g.TranslateTransform(size / 2f - 280f * k, size / 2f - 275.5f * k);
                g.ScaleTransform(k, k);

                var orange = Color.FromArgb(255, 122, 16);
                var green = Color.FromArgb(24, 140, 70);
                using (var p = Parse(beak))
                using (var b = new SolidBrush(orange))
                using (var pen = Round(orange, 40))
                { g.FillPath(b, p); g.DrawPath(pen, p); }
                using (var body = Parse(outline + " " + eye))
                using (var b = new SolidBrush(Color.White))
                    g.FillPath(b, body);
                using (var p = Parse(outline))
                using (var pen = Round(Color.White, 40))
                    g.DrawPath(pen, p);
                using (var p = Parse(wing))
                using (var b = new SolidBrush(green))
                using (var pen = new Pen(green, 24) { LineJoin = LineJoin.Round })
                { g.FillPath(b, p); g.DrawPath(pen, p); }
            }
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }
    }

    // Fichier ICO à entrées PNG (accepté par Windows depuis Vista).
    public static void Ico(string path, int[] sizes, string outline, string eye, string wing, string beak)
    {
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) images[i] = Png(sizes[i], outline, eye, wing, beak);
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }
}
'@

$out = Join-Path $PSScriptRoot 'app.ico'
[DuckIcon]::Ico($out, [int[]](16, 20, 24, 32, 40, 48, 64, 96, 256),
    (PathOf 'OutlinePath'), (PathOf 'EyeLargePath'), (PathOf 'WingLargePath'), (PathOf 'BeakPath'))
if ($Preview) {
    [IO.File]::WriteAllBytes($Preview,
        [DuckIcon]::Png(256, (PathOf 'OutlinePath'), (PathOf 'EyeLargePath'), (PathOf 'WingLargePath'), (PathOf 'BeakPath')))
}
"Écrit : $out"
