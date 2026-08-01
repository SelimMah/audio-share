namespace AudioShare;

/// <summary>
/// Journal minimal dans « audioshare.log » à côté de l'exécutable, pour
/// diagnostiquer le routage : les échecs y étaient jusqu'ici silencieux.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string File =
        System.IO.Path.Combine(AppContext.BaseDirectory, "audioshare.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                System.IO.File.AppendAllText(File,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
