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

    /// <returns>true si une mise à jour est lancée (l'app va être fermée).</returns>
    public static async Task<bool> CheckAndUpdateAsync(Action<string> notify)
    {
        try
        {
            // La copie de développement (bin\Debug…) ne doit pas s'auto-écraser :
            // seule l'app réellement installée se met à jour.
            if (!AppContext.BaseDirectory.Contains(
                    Path.Combine("Programs", "Audio Share"), StringComparison.OrdinalIgnoreCase))
            {
                Log.Write("MàJ : copie de développement, vérification ignorée");
                return false;
            }

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
                return false;
            }

            Log.Write($"MàJ : {current} -> {latest}, téléchargement…");
            notify($"Mise à jour vers la version {latest} — téléchargement en cours…");

            string setupPath = Path.Combine(Path.GetTempPath(), "AudioShare-Setup.exe");
            await using (var download = await http.GetStreamAsync(url))
            await using (var file = File.Create(setupPath))
                await download.CopyToAsync(file);

            notify("Installation de la mise à jour — l'app va redémarrer.");
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
            return false;
        }
    }
}
