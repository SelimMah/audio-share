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
    private const string DarkValue = "DarkMode";

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
    /// Mode sombre : voile noir par-dessus l'acrylique du panneau, pour que
    /// le texte blanc reste lisible devant un fond d'écran clair. Off par
    /// défaut (acrylique nu).
    /// </summary>
    public static bool DarkMode
    {
        get => GetString(DarkValue) == "1";
        set => SetString(DarkValue, value ? "1" : null);
    }

    /// <summary>
    /// Destination de l'émission : nom machine d'un Duckast précis,
    /// ou null pour « le premier découvert » (comportement historique).
    /// </summary>
    public static string? SendTarget
    {
        get => GetString(TargetValue);
        set => SetString(TargetValue, value);
    }

    // ---------- AirPods (ex-PodGate, voir AirPodsGate) ----------

    private const string AirPodsEnabledValue = "AirPodsEnabled";
    private const string AirPodsAddressValue = "AirPodsAddress";
    private const string AirPodsNameValue = "AirPodsName";
    private const string AirPodsModelValue = "AirPodsModel";
    private const string AirPodsPromptValue = "AirPodsPrompt";
    private const string AirPodsExpectLockedValue = "AirPodsExpectLocked";
    private const string AirPodsFirstRunValue = "AirPodsFirstRunDone";

    /// <summary>
    /// Gestion des AirPods activée : Windows ne s'y connecte plus seul, Duckast
    /// propose la connexion quand il les détecte. Off par défaut (opt-in).
    /// </summary>
    public static bool AirPodsEnabled
    {
        get => GetString(AirPodsEnabledValue) == "1";
        set => SetString(AirPodsEnabledValue, value ? "1" : null);
    }

    /// <summary>Adresse Bluetooth des écouteurs gérés.</summary>
    public static ulong? AirPodsAddress
    {
        get => ulong.TryParse(GetString(AirPodsAddressValue), System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : null;
        set => SetString(AirPodsAddressValue, value?.ToString("X12"));
    }

    public static string? AirPodsName
    {
        get => GetString(AirPodsNameValue);
        set => SetString(AirPodsNameValue, value);
    }

    /// <summary>Modèle Apple appris (filtre les AirPods des voisins). null = à apprendre.</summary>
    public static ushort? AirPodsModel
    {
        get => ushort.TryParse(GetString(AirPodsModelValue), System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : null;
        set => SetString(AirPodsModelValue, value?.ToString("X4"));
    }

    /// <summary>Proposer la connexion quand ils sont détectés (on par défaut).</summary>
    public static bool AirPodsPrompt
    {
        get => GetString(AirPodsPromptValue) != "0";
        set => SetString(AirPodsPromptValue, value ? null : "0");
    }

    /// <summary>
    /// Vrai quand l'audio doit rester verrouillé : si Windows est connecté aux
    /// AirPods au démarrage suivant, c'est qu'il l'a fait seul.
    /// </summary>
    public static bool AirPodsExpectLocked
    {
        get => GetString(AirPodsExpectLockedValue) != "0";
        set => SetString(AirPodsExpectLockedValue, value ? null : "0");
    }

    /// <summary>
    /// Faux tant que la gestion n'a jamais tourné : on ne coupe alors pas des
    /// AirPods en cours d'utilisation au moment de l'activation.
    /// </summary>
    public static bool AirPodsFirstRunDone
    {
        get => GetString(AirPodsFirstRunValue) == "1";
        set => SetString(AirPodsFirstRunValue, value ? "1" : null);
    }

    /// <summary>Efface tous les réglages AirPods (désactivation).</summary>
    public static void ClearAirPods()
    {
        foreach (var name in new[] { AirPodsEnabledValue, AirPodsAddressValue, AirPodsNameValue, AirPodsModelValue,
                                     AirPodsPromptValue, AirPodsExpectLockedValue, AirPodsFirstRunValue })
            SetString(name, null);
    }

    // ---------- Garde anti-boucle de mise à jour ----------
    // Si l'installateur d'une release n'apporte pas la version qu'elle
    // annonce (asset erroné publié par erreur — arrivé en v1.6.6), l'app se
    // met à jour, redémarre, se retrouve sur l'ancienne version, recommence…
    // sans fin. On compte les tentatives par version cible pour s'arrêter.

    private const string UpdateTargetValue = "UpdateTarget";
    private const string UpdateCountValue = "UpdateAttempts";

    /// <summary>Tentatives déjà faites vers cette version (0 si autre cible).</summary>
    public static int UpdateAttemptsFor(string version) =>
        GetString(UpdateTargetValue) == version
        && int.TryParse(GetString(UpdateCountValue), out var n) ? n : 0;

    public static void RecordUpdateAttempt(string version)
    {
        int n = UpdateAttemptsFor(version);
        SetString(UpdateTargetValue, version);
        SetString(UpdateCountValue, (n + 1).ToString());
    }

    /// <summary>Appelé dès que l'app est à jour : le compteur repart de zéro.</summary>
    public static void ClearUpdateAttempts()
    {
        SetString(UpdateTargetValue, null);
        SetString(UpdateCountValue, null);
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
