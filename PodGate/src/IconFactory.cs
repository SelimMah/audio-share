using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PodGate;

enum TrayState { NoDevice, Locked, Present, Busy, Connected }

/// <summary>Icônes dessinées à la volée (pas de fichier .ico à fournir).</summary>
static class IconFactory
{
    static readonly Dictionary<TrayState, Icon> Cache = new();

    public static void ClearCache() => Cache.Clear();

    public static Icon Tray(TrayState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;

        bool lightTaskbar = Theme.TaskbarLight;
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var fg = lightTaskbar ? Color.FromArgb(255, 32, 32, 32) : Color.White;
            if (state is TrayState.Locked or TrayState.NoDevice) fg = Color.FromArgb(150, fg);
            DrawBuds(g, new RectangleF(1, 1, 30, 30), fg);

            Color? dot = state switch
            {
                TrayState.Connected => Theme.Hex(0x2EB85C),
                TrayState.Present => Theme.Accent(darkBackground: !lightTaskbar),
                TrayState.Busy => Theme.Hex(0xF2A900),
                _ => null,
            };
            if (dot is Color d)
            {
                var ring = lightTaskbar ? Theme.Hex(0xEEEEEE) : Theme.Hex(0x202020);
                using (var ringBrush = new SolidBrush(ring)) g.FillEllipse(ringBrush, 17, 17, 15, 15);
                using (var dotBrush = new SolidBrush(d)) g.FillEllipse(dotBrush, 19, 19, 11, 11);
            }
        }

        var icon = Icon.FromHandle(bmp.GetHicon());
        Cache[state] = icon;
        return icon;
    }

    /// <summary>Deux écouteurs stylisés (tête ronde + tige), centrés dans le rectangle.</summary>
    public static void DrawBuds(Graphics g, RectangleF area, Color color)
    {
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = Math.Min(area.Width, area.Height);
        float ox = area.X + (area.Width - s) / 2f;
        float oy = area.Y + (area.Height - s) / 2f;
        float head = s * 0.38f;
        float stem = s * 0.15f;
        float top = oy + s * 0.04f;
        float bottom = oy + s * 0.96f - stem / 2f;

        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, stem) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Écouteur gauche : tige sur le bord droit de la tête.
        float lx = ox + s * 0.04f;
        g.FillEllipse(brush, lx, top, head, head);
        float lStem = lx + head - stem / 2f;
        g.DrawLine(pen, lStem, top + head * 0.55f, lStem, bottom);

        // Écouteur droit : symétrique.
        float rx = ox + s * 0.96f - head;
        g.FillEllipse(brush, rx, top, head, head);
        float rStem = rx + stem / 2f;
        g.DrawLine(pen, rStem, top + head * 0.55f, rStem, bottom);

        g.SmoothingMode = oldMode;
    }
}
