using System.Runtime.InteropServices;

namespace AudioShare;

/// <summary>
/// Joue un bip de feedback quand l'utilisateur change le volume,
/// proportionnel au volume choisi.
/// </summary>
internal sealed class VolumeTestTone
{
    [DllImport("kernel32.dll")]
    private static extern bool Beep(uint frequency, uint duration);

    /// <summary>
    /// Joue un bip (~150 ms) proportionnel au volume spécifié.
    /// Non-bloquant.
    /// </summary>
    public static void PlayAtVolume(float volume)
    {
        if (volume < 0.001f) return;

        _ = Task.Run(() =>
        {
            try
            {
                // Fréquence varie avec le volume (300–1200 Hz)
                uint freq = (uint)(300 + volume * 900);
                // Durée courte, fixe
                Beep(freq, 80);
            }
            catch { /* Ignorer les erreurs */ }
        });
    }
}
