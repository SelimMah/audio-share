using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace AudioShare;

/// <summary>
/// Logo de l'app (le canard), en tracés vectoriels sur un carré de 512 : le
/// corps (œil et aile évidés) et le bec, seul élément coloré. Les tracés
/// sont ceux de icon\logo-*.svg — ne pas les retoucher à la main.
/// </summary>
internal static class DuckLogo
{
    private const float Box = 512f;

    private static readonly Color Orange = Color.FromArgb(255, 122, 16);
    private static readonly Color Green = Color.FromArgb(24, 140, 70);

    /// <summary>Dessine le logo dans <paramref name="bounds"/> : d'une seule
    /// teinte, ou avec son bec orange.</summary>
    public static void Draw(Graphics g, RectangleF bounds, Color body, bool colors)
    {
        var state = g.Save();
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / Box, bounds.Height / Box);
        // Le bec d'abord : il déborde un peu sous le corps, pour ne laisser
        // aucun liseré à la jointure.
        Fill(g, BeakPath, colors ? Orange : body);
        if (colors)
        {
            // L'aile est un évidement du corps : on la remplit dessous, avec
            // un contour qui déborde pour la même raison que le bec.
            Fill(g, WingPath, Green);
            using var path = Parse(WingPath);
            using var pen = new Pen(Green, 6f);
            g.DrawPath(pen, path);
        }
        Fill(g, BodyPath, body);
        g.Restore(state);
    }

    private static void Fill(Graphics g, string data, Color color)
    {
        using var path = Parse(data);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Sous-ensemble de la syntaxe SVG, suffisant ici : M, L, C et Z
    /// en coordonnées absolues.</summary>
    private static GraphicsPath Parse(string data)
    {
        // Alternate = evenodd du SVG : les contours intérieurs font des trous.
        var path = new GraphicsPath(FillMode.Alternate);
        var cur = PointF.Empty;
        int i = 0;
        while (i < data.Length)
        {
            char cmd = data[i++];
            int end = i;
            while (end < data.Length && !char.IsAsciiLetter(data[end])) end++;
            var n = Array.ConvertAll(
                data[i..end].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                s => float.Parse(s, CultureInfo.InvariantCulture));
            i = end;

            switch (cmd)
            {
                case 'M':
                    path.StartFigure();
                    cur = new PointF(n[0], n[1]);
                    break;
                case 'L':
                    path.AddLine(cur, cur = new PointF(n[0], n[1]));
                    break;
                case 'C':
                    path.AddBezier(cur, new PointF(n[0], n[1]), new PointF(n[2], n[3]),
                        cur = new PointF(n[4], n[5]));
                    break;
                case 'Z':
                    path.CloseFigure();
                    break;
            }
        }
        return path;
    }

    private const string BodyPath =
        "M240.3 503.3C177.1 498.9 119.2 464.7 83.4 410.7C67.1 386.2 55.0 353.9 51.2 325.1C50.8 322.3 50.3 318.5 50.1 316.6C49.3 310.8 49.5 125.9 50.3 122.5C50.6 120.9 51.1 118.5 51.3 117.1C53.1 102.6 61.7 81.0 71.1 66.9C120.2 -5.9 222.1 -12.6 278.9 53.3C297.1 74.3 308.5 100.8 311.5 128.8C311.7 131.0 311.9 159.4 312.0 192.0C312.0 250.1 312.0 251.1 312.9 252.0C313.8 252.9 314.8 252.9 387.2 252.9C459.9 252.9 460.5 253.0 461.5 253.9C462.5 255.0 462.9 310.3 461.9 316.9C461.6 318.6 461.1 322.3 460.8 325.1C459.4 336.0 456.9 346.7 452.6 360.1C431.4 427.2 375.8 480.0 308.2 497.4C306.0 498.0 303.4 498.6 302.5 498.9C286.9 502.9 261.0 504.7 240.3 503.3ZM270.3 435.9C277.6 435.3 291.4 432.5 297.8 430.3C346.0 414.2 380.5 376.6 392.1 327.8C393.5 321.9 393.5 321.8 392.3 320.6C391.4 319.7 122.8 319.1 120.4 320.0C117.2 321.2 122.7 341.3 130.6 357.8C149.2 396.6 190.2 428.1 229.9 434.2C230.9 434.3 233.1 434.7 234.8 435.0C244.2 436.6 257.4 436.9 270.3 435.9ZM225.0 177.7C262.0 168.9 267.3 118.5 232.9 102.1C205.0 88.7 173.0 111.8 176.5 142.7C179.2 166.6 202.1 183.1 225.0 177.7Z";

    // Même contour que l'évidement de l'aile dans BodyPath.
    private const string WingPath =
        "M270.3 435.9C277.6 435.3 291.4 432.5 297.8 430.3C346.0 414.2 380.5 376.6 392.1 327.8C393.5 321.9 393.5 321.8 392.3 320.6C391.4 319.7 122.8 319.1 120.4 320.0C117.2 321.2 122.7 341.3 130.6 357.8C149.2 396.6 190.2 428.1 229.9 434.2C230.9 434.3 233.1 434.7 234.8 435.0C244.2 436.6 257.4 436.9 270.3 435.9Z";

    private const string BeakPath =
        "M310.0 191.6C309.6 190.9 309.3 182.0 309.1 159.4C308.8 124.1 308.9 125.1 304.8 109.1C302.6 100.3 302.0 96.8 302.7 96.1C304.0 94.7 417.1 94.4 419.0 95.7C421.5 97.5 419.7 113.2 415.5 125.5C406.6 151.9 389.8 170.9 364.8 182.9C351.7 189.2 341.2 191.8 327.0 192.1C322.5 192.2 316.9 192.4 314.6 192.5C310.8 192.6 310.5 192.6 310.0 191.6Z";
}
