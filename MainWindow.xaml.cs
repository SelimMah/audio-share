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
    private VolumeKeyHook? _volumeKeys;
    private VolumeOsd? _osd;

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

        // La hauteur suit le contenu : rester ancré en bas à droite quand
        // il change (statut d'émission qui apparaît, appareil ajouté…).
        SizeChanged += (_, _) => { if (IsVisible) PlaceBottomRight(); };

        _net.Changed += () => Dispatcher.Invoke(UpdateNetworkUi);
        // Contrôles partagés : le volume vit chez l'émetteur. Si c'est nous
        // qui émettons, un ASHAREVOL entrant est une télécommande du
        // récepteur ; sinon c'est l'émetteur qui nous informe (affichage).
        _net.RemoteGain += gain => Dispatcher.Invoke(() =>
        {
            if (_sender.IsRunning)
            {
                _sender.SetEmissionVolume(gain);
                return;
            }
            _syncingVolume = true;
            VolumeSlider.Value = gain;
            _syncingVolume = false;
        });
        _net.RemoteBalance += (l, r) => Dispatcher.Invoke(() => SyncBalanceFromRemote(l, r));
        _net.Start();

        Loc.Changed += ApplyLanguage;
        ApplyLanguage();
        _sender.Changed += () => Dispatcher.Invoke(UpdateSendUi);
        // En émission, le curseur montre le volume émis : il suit aussi les
        // touches physiques (BeginInvoke : l'événement vient d'un rappel COM,
        // ne pas y bloquer).
        _sender.EmissionVolumeChanged += v => Dispatcher.BeginInvoke(() =>
        {
            if (!_sender.IsRunning) return;
            _syncingVolume = true;
            VolumeSlider.Value = v;
            _syncingVolume = false;
        });

        StartWatcher();

        Loaded += (_, _) =>
        {
            AnimateIn();

            // Suit la session du téléphone : volume synchronisé, et balance
            // réappliquée automatiquement après chaque reconnexion.
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pollTimer.Tick += (_, _) => PollPhoneSession();
            _pollTimer.Start();

            // Mise à jour automatique en arrière-plan : si une nouvelle release
            // existe, l'installateur silencieux remplace l'app et la relance.
            _ = Updater.CheckAndUpdateAsync(msg =>
                Dispatcher.Invoke(() => _tray.Notify("Audio Share", msg)));
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PlaceBottomRight(); // avant le premier rendu, pour éviter un flash en 0,0
        WindowEffects.Apply(this);
    }

    // ---------- Réglages ----------

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AudioShare"; // même nom que l'installateur

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsView(SettingsPanel.Visibility != Visibility.Visible);

    /// <summary>
    /// Applique la langue à tous les textes fixes ; les textes d'état
    /// (en-têtes dynamiques, statuts) sont régénérés par les Update*Ui.
    /// </summary>
    private void ApplyLanguage()
    {
        _syncingLanguage = true;
        LangEnglish.IsChecked = !Loc.French;
        LangFrench.IsChecked = Loc.French;
        _syncingLanguage = false;

        GeneralHeader.Text = Loc.T("GENERAL", "GÉNÉRAL");
        AutostartLabel.Text = Loc.T("Launch at Windows startup", "Ouvrir au démarrage de Windows");
        LanguageLabel.Text = Loc.T("Language", "Langue");
        UpdateLabel.Text = Loc.T("Updates", "Mise à jour");
        CheckUpdateButton.Content = Loc.T("Check", "Vérifier");

        EmptyHint.Text = Loc.T("No paired phone — pair one in Windows Bluetooth settings.",
                               "Aucun téléphone appairé — appaire-le dans les réglages Bluetooth de Windows.");
        NetworkLabel.Text = Loc.T("Local network", "Réseau local");
        NetworkRow.ToolTip = Loc.T(
            "Another PC running Audio Share can stream its audio here: turn on “Send” over there, the connection is automatic.",
            "Un autre PC avec Audio Share peut diffuser son audio ici : active « Envoyer » là-bas, la connexion est automatique.");
        SendLabel.Text = Loc.T("Send this PC's audio", "Envoyer le son de ce PC");
        BalanceLeft.Content = Loc.T("Left", "Gauche");
        BalanceStereo.Content = Loc.T("Stereo", "Stéréo");
        BalanceRight.Content = Loc.T("Right", "Droite");
        BalanceCard.ToolTip = Loc.T(
            "Only affects the received audio (phone or other PC) — never this PC's own sounds.",
            "N'agit que sur le son reçu (téléphone ou autre PC) — jamais sur les sons de ce PC.");

        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            TitleText.Text = Loc.T("Settings", "Réglages");
            UpdateStatus.Text = Loc.T($"Installed version: {Updater.CurrentVersion}",
                                      $"Version installée : {Updater.CurrentVersion}");
        }
        UpdateNetworkUi();
        UpdateSendUi();
        _tray.ApplyLanguage();
    }

    private bool _syncingLanguage;

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingLanguage) return;
        Loc.SetFrench(LangFrench.IsChecked == true);
    }

    /// <summary>La page Réglages remplace la page principale, et inversement.</summary>
    private void ShowSettingsView(bool show)
    {
        if (show)
        {
            AutostartToggle.IsChecked = IsAutostartEnabled();
            _syncingLanguage = true;
            LangEnglish.IsChecked = !Loc.French;
            LangFrench.IsChecked = Loc.French;
            _syncingLanguage = false;
            UpdateStatus.Text = Loc.T($"Installed version: {Updater.CurrentVersion}",
                                      $"Version installée : {Updater.CurrentVersion}");
            // La page Réglages, plus courte, garde la taille de la page
            // principale : pas de fenêtre qui rétrécit à la bascule.
            MinHeight = ActualHeight;
        }
        else
        {
            MinHeight = 0;
        }
        SettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MainPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = show ? Loc.T("Settings", "Réglages") : "Audio Share";
        SettingsButton.Content = show ? "" : ""; // croix / engrenage
        SettingsButton.ToolTip = show ? "Retour" : "Réglages";
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (AutostartToggle.IsChecked == true)
            {
                string exe = Environment.ProcessPath
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "AudioShare.exe");
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Démarrage automatique : modification impossible ({ex.Message})");
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            await Updater.CheckAndUpdateAsync(
                msg => Dispatcher.Invoke(() => UpdateStatus.Text = msg),
                verbose: true);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    // ---------- Session du téléphone ----------

    private void UpdateNetworkUi()
    {
        // État compact à droite de la ligne « Réseau local », comme le statut
        // des appareils Bluetooth juste au-dessus.
        if (_net.IsReceiving)
        {
            NetworkStatus.Text = $"🎵 {_net.SenderName}";
            NetworkStatus.Foreground = (System.Windows.Media.Brush)FindResource("Green");
        }
        else
        {
            NetworkStatus.Text = Loc.T("listening", "en écoute");
            NetworkStatus.Foreground = (System.Windows.Media.Brush)FindResource("LabelTertiary");
        }
        VolumeSlider.IsEnabled = PhoneAudio.IsPresent || _net.IsReceiving || _sender.IsRunning;
        MuteButton.IsEnabled = VolumeSlider.IsEnabled;
    }

    /// <summary>
    /// L'émetteur nous relaie ses réglages (contrôles partagés) : on suit son
    /// état et on aligne les boutons quand la valeur correspond à une position
    /// (les pas intermédiaires de son animation sont appliqués sans toucher
    /// aux boutons).
    /// </summary>
    private void SyncBalanceFromRemote(float l, float r)
    {
        _balL = l;
        _balR = r;

        const float eps = 0.01f;
        if (Math.Abs(l - 1f) < eps && r < eps) BalanceLeft.IsChecked = true;
        else if (l < eps && Math.Abs(r - 1f) < eps) BalanceRight.IsChecked = true;
        else if (Math.Abs(l - 1f) < eps && Math.Abs(r - 1f) < eps) BalanceStereo.IsChecked = true;
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
        VolumeHeader.Text = _sender.IsRunning
            ? Loc.T("SENT AUDIO", "SON ÉMIS")
            : Loc.T("RECEIVED AUDIO", "SON REÇU");
        VolumeSlider.IsEnabled = PhoneAudio.IsPresent || _net.IsReceiving || _sender.IsRunning;
        MuteButton.IsEnabled = VolumeSlider.IsEnabled;

        // Pendant l'émission, les touches volume sont interceptées : elles
        // pilotent le volume émis avec notre aperçu, et le périphérique
        // (muet, 0 %) n'est jamais rallumé par Windows.
        if (_sender.IsRunning && _volumeKeys == null)
        {
            _volumeKeys = new VolumeKeyHook();
            _volumeKeys.VolumeStep += s => ShowVolumeOsd(_sender.NudgeEmissionVolume(s));
            _volumeKeys.MuteToggle += () => ShowVolumeOsd(_sender.ToggleEmissionMute());
        }
        else if (!_sender.IsRunning && _volumeKeys != null)
        {
            _volumeKeys.Dispose();
            _volumeKeys = null;
            _osd?.HideNow();
        }
    }

    private void ShowVolumeOsd(float volume)
    {
        _osd ??= new VolumeOsd();
        _osd.ShowVolume(volume);
    }

    private void PollPhoneSession()
    {
        PhoneAudio.Refresh();
        bool present = PhoneAudio.IsPresent;
        VolumeSlider.IsEnabled = present || _net.IsReceiving || _sender.IsRunning;
        MuteButton.IsEnabled = VolumeSlider.IsEnabled;
        // En émission, le curseur appartient au volume émis.
        if (_sender.IsRunning || !present) return;

        float volume = PhoneAudio.Volume;
        if (Math.Abs(VolumeSlider.Value - volume) < 0.02) return;

        _syncingVolume = true;
        VolumeSlider.Value = volume;
        _syncingVolume = false;
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateMuteIcon();
        if (_syncingVolume || !IsLoaded) return;
        float v = (float)e.NewValue;
        if (_sender.IsRunning)
        {
            // En émission, le curseur pilote le volume émis (logiciel,
            // appliqué aux échantillons envoyés).
            _sender.SetEmissionVolume(v);
            return;
        }
        if (_net.IsReceiving)
        {
            // En réception réseau, le volume vit chez l'émetteur : on le
            // télécommande, il nous renverra la valeur en écho.
            _net.SendVolumeToSender(v);
            return;
        }
        PhoneAudio.Volume = v;
    }

    // ---------- Muet ----------

    private float _preMuteVolume = 0.5f;

    /// <summary>
    /// L'icône à gauche du curseur coupe/rétablit le volume (reçu ou émis
    /// selon le mode) en passant simplement le curseur à 0 et retour : les
    /// chemins d'application existants font le reste.
    /// </summary>
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0.001)
        {
            _preMuteVolume = (float)VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else
        {
            VolumeSlider.Value = _preMuteVolume > 0.001f ? _preMuteVolume : 0.5;
        }
    }

    private void UpdateMuteIcon()
    {
        double v = VolumeSlider.Value;
        MuteButton.Content = v switch
        {
            <= 0.001 => "", // muet
            < 0.34 => "",
            < 0.67 => "",
            _ => "",
        };
        MuteButton.ToolTip = v <= 0.001
            ? Loc.T("Unmute", "Rétablir le son")
            : Loc.T("Mute", "Couper le son");
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
        AnimateBalanceTo(l, r);
    }

    private DispatcherTimer? _balanceAnim;
    private float _balL = 1f, _balR = 1f;

    /// <summary>
    /// Fait glisser la balance vers la cible en ~600 ms (courbe en S) au lieu
    /// de sauter : le son semble se déplacer d'un côté à l'autre. Les pas de
    /// 25 ms sont assez fins pour être inaudibles individuellement.
    /// </summary>
    private void AnimateBalanceTo(float targetL, float targetR)
    {
        _balanceAnim?.Stop();

        float fromL = _balL, fromR = _balR;
        var start = DateTime.UtcNow;
        const double durationMs = 800;

        _balanceAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _balanceAnim.Tick += (_, _) =>
        {
            double p = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / durationMs);
            float eased = (float)(p * p * (3 - 2 * p));
            _balL = fromL + (targetL - fromL) * eased;
            _balR = fromR + (targetR - fromR) * eased;
            PhoneAudio.SetBalance(_balL, _balR);
            _net.SetBalance(_balL, _balR);
            // En émission, chaque pas de l'animation est relayé au récepteur :
            // la balance glisse là-bas exactement comme ici.
            if (_sender.IsRunning) _sender.SendBalance(_balL, _balR);
            if (p >= 1.0) _balanceAnim!.Stop();
        };
        _balanceAnim.Start();
    }

    // ---------- Flyout et zone de notification ----------

    private DateTime _lastAutoHide = DateTime.MinValue;

    private void PlaceBottomRight()
    {
        // La hauteur suit le contenu (SizeToContent) : Height reste NaN,
        // c'est ActualHeight qui fait foi une fois la mise en page calculée.
        var area = SystemParameters.WorkArea;
        double height = ActualHeight > 1 ? ActualHeight : 560;
        Left = area.Right - Width - 12;
        Top = area.Bottom - height - 12;
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
        // Le panneau rouvre toujours sur la page principale.
        if (SettingsPanel.Visibility == Visibility.Visible) ShowSettingsView(false);
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
            _tray.Notify(Loc.T("Audio Share keeps running in the background",
                               "Audio Share reste actif en arrière-plan"),
                         Loc.T("Click the tray icon to reopen. Quit: right-click the icon.",
                               "Clique sur l'icône pour rouvrir. Quitter : clic droit sur l'icône."));
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
            d.Status = opened ? Loc.T("connected", "connecté") : Loc.T("waiting", "en attente");
            _tray.SetActive(opened);
        });

        try
        {
            await connection.StartAsync();
        }
        catch
        {
            CloseConnection(dev.Id);
            DeviceList.SelectedItem = null;
            return;
        }

        dev.Status = Loc.T("waiting", "en attente");
        dev.IsActive = true;
        _tray.SetActive(true);

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
        _volumeKeys?.Dispose();
        _osd?.Close();
        _net.Dispose();
        _sender.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }
}
