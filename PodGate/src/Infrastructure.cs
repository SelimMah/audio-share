using System.Text.Json;
using Microsoft.Win32;

namespace PodGate;

/// <summary>Dossier de données : %LOCALAPPDATA%\PodGate (réglages + journal).</summary>
static class AppPaths
{
    public static string Dir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodGate");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

static class Log
{
    static readonly object Sync = new();

    public static string FilePath => Path.Combine(AppPaths.Dir, "podgate.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message} : {ex}");

    /// <summary>Garde le journal sous ~1 Mo (l'ancien est conservé en .old).</summary>
    public static void Rotate()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists || file.Length < 1_000_000) return;
            string old = FilePath + ".old";
            File.Delete(old);
            file.MoveTo(old);
        }
        catch { /* le journal ne doit jamais faire planter l'app */ }
    }

    static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}

/// <summary>Réglages persistés dans settings.json (modifiable à la main, app fermée).</summary>
sealed class Settings
{
    /// <summary>Adresse Bluetooth des AirPods gérés.</summary>
    public ulong? DeviceAddress { get; set; }
    public string? DeviceName { get; set; }

    /// <summary>Identifiant de modèle Apple appris automatiquement (filtre les AirPods des voisins).</summary>
    public ushort? LearnedModel { get; set; }

    public bool PromptOnDetect { get; set; } = true;

    /// <summary>Vrai quand PodGate veut que l'audio reste verrouillé (sert au démarrage suivant).</summary>
    public bool ExpectLocked { get; set; } = true;

    public bool FirstRunDone { get; set; }

    /// <summary>Signal minimum (dBm) pour considérer des AirPods « à proximité ». Plus proche de 0 = plus strict.</summary>
    public int RssiThreshold { get; set; } = -72;

    /// <summary>Sans annonce BLE pendant ce délai, les AirPods sont considérés rangés ou hors de portée.</summary>
    public int PresenceTimeoutSeconds { get; set; } = 45;

    /// <summary>Délai avant de reverrouiller l'audio après une déconnexion.</summary>
    public int AutoLockDelaySeconds { get; set; } = 8;

    /// <summary>Une connexion est attribuée aux Paramètres Windows si leur fenêtre était au premier plan dans ce délai.</summary>
    public int WindowsUiGraceSeconds { get; set; } = 20;

    /// <summary>Temps laissé à Windows pour se connecter après l'activation de l'audio.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 25;

    static string FilePath => Path.Combine(AppPaths.Dir, "settings.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.json illisible, valeurs par défaut utilisées ({ex.Message})");
        }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions)); }
        catch (Exception ex) { Log.Warn($"Impossible d'enregistrer settings.json : {ex.Message}"); }
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }
}

/// <summary>Lancement au démarrage via HKCU\...\Run (pas besoin de droits admin).</summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "PodGate";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --autostart");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info($"Lancement au démarrage : {(enabled ? "activé" : "désactivé")}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Lancement au démarrage non modifié : {ex.Message}");
        }
    }
}

static class Theme
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool AppsLight => ReadDword("AppsUseLightTheme", 1) != 0;
    public static bool TaskbarLight => ReadDword("SystemUsesLightTheme", 0) != 0;

    static int ReadDword(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>Couleur d'accentuation Windows, dans la variante lisible sur fond clair ou sombre.</summary>
    public static Color Accent(bool darkBackground)
    {
        try
        {
            var ui = new global::Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(darkBackground
                ? global::Windows.UI.ViewManagement.UIColorType.AccentLight2
                : global::Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }
        catch
        {
            return darkBackground ? Color.FromArgb(255, 76, 194, 255) : Color.FromArgb(255, 0, 95, 184);
        }
    }

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(255,
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

sealed record Palette(
    Color Back, Color Border, Color Tile, Color Text, Color SubText,
    Color Neutral, Color NeutralHover, Color NeutralBorder,
    Color Accent, Color AccentHover, Color OnAccent)
{
    public static Palette Current()
    {
        bool light = Theme.AppsLight;
        var accent = Theme.Accent(darkBackground: !light);
        return light
            ? new Palette(
                Theme.Hex(0xF9F9F9), Theme.Hex(0xD9D9D9), Theme.Hex(0xEBEBEB), Theme.Hex(0x1A1A1A), Theme.Hex(0x5C5C5C),
                Theme.Hex(0xFFFFFF), Theme.Hex(0xF3F3F3), Theme.Hex(0xD0D0D0),
                accent, Theme.Blend(accent, Color.White, 0.12f), Color.White)
            : new Palette(
                Theme.Hex(0x2C2C2C), Theme.Hex(0x454545), Theme.Hex(0x3A3A3A), Theme.Hex(0xFFFFFF), Theme.Hex(0xC8C8C8),
                Theme.Hex(0x3D3D3D), Theme.Hex(0x484848), Theme.Hex(0x525252),
                accent, Theme.Blend(accent, Color.Black, 0.10f), Color.Black);
    }
}

/// <summary>Boîtes de dialogue toujours au premier plan (l'app n'a pas de fenêtre principale).</summary>
static class Dialogs
{
    public static bool Confirm(string title, string text)
    {
        using var owner = CreateOwner();
        return MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    public static void Info(string title, string text, bool warning = false)
    {
        using var owner = CreateOwner();
        MessageBox.Show(owner, text, title, MessageBoxButtons.OK,
            warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    static Form CreateOwner()
    {
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        var owner = new Form
        {
            TopMost = true,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2),
            Size = new Size(1, 1),
            Opacity = 0,
        };
        owner.Show();
        owner.Activate();
        return owner;
    }
}
