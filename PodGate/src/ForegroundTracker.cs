using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PodGate;

/// <summary>
/// Retient la dernière fois où une interface Bluetooth de Windows (Paramètres, Paramètres rapides,
/// assistant d'appairage) était au premier plan. Sert à distinguer « je clique sur Connecter dans
/// Windows » d'une reconnexion automatique faite dans mon dos.
/// </summary>
static class ForegroundTracker
{
    static readonly HashSet<string> BluetoothUi = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemSettings",       // application Paramètres
        "ShellExperienceHost",  // Paramètres rapides (Win + A), versions 22H2/23H2
        "ShellHost",            // Paramètres rapides, versions 24H2 et plus
        "DevicePairingWizard",  // assistant « Ajouter un appareil »
    };

    static readonly Dictionary<uint, string> NameCache = new();
    static string _lastName = "";

    public static DateTime LastBluetoothUi { get; private set; } = DateTime.MinValue;

    public static bool BluetoothUiActiveWithin(TimeSpan window) => DateTime.Now - LastBluetoothUi <= window;

    public static void Sample()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            string name = ProcessNameOf(hwnd);
            // Les applis UWP (dont Paramètres) sont hébergées dans un cadre ApplicationFrameHost.
            if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                name = HostedAppName(hwnd) ?? name;

            if (BluetoothUi.Contains(name))
            {
                if (!name.Equals(_lastName, StringComparison.OrdinalIgnoreCase))
                    Log.Info($"Interface Windows au premier plan : {name}");
                LastBluetoothUi = DateTime.Now;
            }
            _lastName = name;
        }
        catch { /* échantillonnage best-effort */ }
    }

    static string ProcessNameOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return NameOfPid(pid);
    }

    static string NameOfPid(uint pid)
    {
        if (pid == 0) return "";
        if (NameCache.TryGetValue(pid, out var cached)) return cached;
        string name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch { name = ""; }
        if (NameCache.Count > 256) NameCache.Clear();
        NameCache[pid] = name;
        return name;
    }

    static string? HostedAppName(IntPtr frame)
    {
        GetWindowThreadProcessId(frame, out uint framePid);
        string? found = null;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint pid);
            if (pid != 0 && pid != framePid)
            {
                found = NameOfPid(pid);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
