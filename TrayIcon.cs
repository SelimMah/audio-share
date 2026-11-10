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
    private readonly ToolStripMenuItem _left, _stereo, _right, _show, _quit, _airPods;
    private readonly ToolStripSeparator _airPodsSeparator;
    private Icon? _current;

    public event Action? ShowRequested;
    /// <summary>Clic gauche sur l'icône (le double-clic arrive ensuite, s'il y en a un).</summary>
    public event Action? IconClicked;
    /// <summary>Double-clic gauche sur l'icône : popup AirPods.</summary>
    public event Action? AirPodsPopupRequested;
    public event Action? QuitRequested;
    public event Action<float, float>? BalanceRequested;
    /// <summary>Entrée « Connecter / Déconnecter les AirPods ».</summary>
    public event Action? AirPodsRequested;

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
        // AirPods : masqué tant que la gestion n'est pas active (voir SetAirPods).
        _airPods = new ToolStripMenuItem("", null, (_, _) => AirPodsRequested?.Invoke()) { Visible = false };
        _airPodsSeparator = new ToolStripSeparator { Visible = false };
        menu.Items.Add(_airPods);
        menu.Items.Add(_airPodsSeparator);
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
            if (e.Button == MouseButtons.Left) IconClicked?.Invoke();
        };
        // Le premier clic d'un double-clic a déjà levé MouseClick ; le second, non.
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) AirPodsPopupRequested?.Invoke();
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

    /// <summary>
    /// Texte de l'entrée AirPods (null = masquée) et si elle est cliquable.
    /// </summary>
    public void SetAirPods(string? text, bool enabled)
    {
        _airPods.Visible = _airPodsSeparator.Visible = text != null;
        _airPods.Text = text ?? "";
        _airPods.Enabled = enabled;
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
    /// Dessine l'icône à la taille exacte de la zone de notification (16 px
    /// à 100 %, 20 à 125 %…) : le canard est au trait, et une image réduite
    /// par Windows le rendrait flou. Le logo est tout blanc (noir sur une
    /// barre des tâches claire), avec son bec orange et son aile verte quand
    /// un appareil est relié.
    /// </summary>
    private static Icon Draw(bool connected, bool lightTaskbar)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // Toute la hauteur de l'icône, et un trait un peu plus épais que
            // celui des icônes système (3 px à 32 px contre 2) : le canard,
            // plus étroit qu'elles, paraîtrait sinon plus petit et plus maigre.
            var (bounds, stroke) = DuckLogo.Fit(size, size, size * 3f / 32f);
            DuckLogo.Draw(g, bounds, lightTaskbar ? Color.Black : Color.White, connected, stroke);

            // Pastille « dev » en bas à droite (cotes pensées pour 64 px)
            if (Dev)
            {
                float k = size / 64f;
                using var badge = new SolidBrush(Color.FromArgb(255, 159, 10));
                g.FillEllipse(badge, 42f * k, 42f * k, 19f * k, 19f * k);
                using var ring = new Pen(Color.White, Math.Max(1f, 3f * k));
                g.DrawEllipse(ring, 42f * k, 42f * k, 19f * k, 19f * k);
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
