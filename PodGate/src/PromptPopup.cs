using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PodGate;

/// <summary>
/// Petite fenêtre en bas à droite, façon notification Windows 11.
/// Elle ne prend jamais le focus : elle n'interrompt pas la saisie en cours.
/// </summary>
sealed class PromptPopup : Form
{
    public event Action? ConnectClicked;
    public event Action? Dismissed;

    enum PopupMode { Prompt, Status }

    const int HitNone = 0, HitPrimary = 1, HitSecondary = 2, HitClose = 3;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWCP_ROUND = 2;

    PopupMode _mode = PopupMode.Status;
    string _title = "", _line1 = "", _line2 = "";
    bool _spinner;
    int _spinAngle;
    int _hover = HitNone;
    float _scale = 1f;
    bool _dwmRounded;
    Rectangle _btnPrimary, _btnSecondary, _btnClose;
    Palette _pal = Palette.Current();

    readonly Font _titleFont = new("Segoe UI Semibold", 10.5f);
    readonly Font _textFont = new("Segoe UI", 9f);
    readonly System.Windows.Forms.Timer _closeTimer = new();
    readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 15 };
    readonly System.Windows.Forms.Timer _spinTimer = new() { Interval = 50 };

    public PromptPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        DoubleBuffered = true;
        Text = "PodGate";

        _closeTimer.Tick += (_, _) => OnCloseTimer();
        _fadeTimer.Tick += (_, _) =>
        {
            Opacity = Math.Min(1.0, Opacity + 0.12);
            if (Opacity >= 1.0) _fadeTimer.Stop();
        };
        _spinTimer.Tick += (_, _) => { _spinAngle = (_spinAngle + 20) % 360; Invalidate(); };

        _ = Handle; // crée la fenêtre tout de suite pour connaître le DPI
    }

    // ------------------------------------------------------------------ API

    public bool IsShowingPrompt() => Visible && _mode == PopupMode.Prompt;

    /// <summary>Proposition avec boutons « Connecter » / « Plus tard ».</summary>
    public void ShowPrompt(string title, string line1, string line2, int timeoutMs)
    {
        _mode = PopupMode.Prompt;
        _title = title; _line1 = line1; _line2 = line2;
        _spinner = false;
        ShowCore(timeoutMs);
    }

    /// <summary>Message d'état (avec ou sans indicateur de progression).</summary>
    public void ShowStatus(string title, string line1, bool spinner, int autoCloseMs)
    {
        _mode = PopupMode.Status;
        _title = title; _line1 = line1; _line2 = "";
        _spinner = spinner;
        ShowCore(autoCloseMs);
    }

    public void HidePopup() => HideCore(dismissed: false);

    // ------------------------------------------------------------ Fenêtre

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int pref = DWMWCP_ROUND;
            _dwmRounded = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0;
            ApplyDwmBorder();
        }
        catch { _dwmRounded = false; } // Windows 10 : coins carrés, bordure dessinée à la main
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 & co. : on masque au lieu de détruire.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideCore(dismissed: _mode == PopupMode.Prompt);
        }
        base.OnFormClosing(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
        Invalidate();
    }

    void ApplyDwmBorder()
    {
        if (!_dwmRounded) return;
        int colorRef = _pal.Border.R | (_pal.Border.G << 8) | (_pal.Border.B << 16);
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    void ShowCore(int closeAfterMs)
    {
        _pal = Palette.Current();
        ApplyDwmBorder();
        Relayout();

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - S(12), area.Bottom - Height - S(12));

        _hover = HitNone;
        Cursor = Cursors.Default;
        if (_spinner) _spinTimer.Start(); else _spinTimer.Stop();

        _closeTimer.Stop();
        if (closeAfterMs > 0)
        {
            _closeTimer.Interval = closeAfterMs;
            _closeTimer.Start();
        }

        if (!Visible)
        {
            Opacity = 0;
            Show();
            _fadeTimer.Start();
        }
        Invalidate();
    }

    void HideCore(bool dismissed)
    {
        _closeTimer.Stop();
        _spinTimer.Stop();
        _fadeTimer.Stop();
        bool wasVisible = Visible;
        if (wasVisible) Hide();
        if (dismissed && wasVisible) Dismissed?.Invoke();
    }

    void OnCloseTimer()
    {
        // On ne ferme pas sous le curseur : l'utilisateur est peut-être en train de cliquer.
        if (Bounds.Contains(Cursor.Position)) return;
        HideCore(dismissed: _mode == PopupMode.Prompt);
    }

    int S(float value) => (int)Math.Round(value * _scale);

    void Relayout()
    {
        _scale = DeviceDpi / 96f;
        int w = S(364);
        int h = S(_mode == PopupMode.Prompt ? 138 : 96);
        Size = new Size(w, h);

        if (_mode == PopupMode.Prompt)
        {
            int bw = S(112), bh = S(32), by = h - S(16) - bh;
            _btnPrimary = new Rectangle(w - S(16) - bw, by, bw, bh);
            _btnSecondary = new Rectangle(_btnPrimary.X - S(8) - bw, by, bw, bh);
        }
        else
        {
            _btnPrimary = Rectangle.Empty;
            _btnSecondary = Rectangle.Empty;
        }
        _btnClose = _spinner ? Rectangle.Empty : new Rectangle(w - S(38), S(10), S(28), S(28));
    }

    // ------------------------------------------------------------- Dessin

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_pal.Back);

        if (!_dwmRounded)
            using (var border = new Pen(_pal.Border))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        // Vignette avec les écouteurs
        var tile = new RectangleF(S(16), S(16), S(48), S(48));
        using (var tileBrush = new SolidBrush(_pal.Tile))
        using (var tilePath = RoundedRect(tile, S(10)))
            g.FillPath(tileBrush, tilePath);
        IconFactory.DrawBuds(g, RectangleF.Inflate(tile, -S(11), -S(11)), _pal.Text);

        // Textes
        int tx = S(76);
        int right = _spinner ? Width - S(56) : (_btnClose.IsEmpty ? Width - S(16) : _btnClose.Left - S(4));
        const TextFormatFlags singleLine = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                           TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                                           TextFormatFlags.SingleLine;
        const TextFormatFlags wrapped = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(tx, S(14), right - tx, S(24)), _pal.Text, singleLine);
        if (_mode == PopupMode.Prompt)
        {
            TextRenderer.DrawText(g, _line1, _textFont, new Rectangle(tx, S(38), right - tx, S(20)), _pal.SubText, singleLine);
            if (_line2.Length > 0)
                TextRenderer.DrawText(g, _line2, _textFont, new Rectangle(tx, S(58), Width - tx - S(16), S(20)), _pal.SubText, singleLine);

            DrawButton(g, _btnPrimary, "Connecter",
                _hover == HitPrimary ? _pal.AccentHover : _pal.Accent, null, _pal.OnAccent);
            DrawButton(g, _btnSecondary, "Plus tard",
                _hover == HitSecondary ? _pal.NeutralHover : _pal.Neutral, _pal.NeutralBorder, _pal.Text);
        }
        else
        {
            TextRenderer.DrawText(g, _line1, _textFont, new Rectangle(tx, S(40), right - tx, S(40)), _pal.SubText, wrapped);
        }

        // Bouton fermer
        if (!_btnClose.IsEmpty)
        {
            if (_hover == HitClose)
                using (var hoverBrush = new SolidBrush(_pal.NeutralHover))
                using (var hoverPath = RoundedRect(_btnClose, S(4)))
                    g.FillPath(hoverBrush, hoverPath);
            using var xPen = new Pen(_pal.SubText, Math.Max(1f, 1.2f * _scale));
            float cx = _btnClose.X + _btnClose.Width / 2f, cy = _btnClose.Y + _btnClose.Height / 2f, r = 4.5f * _scale;
            g.DrawLine(xPen, cx - r, cy - r, cx + r, cy + r);
            g.DrawLine(xPen, cx - r, cy + r, cx + r, cy - r);
        }

        // Indicateur de progression
        if (_spinner)
        {
            var arc = new Rectangle(Width - S(42), Height / 2 - S(10), S(20), S(20));
            using var arcPen = new Pen(_pal.Accent, 2.5f * _scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arcPen, arc, _spinAngle, 270);
        }
    }

    void DrawButton(Graphics g, Rectangle r, string text, Color fill, Color? border, Color fore)
    {
        using var path = RoundedRect(r, S(5));
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (border is Color bc)
            using (var pen = new Pen(bc)) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, text, _textFont, r, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // -------------------------------------------------------------- Souris

    int HitTest(Point p) =>
        _btnPrimary.Contains(p) ? HitPrimary :
        _btnSecondary.Contains(p) ? HitSecondary :
        _btnClose.Contains(p) ? HitClose : HitNone;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit == HitNone ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == HitNone) return;
        _hover = HitNone;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        switch (HitTest(e.Location))
        {
            case HitPrimary:
                ConnectClicked?.Invoke();
                break;
            case HitSecondary:
                HideCore(dismissed: true);
                break;
            case HitClose:
                HideCore(dismissed: _mode == PopupMode.Prompt);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer.Dispose();
            _fadeTimer.Dispose();
            _spinTimer.Dispose();
            _titleFont.Dispose();
            _textFont.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
