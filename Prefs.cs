namespace AudioShare;

/// <summary>
/// Préférences persistées dans le registre (même clé que la langue, Loc).
/// </summary>
internal static class Prefs
{
    private const string RegPath = @"Software\AudioShare";
    private const string OutputValue = "OutputDeviceId";
    private const string OpusValue = "OpusEnabled";
    private const string TargetValue = "SendTarget";

    private static string? GetString(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath);
            return key?.GetValue(name) as string;
        }
        catch { return null; }
    }

    private static void SetString(string name, string? value)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath);
            if (string.IsNullOrEmpty(value))
                key.DeleteValue(name, throwOnMissingValue: false);
            else
                key.SetValue(name, value);
        }
        catch { }
    }

    /// <summary>
    /// Compression Opus du flux réseau émis (256 kbit/s, transparent) :
    /// ~6× moins de bande passante, utile en Wi-Fi chargé. Off = PCM brut.
    /// </summary>
    public static bool OpusEnabled
    {
        get => GetString(OpusValue) == "1";
        set => SetString(OpusValue, value ? "1" : null);
    }

    /// <summary>
    /// Destination de l'émission : nom machine d'un Audio Share précis,
    /// ou null pour « le premier découvert » (comportement historique).
    /// </summary>
    public static string? SendTarget
    {
        get => GetString(TargetValue);
        set => SetString(TargetValue, value);
    }

    /// <summary>
    /// Sortie du son reçu par le réseau : ID MMDevice, ou null pour la sortie
    /// par défaut. Le son Bluetooth du téléphone, lui, est rendu par Windows
    /// et suit toujours la sortie par défaut (contrainte du moteur audio).
    /// </summary>
    public static string? OutputDeviceId
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath);
                return key?.GetValue(OutputValue) as string;
            }
            catch { return null; }
        }
        set
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath);
                if (string.IsNullOrEmpty(value))
                    key.DeleteValue(OutputValue, throwOnMissingValue: false);
                else
                    key.SetValue(OutputValue, value);
            }
            catch { }
        }
    }
}
