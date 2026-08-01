using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AudioShare;

/// <summary>
/// Icône de la zone de notification, avec son menu.
/// L'icône est dessinée à l'exécution : pas de fichier .ico à embarquer,
/// et elle change de couleur selon que la réception est active ou non.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _left, _stereo, _right, _show;
    private Icon? _current;

    public event Action? ShowRequested;
    public event Action? QuitRequested;
    public event Action<float, float>? BalanceRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        _show = new ToolStripMenuItem("Afficher Audio Share", null, (_, _) => ShowRequested?.Invoke())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        menu.Items.Add(_show);
        menu.Items.Add(new ToolStripSeparator());

        _left = new ToolStripMenuItem("Gauche", null, (_, _) => Choose(1f, 0f));
        _stereo = new ToolStripMenuItem("Stéréo", null, (_, _) => Choose(1f, 1f)) { Checked = true };
        _right = new ToolStripMenuItem("Droite", null, (_, _) => Choose(0f, 1f));
        menu.Items.Add(_left);
        menu.Items.Add(_stereo);
        menu.Items.Add(_right);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quitter", null, (_, _) => QuitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Text = "Audio Share",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowRequested?.Invoke();
        };
        SetActive(false);
    }

    private void Choose(float l, float r)
    {
        SyncBalance(l, r);
        BalanceRequested?.Invoke(l, r);
    }

    public void SyncBalance(float l, float r)
    {
        _left.Checked = l == 1f && r == 0f;
        _right.Checked = l == 0f && r == 1f;
        _stereo.Checked = !_left.Checked && !_right.Checked;
    }

    /// <summary>Icône colorée quand la réception tourne, grise sinon.</summary>
    public void SetActive(bool active)
    {
        var previous = _current;
        _current = Draw(active);
        _icon.Icon = _current;
        previous?.Dispose();
    }

    public void Notify(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(3000);
    }

    /// <summary>
    /// Dessine l'icône en 64×64 (Windows la réduit proprement) : une pastille
    /// dégradée avec un téléphone qui émet des ondes — volontairement distincte
    /// du haut-parleur blanc de Windows.
    /// </summary>
    private static Icon Draw(bool active)
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Pastille de fond : bleu -> violet quand actif, gris sinon
            var rect = new Rectangle(2, 2, 60, 60);
            using var bg = new LinearGradientBrush(rect,
                active ? Color.FromArgb(10, 132, 255) : Color.FromArgb(128, 128, 134),
                active ? Color.FromArgb(191, 90, 242) : Color.FromArgb(70, 70, 74),
                55f);
            g.FillEllipse(bg, rect);

            // Téléphone blanc, bords arrondis
            using var phone = RoundedRect(16f, 17f, 15f, 30f, 4.5f);
            g.FillPath(Brushes.White, phone);

            // Écouteur et bouton, dans la couleur du fond pour le relief
            using var detail = new SolidBrush(active ? Color.FromArgb(60, 110, 250) : Color.FromArgb(100, 100, 106));
            g.FillEllipse(detail, 21.5f, 20.5f, 4f, 1.8f);
            g.FillEllipse(detail, 21.7f, 41.5f, 3.6f, 3.6f);

            // Ondes émises vers la droite
            using var wave = new Pen(Color.White, 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(wave, 32f, 25f, 10f, 14f, -52, 104);
            g.DrawArc(wave, 36f, 19f, 19f, 26f, -52, 104);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
