using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace AudioShare;

/// <summary>
/// Logo de l'app (le canard), dessiné au trait comme les icônes Fluent de
/// Windows, sur un carré de 560 : contour de la silhouette, aile pleine aux
/// coins arrondis, œil en point et bec. Au repos tout est d'une seule teinte ; actif, le bec se
/// remplit d'orange et l'aile passe au vert. Les tracés sont ceux de
/// icon\logo-*.svg — ne pas les retoucher à la main.
/// </summary>
internal static class DuckLogo
{
    private const float Box = 560f;

    /// <summary>Épaisseur du trait, dans la boîte de 560 (≈ 1,5 px à 20 px).</summary>
    internal const float Stroke = 40f;

    /// <summary>Trait qui arrondit les coins de l'aile pleine.</summary>
    internal const float WingRounding = 24f;

    private static readonly Color Orange = Color.FromArgb(255, 122, 16);
    private static readonly Color Green = Color.FromArgb(24, 140, 70);

    /// <summary>Dessine le logo dans <paramref name="bounds"/> : d'une seule
    /// teinte, ou avec son bec orange et son aile verte. <paramref name="stroke"/>
    /// est l'épaisseur du trait, dans la boîte de 560.</summary>
    public static void Draw(Graphics g, RectangleF bounds, Color body, bool colors,
        float stroke = Stroke)
    {
        var state = g.Save();
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / Box, bounds.Height / Box);

        // Le bec d'abord : le contour de la tête passe par-dessus sa base.
        using (var beak = Parse(BeakPath))
        {
            if (colors)
            {
                using var fill = new SolidBrush(Orange);
                g.FillPath(fill, beak);
            }
            using var pen = RoundPen(colors ? Orange : body, stroke);
            g.DrawPath(pen, beak);
        }
        using (var outline = Parse(OutlinePath))
        using (var pen = RoundPen(body, stroke))
            g.DrawPath(pen, outline);
        // Aile pleine : remplie, plus un trait fin de même couleur aux
        // jointures rondes pour en arrondir les coins.
        using (var wing = Parse(WingPath))
        using (var brush = new SolidBrush(colors ? Green : body))
        using (var pen = new Pen(colors ? Green : body, WingRounding) { LineJoin = LineJoin.Round })
        {
            g.FillPath(brush, wing);
            g.DrawPath(pen, wing);
        }
        using (var eye = Parse(EyePath))
        using (var brush = new SolidBrush(body))
            g.FillPath(brush, eye);

        g.Restore(state);
    }

    /// <summary>
    /// Place le logo au centre d'un carré de <paramref name="size"/> px, haut
    /// de <paramref name="height"/> px trait compris, avec un trait de
    /// <paramref name="strokePx"/> px. Rend le cadre à passer à
    /// <see cref="Draw"/> et l'épaisseur du trait dans la boîte de 560.
    /// </summary>
    public static (RectangleF Bounds, float Stroke) Fit(float size, float height, float strokePx)
    {
        // Hors trait, la silhouette va de y = 24 (haut de la tête) à 527
        // (bas de la coque) ; le trait déborde de sa demi-épaisseur.
        const float span = 527f - 24f;
        float stroke = span * strokePx / (height - strokePx);
        float scale = height / (span + stroke);
        float side = Box * scale;
        float top = (24f - stroke / 2) * scale;
        return (new RectangleF((size - side) / 2, (size - height) / 2 - top, side, side), stroke);
    }

    private static Pen RoundPen(Color color, float width) => new(color, width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    /// <summary>Sous-ensemble de la syntaxe SVG, suffisant ici : M, L, C et Z
    /// en coordonnées absolues.</summary>
    private static GraphicsPath Parse(string data)
    {
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

    // internal : l'interface (MainWindow) redessine le même logo en WPF.
    // Géométrie (grille de 512, décalée de 24) : tête = demi-cercle r131,
    // coque = demi-cercle r206, cran du dos arrondi r24, bec = quart de
    // disque r104, aile = demi-lune r110 concentrique à la coque, œil = cercle r30.

    /// <summary>Silhouette, tracée (non remplie).</summary>
    internal const string OutlinePath =
        "M74 155C74 82.7 132.7 24 205 24C277.3 24 336 82.7 336 155L336 253C336 266.3 346.7 277 360 277L462 277C475.3 277 486 287.7 486 301L486 321C486 434.8 393.8 527 280 527C166.2 527 74 434.8 74 321Z";

    /// <summary>Aile : demi-lune pleine (r110, concentrique à la coque),
    /// remplie et bordée d'un trait de <see cref="WingRounding"/>.</summary>
    internal const string WingPath =
        "M175.1 354L384.9 354C370.5 399.8 328 431 280 431C232 431 189.5 399.8 175.1 354Z";

    /// <summary>Bec : tracé au repos, rempli et tracé quand il est en couleur.</summary>
    internal const string BeakPath =
        "M331 119L440 119C440 176.4 393.4 223 336 223Z";

    /// <summary>Œil agrandi (r44), évidé dans le logo plein de l'app.</summary>
    internal const string EyeLargePath =
        "M289 163C289 187.3 269.3 207 245 207C220.7 207 201 187.3 201 163C201 138.7 220.7 119 245 119C269.3 119 289 138.7 289 163Z";

    /// <summary>Aile agrandie (demi-lune r150) pour le logo plein de l'app.</summary>
    internal const string WingLargePath =
        "M131.2 340L428.8 340C419.2 414.9 355.5 471 280 471C204.5 471 140.8 414.9 131.2 340Z";

    /// <summary>Œil : rempli.</summary>
    internal const string EyePath =
        "M275 163C275 179.6 261.6 193 245 193C228.4 193 215 179.6 215 163C215 146.4 228.4 133 245 133C261.6 133 275 146.4 275 163Z";
}
