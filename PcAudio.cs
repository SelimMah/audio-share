using NAudio.CoreAudioApi;

namespace AudioShare;

/// <summary>
/// Crête du son que le PC joue lui-même sur sa sortie par défaut, pour que
/// l'aurore AirPods danse aussi sur la musique locale. Lu par le vumètre à
/// chaque image (fil d'interface) : la sortie par défaut est relue toutes
/// les 2 s pour suivre un changement (AirPods qui se connectent…).
/// </summary>
internal static class PcAudio
{
    private static MMDeviceEnumerator? _enumerator;
    private static MMDevice? _device;
    private static long _nextLookup;

    public static float Peak
    {
        get
        {
            long now = Environment.TickCount64;
            if (now >= _nextLookup)
            {
                _nextLookup = now + 2000;
                try
                {
                    _enumerator ??= new MMDeviceEnumerator();
                    var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (device.ID != _device?.ID)
                    {
                        _device?.Dispose();
                        _device = device;
                    }
                    else device.Dispose();
                }
                catch
                {
                    _device?.Dispose();
                    _device = null; // pas de sortie active
                }
            }
            try { return _device?.AudioMeterInformation.MasterPeakValue ?? 0f; }
            catch { return 0f; }
        }
    }
}
