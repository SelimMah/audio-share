using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AudioShare;

/// <summary>
/// Icône de la zone de notification, avec son menu.
/// L'icône est dessinée à l'exécution : pas de fichier .ico à embarquer,
/// et elle prend ses couleurs quand un autre appareil est relié.
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
            Text = Dev ? "Duckast (dev)" : "Duckast",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowRequested?.Invoke();
        };
        ApplyLanguage();
        SetConnected(false);
    }

    /// <summary>Textes du menu dans la langue courante (appelé au changement).</summary>
    public void ApplyLanguage()
    {
        _show.Text = Loc.T("Show Duckast", "Afficher Duckast");
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

    private (bool Connected, bool Light)? _drawn;

    /// <summary>Logo en couleurs quand un autre appareil est relié, d'une
    /// seule teinte sinon.</summary>
    public void SetConnected(bool connected)
    {
        // Appelé à chaque rafraîchissement d'état : ne redessiner que si
        // l'icône change vraiment (le thème de la barre peut aussi basculer).
        var wanted = (connected, TaskbarIsLight());
        if (_drawn == wanted) return;
        _drawn = wanted;

        var previous = _current;
        _current = Draw(connected, wanted.Item2);
        _icon.Icon = _current;
        previous?.Dispose();
    }

    /// <summary>Barre des tâches claire : un trait blanc y serait invisible.</summary>
    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    public void Notify(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(3000);
    }

    /// <summary>Copie de développement : pastille orange, pour ne jamais la
    /// confondre avec l'app installée.</summary>
    private static readonly bool Dev = !Updater.IsInstalledCopy;

    /// <summary>
    /// Dessine l'icône en 64×64 (Windows la réduit proprement) : le logo
    /// tout blanc (noir sur une barre des tâches claire), avec son bec orange
    /// quand un appareil est relié.
    /// </summary>
    private static Icon Draw(bool connected, bool lightTaskbar)
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            DuckLogo.Draw(g, new RectangleF(0, 0, 64, 64),
                lightTaskbar ? Color.Black : Color.White, connected);

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
