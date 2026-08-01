namespace AudioShare;

/// <summary>
/// Langue de l'interface : anglais par défaut, français en option (switch
/// dans les Réglages). Volontairement minimaliste — chaque texte fournit ses
/// deux variantes sur place via T(en, fr), pas de fichiers de ressources.
/// </summary>
internal static class Loc
{
    private const string RegPath = @"Software\AudioShare";
    private const string RegValue = "Language";

    public static bool French { get; private set; }

    /// <summary>Levé sur le thread qui change la langue (UI).</summary>
    public static event Action? Changed;

    static Loc()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath);
            French = key?.GetValue(RegValue) as string == "fr";
        }
        catch { }
    }

    public static void SetFrench(bool french)
    {
        if (French == french) return;
        French = french;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue(RegValue, french ? "fr" : "en");
        }
        catch { }
        Changed?.Invoke();
    }

    public static string T(string en, string fr) => French ? fr : en;
}
