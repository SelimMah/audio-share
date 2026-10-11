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
    private readonly ToolStripMenuItem _left, _stereo, _right, _show, _quit;
    private Icon? _current;

    public event Action? ShowRequested;
    public event Action? QuitRequested;
    public event Action<float, float>? BalanceRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        _show = new ToolStripMenuItem("", null, (_, _) => ShowRequested?.Invoke())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        menu.Items.Add(_show);
        menu.Items.Add(new ToolStripSeparator());

        _left = new ToolStripMenuItem("", null, (_, _) => Choose(1f, 0f));
        _stereo = new ToolStripMenuItem("", null, (_, _) => Choose(1f, 1f)) { Checked = true };
        _right = new ToolStripMenuItem("", null, (_, _) => Choose(0f, 1f));
        menu.Items.Add(_left);
        menu.Items.Add(_stereo);
        menu.Items.Add(_right);
        menu.Items.Add(new ToolStripSeparator());
        _quit = new ToolStripMenuItem("", null, (_, _) => QuitRequested?.Invoke());
        menu.Items.Add(_quit);

        _icon = new NotifyIcon
        {
            Text = Dev ? "Audio Share (dev)" : "Audio Share",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowRequested?.Invoke();
        };
        ApplyLanguage();
        SetActive(false);
    }

    /// <summary>Textes du menu dans la langue courante (appelé au changement).</summary>
    public void ApplyLanguage()
    {
        _show.Text = Loc.T("Show Audio Share", "Afficher Audio Share");
        _left.Text = Loc.T("Left", "Gauche");
        _stereo.Text = Loc.T("Stereo", "Stéréo");
        _right.Text = Loc.T("Right", "Droite");
        _quit.Text = Loc.T("Quit", "Quitter");
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

    /// <summary>Copie de développement : icône orange avec pastille, pour ne
    /// jamais la confondre avec l'app installée (bleue).</summary>
    private static readonly bool Dev = !Updater.IsInstalledCopy;

    /// <summary>
    /// Dessine l'icône en 64×64 (Windows la réduit proprement) : ondes sonores
    /// fluides et concentriques, émanant d'une source centrale. Bleu/violet pour
    /// l'app installée, orange/rose pour la copie de développement.
    /// </summary>
    private static Icon Draw(bool active)
    {
        Color c1, c2;
        if (active)
        {
            c1 = Dev ? Color.FromArgb(255, 159, 10) : Color.FromArgb(10, 132, 255);
            c2 = Dev ? Color.FromArgb(255, 55, 95) : Color.FromArgb(191, 90, 242);
        }
        else
        {
            c1 = Color.FromArgb(128, 128, 134);
            c2 = Color.FromArgb(70, 70, 74);
        }

        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var rect = new Rectangle(2, 2, 60, 60);
            using var bg = new LinearGradientBrush(rect, c1, c2, 55f);
            g.FillEllipse(bg, rect);

            var cx = 32f;
            var cy = 32f;

            // Trois ondes concentriques émanant du centre
            var waveWidths = new[] { 2.4f, 1.8f, 1.2f };
            var waveRadii = new[] { 7f, 14f, 21f };

            for (int i = 0; i < 3; i++)
            {
                using var wave = new Pen(Color.White, waveWidths[i])
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };

                var radius = waveRadii[i];
                // Arc de 220° pour un effet de flux directionnel (légèrement vers le haut/droite)
                g.DrawArc(wave, cx - radius, cy - radius, radius * 2, radius * 2, -20, 220);
            }

            // Point central blanc pour l'origine des ondes
            g.FillEllipse(Brushes.White, cx - 3.5f, cy - 3.5f, 7f, 7f);

            // Pastille « dev » en bas à droite, lisible même en 16×16
            if (Dev)
            {
                using var badge = new SolidBrush(Color.FromArgb(255, 159, 10));
                g.FillEllipse(badge, 42f, 42f, 19f, 19f);
                using var ring = new Pen(Color.White, 3f);
                g.DrawEllipse(ring, 42f, 42f, 19f, 19f);
            }
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
