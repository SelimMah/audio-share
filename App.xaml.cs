namespace AudioShare;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        // Ne pas afficher la fenêtre : elle restrera cachée jusqu'au clic sur la tray icon.
        // OnSourceInitialized et Loaded se déclencheront quand même.
    }
}
