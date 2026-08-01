using System.Runtime.InteropServices;

namespace AudioShare;

/// <summary>
/// Intercepte les touches volume (clavier, molette de casque…) AVANT Windows
/// pendant l'émission : le périphérique reste muet à 0 % en permanence — plus
/// aucun démutage, donc plus aucune fuite possible — et c'est l'app qui
/// applique la commande au volume émis. Hook clavier bas niveau : à installer
/// sur un thread avec une boucle de messages (le thread UI), les rappels y
/// arrivent aussi.
/// </summary>
internal sealed class VolumeKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Référence gardée : sans elle le GC ramasse le délégué passé au hook
    // natif et le processus plante à la frappe suivante.
    private readonly HookProc _proc;
    private IntPtr _hook;

    /// <summary>+1 / −1 par appui (répétitions de maintien incluses).</summary>
    public event Action<int>? VolumeStep;
    public event Action? MuteToggle;

    public VolumeKeyHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // Premier champ de KBDLLHOOKSTRUCT : le code de touche virtuel.
            int vk = Marshal.ReadInt32(lParam);
            if (vk is VkVolumeMute or VkVolumeDown or VkVolumeUp)
            {
                long msg = wParam;
                if (msg is WmKeyDown or WmSysKeyDown)
                {
                    switch (vk)
                    {
                        case VkVolumeUp: VolumeStep?.Invoke(+1); break;
                        case VkVolumeDown: VolumeStep?.Invoke(-1); break;
                        case VkVolumeMute: MuteToggle?.Invoke(); break;
                    }
                }
                // Touche consommée (appui ET relâchement) : Windows ne la
                // voit pas, son OSD ne s'affiche pas, rien ne se démute.
                return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
