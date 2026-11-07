namespace AudioShare;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Mode « worker administrateur » : relancé par l'app elle-même via UAC
        // quand Windows refuse de modifier les services Bluetooth des AirPods
        // sans élévation (voir ServiceSwitch). Aucune fenêtre, on sort aussitôt.
        if (e.Args.Length >= 3 && e.Args[0] == ServiceSwitch.ElevatedArg)
        {
            Shutdown(ServiceSwitch.RunElevatedWorker(e.Args[1], e.Args[2]));
            return;
        }

        var window = new MainWindow();
        // Ne pas afficher la fenêtre : elle restrera cachée jusqu'au clic sur la tray icon.
        // OnSourceInitialized et Loaded se déclencheront quand même.
    }
}
