namespace AudioShare;

/// <summary>
/// Préférences persistées dans le registre (même clé que la langue, Loc).
/// </summary>
internal static class Prefs
{
    private const string RegPath = @"Software\AudioShare";
    private const string OutputValue = "OutputDeviceId";

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
