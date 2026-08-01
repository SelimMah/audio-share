using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioShare;

/// <summary>
/// Pilote la session audio de l'appareil Bluetooth : volume, sourdine et
/// balance gauche/droite. La balance passe par le volume par canal de la
/// session (IChannelAudioVolume) : elle n'affecte que le flux du téléphone,
/// sans câble virtuel, sans latence, et sans toucher au reste du PC.
/// </summary>
internal static class PhoneAudio
{
    // Le minuteur d'interface et la fermeture appellent ce module en parallèle.
    private static readonly object Gate = new();

    private static MMDevice? _device;          // gardé en vie : la session en dépend
    private static AudioSessionControl? _session;
    private static float _left = 1f, _right = 1f;

    public static uint? ProcessId { get; private set; }

    public static bool IsPresent { get { lock (Gate) return _session != null; } }

    /// <summary>
    /// Relit la liste des sessions, mémorise celle de l'appareil Bluetooth et
    /// lui réapplique la balance : une reconnexion crée une session neuve qui
    /// repart en stéréo, la réassertion périodique la rattrape en ~1 s.
    /// </summary>
    public static bool Refresh()
    {
        lock (Gate)
        {
            bool found = RefreshCore();
            if (found) ApplyBalanceCore();
            return found;
        }
    }

    private static bool RefreshCore()
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice? fallbackDevice = null;
        AudioSessionControl? fallback = null;

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            // Un périphérique récalcitrant ne doit pas interrompre le passage
            // en revue des autres.
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        var name = session.DisplayName ?? "";
                        if (!name.Contains("A2DP", StringComparison.OrdinalIgnoreCase)) continue;

                        // La session active est celle qui joue réellement ; les
                        // inactives sont des vestiges sur d'autres périphériques.
                        if (session.State == AudioSessionState.AudioSessionStateActive)
                        {
                            _device = device;
                            _session = session;
                            ProcessId = session.GetProcessID;
                            return true;
                        }
                        if (fallback == null) { fallback = session; fallbackDevice = device; }
                    }
                    catch { /* session disparue entre-temps */ }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"PhoneAudio : sessions illisibles sur « {device.FriendlyName} » : {ex.Message}");
            }
        }

        if (fallback != null)
        {
            _device = fallbackDevice;
            _session = fallback;
            ProcessId = fallback.GetProcessID;
            return true;
        }

        _device = null;
        _session = null;
        ProcessId = null;
        return false;
    }

    // ---------- Balance ----------

    /// <summary>1,1 = stéréo ; 1,0 = gauche seulement ; 0,1 = droite seulement.</summary>
    public static void SetBalance(float left, float right)
    {
        lock (Gate)
        {
            _left = left;
            _right = right;
            ApplyBalanceCore();
        }
    }

    private static void ApplyBalanceCore()
    {
        if (_session == null) return;
        try
        {
            if (GetChannelVolume(_session) is not { } channels) return;

            channels.GetChannelCount(out uint count);
            if (count < 2) return;

            var ctx = Guid.Empty;
            channels.SetChannelVolume(0, _left, ref ctx);
            channels.SetChannelVolume(1, _right, ref ctx);
        }
        catch (Exception ex)
        {
            Log.Write($"PhoneAudio : balance refusée : {ex.Message}");
        }
    }

    private static FieldInfo? _comField;

    /// <summary>
    /// NAudio n'expose pas IChannelAudioVolume : on récupère l'objet COM de la
    /// session par réflexion, et le transtypage déclenche le QueryInterface.
    /// </summary>
    private static IChannelAudioVolume? GetChannelVolume(AudioSessionControl session)
    {
        _comField ??= typeof(AudioSessionControl)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(f => f.FieldType.Name.Contains("IAudioSessionControl"));
        return _comField?.GetValue(session) as IChannelAudioVolume;
    }

    /// <summary>
    /// Crête instantanée de la session du téléphone (0..1), pour le vumètre.
    /// Le flux A2DP n'est pas capturable, mais son compteur de niveau l'est.
    /// </summary>
    public static float Peak
    {
        get
        {
            lock (Gate)
            {
                try { return _session?.AudioMeterInformation.MasterPeakValue ?? 0f; }
                catch { return 0f; }
            }
        }
    }

    // ---------- Volume et sourdine ----------

    public static float Volume
    {
        get
        {
            try { return _session?.SimpleAudioVolume.Volume ?? 1f; }
            catch { return 1f; }
        }
        set
        {
            try
            {
                if (_session != null) _session.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f);
            }
            catch { /* la session a pu se fermer */ }
        }
    }

    public static bool Muted
    {
        get
        {
            try { return _session?.SimpleAudioVolume.Mute ?? false; }
            catch { return false; }
        }
        set
        {
            try
            {
                if (_session != null) _session.SimpleAudioVolume.Mute = value;
            }
            catch { }
        }
    }

    // L'ordre des méthodes doit suivre exactement la vtable COM (audioclient.h).
    [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChannelAudioVolume
    {
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetChannelVolume(uint index, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolume(uint index, out float level);
        [PreserveSig] int SetAllVolumes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] volumes, ref Guid eventContext);
        [PreserveSig] int GetAllVolumes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] volumes);
    }
}
