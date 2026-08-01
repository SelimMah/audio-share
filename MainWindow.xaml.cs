using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace AudioShare;

public class BtDevice : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    private string _status = "";
    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<BtDevice> _devices = new();
    private readonly Dictionary<string, AudioPlaybackConnection> _connections = new();
    private readonly NetworkReceiver _net = new();
    private readonly NetworkSender _sender = new();
    private readonly TrayIcon _tray = new();
    private DeviceWatcher? _watcher;
    private DispatcherTimer? _retryTimer;
    private DispatcherTimer? _pollTimer;
    private string? _activeId;
    private bool _quitting;
    private bool _trayHintShown;
    private bool _syncingVolume;

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;

        // Les anciennes versions passaient par un câble virtuel ; si l'une
        // d'elles a été tuée brutalement, ses redirections ont survécu dans le
        // registre et détourneraient encore du son. On nettoie au démarrage.
        PurgeStaleCablePins();

        _tray.ShowRequested += () => Dispatcher.Invoke(ToggleFlyout);
        _tray.QuitRequested += () => Dispatcher.Invoke(Quit);
        _tray.BalanceRequested += (l, r) => Dispatcher.Invoke(() => SetBalanceFromTray(l, r));

        // Comportement « flyout » : la fenêtre se retire dès qu'on clique ailleurs.
        Deactivated += (_, _) => { if (IsVisible && !_quitting) HideFlyout(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideFlyout(); };

        _net.Changed += () => Dispatcher.Invoke(UpdateNetworkUi);
        _net.Start();
        _sender.Changed += () => Dispatcher.Invoke(UpdateSendUi);

        StartWatcher();

        Loaded += (_, _) =>
        {
            AnimateIn();

            // Suit la session du téléphone : volume synchronisé, et balance
            // réappliquée automatiquement après chaque reconnexion.
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pollTimer.Tick += (_, _) => PollPhoneSession();
            _pollTimer.Start();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PlaceBottomRight(); // avant le premier rendu, pour éviter un flash en 0,0
        WindowEffects.Apply(this);
    }

    // ---------- Session du téléphone ----------

    private void UpdateNetworkUi()
    {
        NetworkStatus.Text = _net.IsReceiving
            ? $"🎵 Réception du son de « {_net.SenderName} » — il est diffusé sur ce PC."
            : "En écoute — installe Audio Share sur l'autre PC et active « Envoyer » là-bas.";
        VolumeSlider.IsEnabled = PhoneAudio.IsPresent || _net.IsReceiving;
    }

    private void SendToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (SendToggle.IsChecked == true) _sender.Start();
        else _sender.Stop();
        UpdateSendUi();
    }

    private void UpdateSendUi()
    {
        SendStatus.Text = _sender.State;
        SendStatus.Visibility = string.IsNullOrEmpty(_sender.State)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PollPhoneSession()
    {
        PhoneAudio.Refresh();
        bool present = PhoneAudio.IsPresent;
        VolumeSlider.IsEnabled = present || _net.IsReceiving;
        if (!present) return;

        float volume = PhoneAudio.Volume;
        if (Math.Abs(VolumeSlider.Value - volume) < 0.02) return;

        _syncingVolume = true;
        VolumeSlider.Value = volume;
        _syncingVolume = false;
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingVolume || !IsLoaded) return;
        PhoneAudio.Volume = (float)e.NewValue;
        _net.SetGain((float)e.NewValue);
    }

    // ---------- Balance ----------

    private void Balance_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyBalance();
    }

    private void ApplyBalance()
    {
        float l = 1f, r = 1f;
        if (BalanceLeft.IsChecked == true) r = 0f;
        else if (BalanceRight.IsChecked == true) l = 0f;

        _tray.SyncBalance(l, r);
        PhoneAudio.SetBalance(l, r);
        _net.SetBalance(l, r);

        if (l != r && !PhoneAudio.IsPresent && !_net.IsReceiving)
            StatusText.Text = "La balance s'appliquera dès qu'un son sera reçu.";
    }

    // ---------- Flyout et zone de notification ----------

    private DateTime _lastAutoHide = DateTime.MinValue;

    private void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    private void ToggleFlyout()
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }

        // Le clic sur l'icône vient souvent de désactiver — donc cacher — la
        // fenêtre juste avant d'arriver ici : l'utilisateur voulait la fermer,
        // pas la rouvrir aussitôt.
        if ((DateTime.UtcNow - _lastAutoHide).TotalMilliseconds < 350) return;

        ShowFlyout();
    }

    private void ShowFlyout()
    {
        PlaceBottomRight();
        Show();
        Activate();
        AnimateIn();
    }

    /// <summary>Glissement vers le haut + fondu, comme les panneaux système de Windows 11.</summary>
    private void AnimateIn()
    {
        RootSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(240))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void HideFlyout()
    {
        if (!IsVisible) return;
        _lastAutoHide = DateTime.UtcNow;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.Notify("Audio Share reste actif en arrière-plan",
                         "Clique sur l'icône pour rouvrir. Quitter : clic droit sur l'icône.");
        }
    }

    private void SetBalanceFromTray(float left, float right)
    {
        if (left == 1f && right == 0f) BalanceLeft.IsChecked = true;
        else if (left == 0f && right == 1f) BalanceRight.IsChecked = true;
        else BalanceStereo.IsChecked = true;
    }

    private void Quit()
    {
        _quitting = true;
        Close();
    }

    // ---------- Bluetooth ----------

    private void StartWatcher()
    {
        // Énumère les appareils appairés capables d'envoyer de l'audio vers ce PC (A2DP source)
        _watcher = DeviceInformation.CreateWatcher(AudioPlaybackConnection.GetDeviceSelector());

        _watcher.Added += (_, info) => Dispatcher.Invoke(() =>
        {
            if (_devices.All(d => d.Id != info.Id))
                _devices.Add(new BtDevice { Id = info.Id, Name = info.Name, Status = "" });
            EmptyHint.Visibility = Visibility.Collapsed;
        });

        _watcher.Removed += (_, update) => Dispatcher.Invoke(() =>
        {
            var dev = _devices.FirstOrDefault(d => d.Id == update.Id);
            if (dev != null) _devices.Remove(dev);
            CloseConnection(update.Id);
        });

        _watcher.EnumerationCompleted += (_, _) => Dispatcher.Invoke(() =>
        {
            EmptyHint.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });

        _watcher.Start();
    }

    private async void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (DeviceList.SelectedItem is not BtDevice dev || dev.Id == _activeId) return;

        // Une seule connexion active à la fois
        if (_activeId != null) CloseConnection(_activeId);
        await StartConnectionAsync(dev);
    }

    private async Task StartConnectionAsync(BtDevice dev)
    {
        var connection = AudioPlaybackConnection.TryCreateFromId(dev.Id);
        if (connection == null)
        {
            StatusText.Text = "Impossible de créer la connexion audio pour cet appareil.";
            DeviceList.SelectedItem = null;
            return;
        }

        _connections[dev.Id] = connection;
        _activeId = dev.Id;

        connection.StateChanged += (conn, _) => Dispatcher.Invoke(() =>
        {
            var d = _devices.FirstOrDefault(x => x.Id == _activeId);
            if (d == null) return;
            bool opened = conn.State == AudioPlaybackConnectionState.Opened;
            d.Status = opened ? "connecté" : "en attente";
            StatusText.Text = opened
                ? $"Le son de « {d.Name} » est diffusé sur ce PC."
                : "Connexion fermée. Reconnecte le Bluetooth depuis le téléphone.";
            _tray.SetActive(opened);
        });

        StatusText.Text = $"Activation de la réception pour « {dev.Name} »…";

        try
        {
            await connection.StartAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erreur à l'activation : {ex.Message}";
            CloseConnection(dev.Id);
            DeviceList.SelectedItem = null;
            return;
        }

        dev.Status = "en attente";
        dev.IsActive = true;
        _tray.SetActive(true);
        StatusText.Text = $"Réception active. Sur « {dev.Name} », connecte-toi à ce PC depuis les réglages Bluetooth.";

        // Tente d'ouvrir le flux audio toutes les 2 s jusqu'à ce que le téléphone soit connecté
        _retryTimer?.Stop();
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retryTimer.Tick += async (_, _) => await TryOpenAsync();
        _retryTimer.Start();
        await TryOpenAsync();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // ne pas laisser le clic re-sélectionner la ligne
        if (_activeId != null) CloseConnection(_activeId);
        DeviceList.SelectedItem = null;
        StatusText.Text = "Réception arrêtée.";
    }

    private async Task TryOpenAsync()
    {
        if (_activeId == null || !_connections.TryGetValue(_activeId, out var connection)) return;
        if (connection.State == AudioPlaybackConnectionState.Opened)
        {
            _retryTimer?.Stop();
            return;
        }

        try
        {
            var result = await connection.OpenAsync();
            if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success)
                _retryTimer?.Stop();
        }
        catch
        {
            // Le téléphone n'est pas encore connecté — on réessaie au prochain tick
        }
    }

    private void CloseConnection(string id)
    {
        _retryTimer?.Stop();
        if (_connections.Remove(id, out var connection))
            connection.Dispose();
        if (_activeId == id) _activeId = null;
        var dev = _devices.FirstOrDefault(d => d.Id == id);
        if (dev != null)
        {
            dev.Status = "";
            dev.IsActive = false;
        }
        _tray.SetActive(false);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
    }

    /// <summary>
    /// Supprime du registre les redirections anonymes vers le câble virtuel
    /// laissées par les anciennes versions de l'app.
    /// </summary>
    private static void PurgeStaleCablePins()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore",
                writable: true);
            if (key == null) return;

            var doomed = new List<string>();
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue("") is string v
                    && v.Contains("vbaudio", StringComparison.OrdinalIgnoreCase)
                    && v.Contains("|#%b", StringComparison.Ordinal))
                    doomed.Add(name);
            }
            foreach (var name in doomed)
            {
                try { key.DeleteSubKeyTree(name); } catch { }
            }
        }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // La croix réduit dans la zone de notification ; on ne quitte que
        // depuis le menu de l'icône.
        if (!_quitting)
        {
            e.Cancel = true;
            HideFlyout();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer?.Stop();
        _retryTimer?.Stop();
        _watcher?.Stop();

        // Ne pas laisser la session du téléphone avec un canal coupé.
        try { PhoneAudio.SetBalance(1f, 1f); } catch { }

        // Important : libère la connexion Bluetooth, sinon elle monopolise
        // la sortie audio et le reste du PC devient muet.
        foreach (var c in _connections.Values) c.Dispose();
        _connections.Clear();
        _net.Dispose();
        _sender.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }
}
