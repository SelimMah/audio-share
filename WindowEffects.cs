using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AudioShare;

/// <summary>
/// Active le fond acrylique et les coins arrondis de Windows 11.
/// C'est le compositeur du système qui floute ce qu'il y a derrière la fenêtre :
/// aucun rendu à notre charge, et l'effet reste net pendant les déplacements.
/// </summary>
internal static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerRound = 2;
    private const int BackdropNone = 1;    // DWMSBT_NONE
    private const int BackdropAcrylic = 3; // DWMSBT_TRANSIENTWINDOW

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private const int WmNcActivate = 0x0086;

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly SolidColorBrush Veil = CreateVeil();

    private static SolidColorBrush CreateVeil()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Fond WPF de la fenêtre, toujours translucide (opaque, il cacherait
    /// l'acrylique). En mode sombre, un voile noir l'assombrit : l'acrylique
    /// seul vire au gris clair devant un fond d'écran lumineux et le texte
    /// blanc s'y perd.
    /// </summary>
    public static void ApplyVeil(Window window) =>
        window.Background = Prefs.DarkMode ? Veil : System.Windows.Media.Brushes.Transparent;

    /// <summary>
    /// Met réellement la fenêtre au premier plan. DWM ne dessine l'acrylique
    /// que sur une fenêtre active (inactive : aplat opaque), et Windows peut
    /// refuser un simple Activate() — on emprunte alors la file d'entrée de
    /// la fenêtre au premier plan, le temps de prendre sa place.
    /// </summary>
    public static void ForceForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        window.Activate();
        var front = GetForegroundWindow();
        if (front == hwnd) return;

        uint frontThread = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, out _);
        uint self = GetCurrentThreadId();
        if (frontThread == 0 || frontThread == self)
        {
            SetForegroundWindow(hwnd);
            return;
        }
        AttachThreadInput(self, frontThread, true);
        SetForegroundWindow(hwnd);
        AttachThreadInput(self, frontThread, false);
    }

    /// <summary>
    /// Rétablit l'acrylique sur une fenêtre déjà affichée. DWM ne le dessine
    /// que sur un cadre qu'il croit actif ; or au tout premier affichage le
    /// WM_NCACTIVATE ne lui parvient pas tel quel (WindowChrome l'intercepte)
    /// et le panneau sortait en aplat gris jusqu'à la réouverture. On lui
    /// annonce donc le cadre actif nous-mêmes, puis on repasse par « aucun
    /// fond » pour le forcer à recomposer. À appeler en différé après Show().
    /// </summary>
    public static void RefreshBackdrop(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        DefWindowProc(hwnd, WmNcActivate, (IntPtr)1, IntPtr.Zero);
        int none = BackdropNone;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
        int acrylic = BackdropAcrylic;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref acrylic, sizeof(int));
    }

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Fenêtre outil : absente de la barre des tâches ET d'Alt+Tab,
        // comme le panneau de volume de Windows.
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow);

        ApplyVeil(window);
        if (HwndSource.FromHwnd(hwnd) is { } source)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        int corner = CornerRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        int backdrop = BackdropAcrylic;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
    }

    /// <summary>
    /// Même habillage que le flyout, plus « non activable » : l'aperçu de
    /// volume ne doit jamais voler le focus à l'application au premier plan.
    /// </summary>
    public static void ApplyOsd(Window window)
    {
        Apply(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate);
    }
}
