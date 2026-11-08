using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace AudioShare;

/// <summary>
/// Notification AirPods, au style du panneau Duckast : même bloc que la
/// carte de la page principale (AirPodsView) sur fond acrylique. Apparaît
/// comme le flyout (glissement vers le haut + fondu) et repart en fondu. Ne
/// prend jamais le focus ; un clic n'importe où en dehors la ferme (crochet
/// souris bas niveau, posé seulement pendant l'affichage).
/// </summary>
internal sealed partial class AirPodsPopup : Window
{
    /// <summary>« Connecter ».</summary>
    public event Action? ConnectClicked;
    /// <summary>Bouton d'action en mode « état » (double-clic sur l'icône) : connecter ou déconnecter.</summary>
    public event Action? LiveActionClicked;
    /// <summary>Clic à l'extérieur ou expiration d'une proposition (hors aperçu).</summary>
    public event Action? Dismissed;

    /// <summary>Fenêtre au-dessus de laquelle se placer quand elle est ouverte (le flyout).</summary>
    public Window? Anchor { get; set; }

    public bool IsShowingPrompt => _shown && _isPrompt;

    /// <summary>Affiche l'état courant (ouverte par double-clic) : à rafraîchir à chaque changement.</summary>
    public bool IsShowingLive => _shown && _isLive;

    private readonly DispatcherTimer _autoClose = new();
    private bool _shown;        // affichée (ou en train d'apparaître), pas en train de partir
    private bool _isPrompt;
    private bool _isPreview;
    private bool _isLive;       // état courant, rempli par l'appelant    // aperçu depuis les Réglages : un refus ne compte pas
    private bool _allowClose;

    public AirPodsPopup()
    {
        InitializeComponent();
        View.ActionClicked += () =>
        {
            if (_isLive) LiveActionClicked?.Invoke();
            else ConnectClicked?.Invoke();
        };

        _autoClose.Tick += (_, _) =>
        {
            // On ne ferme pas sous le curseur : l'utilisateur est peut-être en train de cliquer.
            if (IsMouseOver) return;
            HideCore(dismissed: _isPrompt);
        };

        // Acrylique permanent (la fenêtre n'est jamais active), coins arrondis,
        // fenêtre outil non activable.
        SourceInitialized += (_, _) =>
        {
            WindowEffects.ApplyPopup(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TouchInputHook);
        };
        // Toucher sur la popup elle-même (voir TouchInputHook).
        PreviewStylusDown += (_, _) => { _touchInside = true; _lastInsideTouch = DateTime.UtcNow; };
        PreviewStylusUp += (_, _) => _touchInside = false;
        LostStylusCapture += (_, _) => _touchInside = false;
        // La hauteur suit le contenu (proposition / message) : rester ancrée en bas.
        SizeChanged += (_, _) => { if (_shown) Place(); };
        // Alt+F4 & co. : on masque au lieu de détruire.
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            HideCore(dismissed: _isPrompt);
        };
    }

    // ================================================================== API

    /// <param name="preview">Aperçu (bouton des Réglages) : la fermeture ne compte pas comme un refus.</param>
    public void ShowPrompt(AirPodsPrompt prompt, bool preview = false)
    {
        _isPrompt = true;
        _isLive = false;
        _isPreview = preview;
        View.Title = prompt.Title;
        View.SetAction(Loc.T("Connect", "Connecter"));
        View.SetStatus(null, null);
        View.SetBattery(prompt.Battery);
        View.SetLed(AirPodsView.Led.White, TimeSpan.FromMilliseconds(900));
        ShowCore(prompt.TimeoutMs);
    }

    public void ShowMessage(AirPodsMessage message)
    {
        _isPrompt = false;
        _isPreview = false;
        _isLive = false;
        View.Title = message.Title;
        View.SetAction(null);
        View.SetStatus(message.Kind, message.Text);
        switch (message.Kind)
        {
            case AirPodsMessageKind.Progress: View.SetLed(AirPodsView.Led.White, TimeSpan.FromMilliseconds(380)); break;
            case AirPodsMessageKind.Success: View.SetLed(AirPodsView.Led.Green); break;
            case AirPodsMessageKind.Error: View.SetLed(AirPodsView.Led.Amber); break;
            default: View.SetLed(AirPodsView.Led.White); break;
        }
        ShowCore(message.AutoCloseMs);
    }

    /// <summary>
    /// État courant des AirPods, rempli par <paramref name="fill"/> (le même
    /// que la carte de la page principale). Reste ouverte jusqu'à un clic à
    /// l'extérieur ; jamais compté comme un refus.
    /// </summary>
    public void ShowLive(Action<AirPodsView> fill, int autoCloseMs = 0)
    {
        _isPrompt = false;
        _isPreview = false;
        _isLive = true;
        fill(View);
        ShowCore(autoCloseMs);
    }

    /// <summary>Batterie mise à jour en direct pendant une proposition.</summary>
    public void UpdateBattery(AirPodsBattery? battery)
    {
        if (_shown && _isPrompt && battery != null) View.SetBattery(battery);
    }

    /// <summary>Masque sans compter comme un refus (la proposition n'a plus lieu d'être).</summary>
    public void HideNow() => HideCore(dismissed: false);

    /// <summary>Ferme pour de bon (fermeture de l'app).</summary>
    public void Destroy()
    {
        _autoClose.Stop();
        RemoveMouseHook();
        RemoveTouchWatch();
        _allowClose = true;
        Close();
    }

    // ============================================================ Affichage

    private void ShowCore(int closeAfterMs)
    {
        _autoClose.Stop();
        if (closeAfterMs > 0)
        {
            _autoClose.Interval = TimeSpan.FromMilliseconds(closeAfterMs);
            _autoClose.Start();
        }

        if (_shown)
        {
            UpdateLayout();
            Place();
            return;
        }

        _shown = true;
        WindowEffects.ApplyVeil(this); // suit le réglage « mode sombre »
        if (!IsVisible) Show();
        UpdateLayout();
        Place();
        AnimateIn();
        View.PlayIntro();
        InstallMouseHook();
        InstallTouchWatch();
    }

    private void HideCore(bool dismissed)
    {
        if (!_shown) return;
        _shown = false;
        _isLive = false;
        bool countsAsRefusal = dismissed && _isPrompt && !_isPreview;
        _isPrompt = false;
        _autoClose.Stop();
        RemoveMouseHook();
        RemoveTouchWatch();
        AnimateOut();
        if (countsAsRefusal) Dismissed?.Invoke();
    }

    /// <summary>En bas à droite (comme le flyout), ou juste au-dessus du flyout s'il est ouvert.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        double bottom = area.Bottom - 12;
        if (Anchor is { IsVisible: true } anchor && anchor.ActualHeight > 1)
            bottom = Math.Min(bottom, anchor.Top - 10);
        Top = Math.Max(area.Top + 12, bottom - ActualHeight);
    }

    /// <summary>Même entrée que le flyout : glissement vers le haut + fondu.</summary>
    private void AnimateIn()
    {
        RootSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>Le contenu retombe en fondu, puis la fenêtre est masquée.</summary>
    private void AnimateOut()
    {
        RootSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (_shown) return; // réaffichée pendant le départ
            Hide();
            View.StopLoops();
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    // ====================================================== Clic à l'extérieur

    // La fenêtre n'est jamais activée : pas d'événement « Deactivated » sur
    // lequel s'appuyer. Un crochet souris bas niveau voit tous les clics du
    // système ; il n'est posé que pendant l'affichage et ne fait qu'un test
    // de rectangle (le travail est renvoyé au Dispatcher).

    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMButtonDown = 0x0207, WmXButtonDown = 0x020B;

    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseProc; // gardé en champ : sinon collecté par le GC

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Write($"AirPods : crochet souris impossible (erreur {Marshal.GetLastWin32Error()})");
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = (int)wParam;
            if (nCode >= 0 && _shown &&
                msg is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!WindowContains(info.pt.X, info.pt.Y))
                    Dispatcher.BeginInvoke(() => HideCore(dismissed: _isPrompt));
            }
        }
        catch { /* un crochet ne doit jamais lever */ }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>Le point (pixels écran) est-il sur la popup ?</summary>
    private bool WindowContains(int x, int y)
    {
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        return x >= topLeft.X && x <= bottomRight.X && y >= topLeft.Y && y <= bottomRight.Y;
    }

    // ==================================================== Toucher à l'extérieur

    // Le tactile ne passe pas par le crochet souris : une fenêtre qui gère
    // les pointeurs (WPF, navigateur, Explorateur…) ne reçoit jamais de
    // clic souris simulé. On écoute donc l'écran tactile lui-même (Raw
    // Input, même en arrière-plan) : à chaque nouveau contact, si la popup
    // n'a pas été touchée dans la foulée, c'est que le doigt était ailleurs.
    // On ne lit pas les rapports (coordonnées propres à chaque écran) :
    // seul leur rythme compte — un écart marque un nouveau contact.

    private const int WmInput = 0x00FF;
    private const uint RidevInputSink = 0x00000100, RidevRemove = 0x00000001;
    private const ushort HidUsagePageDigitizer = 0x0D, HidUsageTouchScreen = 0x04, HidUsagePen = 0x02;

    private bool _touchWatch;
    private bool _touchInside;
    private DateTime _lastInsideTouch, _lastTouchReport, _shownAt;

    private void InstallTouchWatch()
    {
        _shownAt = _lastTouchReport = DateTime.UtcNow; // contact en cours (le tap qui a ouvert) : pas nouveau
        if (_touchWatch) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        _touchWatch = RegisterDigitizers(RidevInputSink, hwnd);
        if (!_touchWatch)
            Log.Write($"AirPods : écoute tactile impossible (erreur {Marshal.GetLastWin32Error()})");
    }

    private void RemoveTouchWatch()
    {
        if (!_touchWatch) return;
        RegisterDigitizers(RidevRemove, IntPtr.Zero);
        _touchWatch = false;
        _touchInside = false;
    }

    private static bool RegisterDigitizers(uint flags, IntPtr hwnd)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = HidUsagePageDigitizer, usUsage = HidUsageTouchScreen, dwFlags = flags, hwndTarget = hwnd },
            new RAWINPUTDEVICE { usUsagePage = HidUsagePageDigitizer, usUsage = HidUsagePen, dwFlags = flags, hwndTarget = hwnd },
        };
        return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private IntPtr TouchInputHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Pas « handled » : DefWindowProc doit libérer l'entrée brute.
        if (msg != WmInput || !_shown || !_touchWatch) return IntPtr.Zero;

        var now = DateTime.UtcNow;
        bool newContact = (now - _lastTouchReport).TotalMilliseconds > 120;
        _lastTouchReport = now;
        if (!newContact || (now - _shownAt).TotalMilliseconds < 300) return IntPtr.Zero;

        // Laisse à WPF le temps de livrer le toucher à la popup, s'il est dessus.
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (!_shown || _touchInside || _lastInsideTouch >= now.AddMilliseconds(-60)) return;
            HideCore(dismissed: _isPrompt);
        };
        check.Start();
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
