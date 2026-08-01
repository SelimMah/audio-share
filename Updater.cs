using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AudioShare;

/// <summary>
/// Mise à jour automatique au lancement : interroge la dernière release GitHub,
/// compare avec la version de l'assembly, télécharge l'installateur et le lance
/// en silencieux — il ferme l'app, remplace les fichiers et relance la
/// nouvelle version.
/// </summary>
internal static class Updater
{
    private const string ApiUrl = "https://api.github.com/repos/SelimMah/audio-share/releases/latest";

    /// <summary>
    /// Vrai pour l'app réellement installée par le setup, faux pour la copie
    /// de développement (bin\Debug…). Sert aussi à différencier l'icône.
    /// Le marqueur est le désinstallateur qu'Inno Setup dépose à côté de
    /// l'exécutable : fiable quel que soit le dossier d'installation
    /// (par utilisateur ou Program Files en admin).
    /// </summary>
    public static bool IsInstalledCopy =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>Version courante en trois chiffres, pour l'affichage.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <param name="verbose">Vérification demandée par l'utilisateur : rendre
    /// compte de chaque issue, y compris « déjà à jour » et les erreurs.</param>
    /// <returns>true si une mise à jour est lancée (l'app va être fermée).</returns>
    public static async Task<bool> CheckAndUpdateAsync(Action<string> notify, bool verbose = false)
    {
        try
        {
            // La copie de développement (bin\Debug…) ne doit pas s'auto-écraser :
            // seule l'app réellement installée se met à jour.
            if (!IsInstalledCopy)
            {
                Log.Write("MàJ : copie de développement, vérification ignorée");
                if (verbose) notify(Loc.T("Development copy — updates disabled.",
                                          "Copie de développement — mise à jour désactivée."));
                return false;
            }

            if (verbose) notify(Loc.T("Checking…", "Vérification…"));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AudioShare-Updater");

            using var doc = JsonDocument.Parse(await http.GetStringAsync(ApiUrl));
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return false;

            var current = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            if (latest <= current)
            {
                Log.Write($"MàJ : à jour (installée {current}, dernière {latest})");
                if (verbose) notify(Loc.T($"The app is up to date (version {CurrentVersion}).",
                                          $"L'app est à jour (version {CurrentVersion})."));
                return false;
            }

            string? url = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (url == null)
            {
                Log.Write($"MàJ : release {latest} sans installateur, ignorée");
                if (verbose) notify(Loc.T("The latest release has no installer.",
                                          "La dernière release ne contient pas d'installateur."));
                return false;
            }

            Log.Write($"MàJ : {current} -> {latest}, téléchargement…");
            notify(Loc.T($"Updating to version {latest} — downloading…",
                         $"Mise à jour vers la version {latest} — téléchargement…"));

            string setupPath = Path.Combine(Path.GetTempPath(), "AudioShare-Setup.exe");
            await using (var download = await http.GetStreamAsync(url))
            await using (var file = File.Create(setupPath))
                await download.CopyToAsync(file);

            notify(Loc.T("Installing the update — the app will restart.",
                         "Installation de la mise à jour — l'app va redémarrer."));
            Log.Write("MàJ : lancement de l'installateur silencieux");

            // L'installateur ferme l'app (taskkill), installe, puis la relance.
            Process.Start(new ProcessStartInfo(setupPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            // Hors ligne, API indisponible… : l'app démarre normalement.
            Log.Write($"MàJ : vérification impossible ({ex.Message})");
            if (verbose) notify(Loc.T("Could not check for updates — check your connection.",
                                      "Vérification impossible — regarde ta connexion."));
            return false;
        }
    }
}
