using System.Globalization;

namespace PodGate;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Mode « worker administrateur » : relancé par PodGate lui-même via UAC
        // quand Windows refuse de modifier les services Bluetooth sans élévation.
        if (args.Length >= 3 && args[0] == "--set-services")
            return RunElevatedWorker(args[1], args[2]);

        using var mutex = new Mutex(true, @"Local\PodGate.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "PodGate est déjà lancé : son icône est dans la zone de notification, près de l'horloge.",
                "PodGate", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Log.Rotate();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Exception non gérée (interface)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Exception fatale", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Tâche en échec", e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(fromAutostart: args.Contains("--autostart")));
        return 0;
    }

    static int RunElevatedWorker(string addressHex, string mode)
    {
        try
        {
            ulong address = ulong.Parse(addressHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            bool enable = mode == "on";
            var result = ServiceSwitch.ApplyDirect(address, enable, out _);
            Log.Info($"[admin] {(enable ? "activation" : "désactivation")} : {result.Message}");
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error("[admin] échec", ex);
            return 2;
        }
    }
}
