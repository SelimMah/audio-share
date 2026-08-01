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
    private const int BackdropAcrylic = 3; // DWMSBT_TRANSIENTWINDOW

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Fenêtre outil : absente de la barre des tâches ET d'Alt+Tab,
        // comme le panneau de volume de Windows.
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow);

        // Sans fond transparent côté WPF, l'acrylique resterait invisible.
        window.Background = System.Windows.Media.Brushes.Transparent;
        if (HwndSource.FromHwnd(hwnd) is { } source)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        int corner = CornerRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        int backdrop = BackdropAcrylic;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
    }
}
