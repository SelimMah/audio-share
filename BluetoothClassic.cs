using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioShare;

/// <summary>Appareil Bluetooth appairé, vu par l'API Win32 classique.</summary>
internal sealed record PairedDevice(ulong Address, string Name, bool Connected, uint ClassOfDevice)
{
    /// <summary>Classe majeure « Audio/Vidéo » du Class of Device (exclut les téléphones).</summary>
    public bool IsAudio => ((ClassOfDevice >> 8) & 0x1F) == 0x04;

    public string AddressText => string.Join(":",
        Enumerable.Range(0, 6).Select(i => ((Address >> (40 - 8 * i)) & 0xFF).ToString("X2")));

    /// <summary>Le style PlainCombo affiche l'élément choisi via ToString() (pas DisplayMemberPath).</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Services audio des écouteurs : les cases de l'onglet « Services » des
/// propriétés de l'appareil. Décochés, Windows ne s'y connecte plus seul.
/// </summary>
internal static class BtServices
{
    public static readonly Guid AudioSink = new("0000110B-0000-1000-8000-00805F9B34FB");     // Récepteur audio (A2DP)
    public static readonly Guid RemoteControl = new("0000110E-0000-1000-8000-00805F9B34FB"); // Contrôle à distance (AVRCP)
    public static readonly Guid Handsfree = new("0000111E-0000-1000-8000-00805F9B34FB");     // Téléphonie mains libres (HFP)

    public static readonly Guid[] Managed = { AudioSink, RemoteControl, Handsfree };

    public static string Name(Guid service) =>
        service == AudioSink ? "Récepteur audio" :
        service == RemoteControl ? "Contrôle à distance" :
        service == Handsfree ? "Téléphonie mains libres" : service.ToString();
}

/// <summary>Accès à l'API Win32 Bluetooth classique (BluetoothApis.dll).</summary>
internal static class BluetoothClassic
{
    private const uint ErrorMoreData = 234;

    public static List<PairedDevice> GetPairedDevices()
    {
        var result = new List<PairedDevice>();
        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnConnected = true,
            fReturnUnknown = false,
            fIssueInquiry = false,   // pas de scan radio : la liste des appareils connus suffit
            cTimeoutMultiplier = 0,
            hRadio = IntPtr.Zero,    // toutes les radios
        };
        var info = NewInfo(0);
        IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
        if (find == IntPtr.Zero) return result;
        try
        {
            do
            {
                if ((info.fRemembered || info.fAuthenticated) && result.All(d => d.Address != info.Address))
                    result.Add(ToDevice(info));
                info = NewInfo(0);
            }
            while (BluetoothFindNextDevice(find, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(find);
        }
        return result;
    }

    public static PairedDevice? GetDevice(ulong address) => WithRadio<PairedDevice?>(radio =>
    {
        var info = NewInfo(address);
        return BluetoothGetDeviceInfo(radio, ref info) == 0 ? ToDevice(info) : null;
    });

    /// <summary>Services cochés pour l'appareil, ou null si Windows ne répond pas.</summary>
    public static HashSet<Guid>? GetEnabledServices(ulong address) => WithRadio<HashSet<Guid>?>(radio =>
    {
        var info = NewInfo(address);
        if (BluetoothGetDeviceInfo(radio, ref info) != 0) return null;

        uint count = 32;
        var services = new Guid[count];
        uint err = BluetoothEnumerateInstalledServices(radio, ref info, ref count, services);
        if (err == ErrorMoreData)
        {
            services = new Guid[count];
            err = BluetoothEnumerateInstalledServices(radio, ref info, ref count, services);
        }
        if (err != 0) return null;
        return services.Take((int)Math.Min(count, (uint)services.Length)).ToHashSet();
    });

    /// <summary>Coche ou décoche un service. Renvoie un code d'erreur Win32 (0 = succès).</summary>
    public static uint SetServiceState(ulong address, Guid service, bool enable) => WithRadio(radio =>
    {
        var info = NewInfo(address);
        uint err = BluetoothGetDeviceInfo(radio, ref info);
        if (err != 0) return err;
        var guid = service;
        return BluetoothSetServiceState(radio, ref info, ref guid, enable ? 1u : 0u);
    });

    private static T WithRadio<T>(Func<IntPtr, T> action)
    {
        var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr radio);
        if (find == IntPtr.Zero) radio = IntPtr.Zero;
        try
        {
            return action(radio);
        }
        finally
        {
            if (radio != IntPtr.Zero) CloseHandle(radio);
            if (find != IntPtr.Zero) BluetoothFindRadioClose(find);
        }
    }

    private static BLUETOOTH_DEVICE_INFO NewInfo(ulong address) => new()
    {
        dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
        Address = address,
        szName = "",
    };

    private static PairedDevice ToDevice(BLUETOOTH_DEVICE_INFO i) => new(
        i.Address,
        string.IsNullOrWhiteSpace(i.szName) ? Loc.T("Bluetooth device", "Appareil Bluetooth") : i.szName,
        i.fConnected,
        i.ulClassofDevice);

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    private const string BtDll = "BluetoothApis.dll";

    [DllImport(BtDll, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport(BtDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport(BtDll, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport(BtDll, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport(BtDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport(BtDll, CharSet = CharSet.Unicode)]
    private static extern uint BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport(BtDll, CharSet = CharSet.Unicode)]
    private static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

    [DllImport(BtDll, CharSet = CharSet.Unicode)]
    private static extern uint BluetoothEnumerateInstalledServices(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref uint pcServiceInout, [In, Out] Guid[]? pGuidServices);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Verrouille (services décochés) ou déverrouille (services cochés) l'audio
/// des écouteurs. Si Windows exige des droits administrateur, l'app se relance
/// élevée avec « --set-services » pour cette seule opération (voir App).
/// </summary>
internal static class ServiceSwitch
{
    public sealed record Result(bool Success, string Message);

    public const string ElevatedArg = "--set-services";

    private const uint ErrorAccessDenied = 5;
    private const int ErrorCancelled = 1223;

    /// <summary>Vrai si au moins un des services audio gérés est coché.</summary>
    public static bool? IsUnlocked(ulong address)
    {
        var enabled = BluetoothClassic.GetEnabledServices(address);
        return enabled == null ? null : BtServices.Managed.Any(enabled.Contains);
    }

    public static Result Apply(ulong address, bool enable, bool allowElevation)
    {
        var result = ApplyDirect(address, enable, out bool accessDenied);
        if (result.Success || !accessDenied || !allowElevation) return result;
        Log.Write("AirPods : Windows demande des droits administrateur pour modifier les services — relance élevée");
        return RunElevated(address, enable);
    }

    public static Result ApplyDirect(ulong address, bool enable, out bool accessDenied)
    {
        accessDenied = false;
        var before = BluetoothClassic.GetEnabledServices(address);
        var errors = new List<string>();
        int changed = 0;

        foreach (var service in BtServices.Managed)
        {
            if (before != null && before.Contains(service) == enable) continue; // déjà dans le bon état
            uint err = BluetoothClassic.SetServiceState(address, service, enable);
            if (err == 0) { changed++; continue; }
            if (err == ErrorAccessDenied) accessDenied = true;
            errors.Add($"{BtServices.Name(service)} : erreur {err} ({new Win32Exception((int)err).Message})");
        }

        var after = BluetoothClassic.GetEnabledServices(address);
        bool ok = after != null
            ? (enable ? after.Contains(BtServices.AudioSink) : !BtServices.Managed.Any(after.Contains))
            : errors.Count == 0;

        string message = $"{changed} service(s) modifié(s)";
        if (errors.Count > 0) message += " ; " + string.Join(" ; ", errors);
        return new Result(ok, ok ? message : "échec : " + message);
    }

    /// <summary>
    /// Point d'entrée du processus élevé : « --set-services &lt;adresse hex&gt; on|off ».
    /// Renvoie le code de sortie (0 = succès).
    /// </summary>
    public static int RunElevatedWorker(string addressHex, string mode)
    {
        try
        {
            ulong address = ulong.Parse(addressHex, System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture);
            bool enable = mode == "on";
            var result = ApplyDirect(address, enable, out _);
            Log.Write($"AirPods [admin] : {(enable ? "activation" : "désactivation")} — {result.Message}");
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Write($"AirPods [admin] : échec — {ex.Message}");
            return 2;
        }
    }

    private static Result RunElevated(ulong address, bool enable)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!,
                $"{ElevatedArg} {address:X12} {(enable ? "on" : "off")}")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var process = Process.Start(psi);
            if (process == null) return new Result(false, "impossible de lancer le processus administrateur");
            if (!process.WaitForExit(60_000)) return new Result(false, "délai dépassé (processus administrateur)");
            return process.ExitCode == 0
                ? new Result(true, "appliqué avec les droits administrateur")
                : new Result(false, $"échec en mode administrateur (code {process.ExitCode})");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new Result(false, "autorisation administrateur refusée");
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message);
        }
    }
}
