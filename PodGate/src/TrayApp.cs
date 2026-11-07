using System.Diagnostics;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;

namespace PodGate;

/// <summary>
/// Cœur de l'application (icône de la zone de notification).
///
/// Principe : les services audio des AirPods (A2DP, AVRCP, HFP) restent décochés, donc Windows
/// ne s'y connecte jamais tout seul. PodGate les coche à la demande, puis les décoche dès que
/// les AirPods se déconnectent.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly bool _fromAutostart;
    readonly Settings _settings;
    readonly Control _invoker;
    readonly PromptPopup _popup;
    readonly NotifyIcon _tray;
    readonly AirPodsScanner _scanner = new();
    readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 500 };

    readonly ToolStripMenuItem _miStatus = new("PodGate") { Enabled = false };
    readonly ToolStripMenuItem _miBattery = new("") { Enabled = false };
    readonly ToolStripMenuItem _miConnect = new("Connecter les AirPods");
    readonly ToolStripMenuItem _miDisconnect = new("Déconnecter les AirPods");
    readonly ToolStripMenuItem _miDevice = new("Appareil géré");
    readonly ToolStripMenuItem _miPrompt = new("Proposer la connexion quand ils sont détectés");
    readonly ToolStripMenuItem _miAutostart = new("Lancer au démarrage de Windows");
    readonly ToolStripMenuItem _miLog = new("Ouvrir le journal");
    readonly ToolStripMenuItem _miReset = new("Désactiver PodGate et restaurer Windows…");
    readonly ToolStripMenuItem _miQuit = new("Quitter");

    PairedDevice? _device;
    BluetoothDevice? _winrtDevice;

    bool _servicesEnabled;      // services audio cochés (= « déverrouillé »)
    bool _connected;            // lien Bluetooth établi avec les AirPods
    bool _busy;                 // modification des services en cours
    bool _stopping;

    bool _present;              // annonces BLE reçues récemment
    bool _promptSuppressed;     // « Plus tard » : pas de nouvelle proposition avant qu'ils disparaissent
    DateTime _lastAdvertAt = DateTime.MinValue;
    DateTime _lastPromptAt = DateTime.MinValue;
    AirPodsAdvert? _lastAdvert;
    readonly Dictionary<ushort, int> _modelVotes = new();

    DateTime? _disconnectedSince;
    DateTime? _awaitUntil;      // on attend que Windows se connecte après déverrouillage
    bool _awaitShowsPopup;

    int _pollCount;
    string _lastUiKey = "";

    public TrayApp(bool fromAutostart)
    {
        _fromAutostart = fromAutostart;
        _settings = Settings.Load();

        // Contrôle invisible : sert à revenir sur le thread de l'interface depuis les événements Bluetooth.
        _invoker = new Control();
        _ = _invoker.Handle;

        _popup = new PromptPopup();
        _popup.ConnectClicked += () => Fire(() => ConnectAsync(true, "connexion demandée depuis la fenêtre"), "Connexion");
        _popup.Dismissed += () =>
        {
            _promptSuppressed = true;
            Log.Info("Proposition ignorée : pas de rappel tant que les AirPods restent à proximité");
        };

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Tray(TrayState.Locked),
            Text = "PodGate",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _tray.MouseClick += OnTrayClick;

        _pollTimer.Tick += (_, _) => Poll();
        _foregroundTimer.Tick += (_, _) => ForegroundTracker.Sample();
        _scanner.AdvertReceived += advert => Post(() => OnAdvert(advert));

        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Post(() => Fire(StartAsync, "Démarrage"));
    }

    // =================================================================== Démarrage

    async Task StartAsync()
    {
        Log.Info($"PodGate {Application.ProductVersion} démarre{(_fromAutostart ? " (ouverture de session)" : "")}");

        bool firstRun = !_settings.FirstRunDone;
        if (firstRun)
        {
            AutoStart.Set(true);
            _settings.FirstRunDone = true;
            _settings.Save();
        }
        _miAutostart.Checked = AutoStart.IsEnabled;

        await AttachDeviceAsync(FindDevice(), allowTakeover: !firstRun);

        _scanner.Start();
        _pollTimer.Start();
        _foregroundTimer.Start();

        if (firstRun)
            Notify("PodGate est actif",
                "Windows ne se connectera plus seul à tes AirPods. Je te proposerai de les connecter quand ils seront détectés.",
                ToolTipIcon.Info);
        UpdateUi();
    }

    PairedDevice? FindDevice()
    {
        List<PairedDevice> devices;
        try { devices = BluetoothClassic.GetPairedDevices(); }
        catch (Exception ex)
        {
            Log.Warn($"Liste des appareils Bluetooth indisponible : {ex.Message}");
            return null;
        }

        if (_settings.DeviceAddress is ulong saved && devices.FirstOrDefault(d => d.Address == saved) is PairedDevice known)
            return known;
        return devices.FirstOrDefault(d => d.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="allowTakeover">
    /// Si Windows s'est connecté tout seul au démarrage, PodGate coupe et propose la connexion.
    /// Désactivé au tout premier lancement pour ne pas couper des AirPods en cours d'utilisation.
    /// </param>
    async Task AttachDeviceAsync(PairedDevice? device, bool allowTakeover)
    {
        DetachWinRt();
        _device = device;
        _connected = false;
        _servicesEnabled = false;
        _awaitUntil = null;
        _modelVotes.Clear();
        _lastUiKey = "";

        if (device == null)
        {
            Log.Warn("Aucun AirPods appairé. PodGate attend qu'ils le soient (appairage depuis les Paramètres Windows).");
            UpdateUi();
            return;
        }

        if (_settings.DeviceAddress != device.Address)
        {
            _settings.DeviceAddress = device.Address;
            _settings.DeviceName = device.Name;
            _settings.Save();
        }

        _connected = device.Connected;
        _servicesEnabled = ServiceSwitch.IsUnlocked(device.Address) ?? false;
        Log.Info($"Appareil géré : {device.Name} [{device.AddressText}] ; lien {(_connected ? "connecté" : "déconnecté")} ; audio {(_servicesEnabled ? "autorisé" : "verrouillé")}");

        try
        {
            var winrt = await BluetoothDevice.FromBluetoothAddressAsync(device.Address);
            if (winrt != null && _device == device)
            {
                _winrtDevice = winrt;
                _winrtDevice.ConnectionStatusChanged += OnWinRtConnectionChanged;
            }
            else
            {
                winrt?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Suivi de connexion WinRT indisponible (le sondage prend le relais) : {ex.Message}");
        }

        if (_servicesEnabled)
        {
            bool justBooted = _fromAutostart && Environment.TickCount64 < 5 * 60 * 1000;
            if (!_connected)
            {
                await SetServicesAsync(false, "verrouillage au démarrage");
            }
            else if (allowTakeover && (_settings.ExpectLocked || justBooted))
            {
                Log.Info("Windows s'est connecté tout seul aux AirPods : PodGate reprend la main");
                if (await SetServicesAsync(false, "connexion automatique de Windows annulée"))
                    ShowPrompt();
            }
            else
            {
                Log.Info("AirPods en cours d'utilisation sur ce PC : PodGate n'y touche pas pour l'instant");
            }
        }
        UpdateUi();
    }

    // ========================================================== Boucle de sondage

    void Poll()
    {
        if (_stopping) return;
        var now = DateTime.Now;
        _pollCount++;

        if (_device == null)
        {
            if (_pollCount % 10 == 0 && FindDevice() is PairedDevice found)
                Fire(() => AttachDeviceAsync(found, allowTakeover: false), "Rattachement de l'appareil");
            UpdatePresence(now);
            UpdateUi();
            return;
        }

        // Source principale : l'API Win32. WinRT ne sert que si elle ne répond pas.
        bool connected;
        if (BluetoothClassic.GetDevice(_device.Address) is PairedDevice info)
        {
            connected = info.Connected;
        }
        else
        {
            try { connected = _winrtDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected; }
            catch { connected = false; }
        }

        if (connected != _connected)
        {
            _connected = connected;
            OnConnectionChanged(connected);
        }
        if (connected) _disconnectedSince = null;
        else _disconnectedSince ??= now;

        if (!_busy && _pollCount % 3 == 0) RefreshServicesState();

        if (_awaitUntil is DateTime until)
        {
            if (connected && _servicesEnabled)
            {
                _awaitUntil = null;
                Log.Info("AirPods connectés à ce PC");
                if (_awaitShowsPopup)
                    _popup.ShowStatus(_device.Name, "Connectés. Le son passe maintenant par tes AirPods.", false, 3500);
            }
            else if (now > until && !_busy)
            {
                _awaitUntil = null;
                Log.Warn("Windows ne s'est pas connecté dans le délai imparti : reverrouillage");
                if (_awaitShowsPopup)
                    _popup.ShowStatus(_device.Name, "Connexion impossible. Vérifie qu'ils sont sortis du boîtier, puis réessaie.", false, 6000);
                Fire(() => SetServicesAsync(false, "échec de connexion"), "Verrouillage");
            }
        }
        else if (_servicesEnabled && !connected && !_busy &&
                 _disconnectedSince is DateTime since &&
                 (now - since).TotalSeconds >= _settings.AutoLockDelaySeconds)
        {
            Fire(() => SetServicesAsync(false, "AirPods déconnectés"), "Verrouillage");
        }

        UpdatePresence(now);
        UpdateUi();
    }

    void OnWinRtConnectionChanged(BluetoothDevice sender, object args) => Post(Poll);

    void OnConnectionChanged(bool connected)
    {
        Log.Info($"Lien Bluetooth {(connected ? "établi" : "coupé")} (audio {(_servicesEnabled ? "autorisé" : "verrouillé")})");

        if (connected && _servicesEnabled && _popup.IsShowingPrompt())
            _popup.HidePopup();

        if (!connected || _servicesEnabled || _busy) return;

        // Lien établi alors que l'audio est verrouillé : soit tu viens de cliquer « Connecter »
        // dans Windows, soit c'est une reconnexion automatique. On tranche selon la fenêtre active.
        if (ForegroundTracker.BluetoothUiActiveWithin(TimeSpan.FromSeconds(_settings.WindowsUiGraceSeconds)))
        {
            Log.Info("Connexion lancée depuis l'interface Windows : activation de l'audio");
            Fire(() => ConnectAsync(true, "connexion depuis les Paramètres Windows"), "Connexion depuis Windows");
        }
        else
        {
            Log.Info("Lien établi sans action de ta part dans Windows : ignoré (l'audio reste verrouillé)");
        }
    }

    /// <summary>Détecte une case cochée/décochée à la main dans le Panneau de configuration.</summary>
    void RefreshServicesState()
    {
        if (_device == null) return;
        if (ServiceSwitch.IsUnlocked(_device.Address) is not bool enabled || enabled == _servicesEnabled) return;

        Log.Info($"Services audio {(enabled ? "cochés" : "décochés")} en dehors de PodGate");
        _servicesEnabled = enabled;
        if (enabled && !_connected && _awaitUntil == null)
        {
            _awaitUntil = DateTime.Now.AddSeconds(_settings.ConnectTimeoutSeconds);
            _awaitShowsPopup = false;
        }
    }

    // =================================================== Détection BLE et popup

    void OnAdvert(AirPodsAdvert advert)
    {
        if (_stopping || _device == null) return;

        // Apprentissage du modèle : quand TES AirPods sont connectés à ce PC, l'annonce forte qui revient
        // le plus est la leur. Ensuite, on ignore les autres modèles (AirPods des voisins).
        if (_settings.LearnedModel == null && _connected && _servicesEnabled && advert.Rssi >= -60)
        {
            int votes = _modelVotes.TryGetValue(advert.Model, out var v) ? v + 1 : 1;
            _modelVotes[advert.Model] = votes;
            if (votes >= 5)
            {
                _settings.LearnedModel = advert.Model;
                _settings.Save();
                Log.Info($"Modèle de tes AirPods appris : {advert.ModelName} (0x{advert.Model:X4})");
            }
        }

        if (_settings.LearnedModel is ushort learned && advert.Model != learned) return;
        if (advert.Rssi < _settings.RssiThreshold) return;

        _lastAdvert = advert;
        _lastAdvertAt = DateTime.Now;
        if (_present) return;

        _present = true;
        Log.Info($"AirPods détectés : {advert.ModelName}, {advert.Rssi} dBm. {advert.BatteryText}");
        MaybePrompt();
        UpdateUi();
    }

    void UpdatePresence(DateTime now)
    {
        if (!_present || (now - _lastAdvertAt).TotalSeconds <= _settings.PresenceTimeoutSeconds) return;
        _present = false;
        _promptSuppressed = false;
        Log.Info("AirPods plus détectés (rangés dans le boîtier ou hors de portée)");
        if (_popup.IsShowingPrompt()) _popup.HidePopup();
    }

    void MaybePrompt()
    {
        if (!_settings.PromptOnDetect || _device == null || _busy || _awaitUntil != null) return;
        if (_servicesEnabled && _connected) return;
        if (_promptSuppressed || (DateTime.Now - _lastPromptAt).TotalSeconds < 60) return;
        ShowPrompt();
    }

    void ShowPrompt()
    {
        if (_device == null) return;
        _lastPromptAt = DateTime.Now;
        string battery = _present && _lastAdvert != null ? _lastAdvert.BatteryText : "";
        _popup.ShowPrompt(_device.Name, _present ? "Détectés. Les connecter à ce PC ?" : "Les connecter à ce PC ?", battery, 25_000);
    }

    void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_device == null)
        {
            Notify("PodGate", "Aucun AirPods appairé. Appaire-les une première fois depuis les Paramètres Bluetooth de Windows.", ToolTipIcon.Info);
            return;
        }
        if (_servicesEnabled && _connected)
            _popup.ShowStatus(_device.Name, "Connectés à ce PC. Clic droit sur l'icône pour les déconnecter.", false, 4000);
        else if (!_busy)
            ShowPrompt();
    }

    // ============================================== Connexion / déconnexion

    async Task ConnectAsync(bool showProgress, string reason)
    {
        var device = _device;
        if (device == null)
        {
            Notify("PodGate", "Aucun AirPods appairé. Appaire-les une première fois depuis les Paramètres Bluetooth de Windows.", ToolTipIcon.Info);
            return;
        }
        if (_busy) return;
        if (_servicesEnabled && _connected)
        {
            if (showProgress) _popup.ShowStatus(device.Name, "Déjà connectés à ce PC.", false, 3000);
            return;
        }

        if (showProgress) _popup.ShowStatus(device.Name, "Connexion en cours…", true, 0);
        if (!await SetServicesAsync(true, reason))
        {
            if (showProgress)
                _popup.ShowStatus(device.Name, "Échec : l'audio Bluetooth n'a pas pu être activé (détails dans le journal).", false, 6000);
            return;
        }
        // Windows se connecte de lui-même dès que les services sont cochés ; Poll() suit la suite.
        _awaitUntil = DateTime.Now.AddSeconds(_settings.ConnectTimeoutSeconds);
        _awaitShowsPopup = showProgress;
    }

    async Task DisconnectAsync()
    {
        var device = _device;
        if (device == null || _busy) return;
        _awaitUntil = null;
        _popup.HidePopup();
        if (await SetServicesAsync(false, "déconnexion demandée"))
        {
            _promptSuppressed = true; // pas de proposition immédiate juste après avoir déconnecté
            _popup.ShowStatus(device.Name, "Déconnectés. Windows ne s'y reconnectera pas seul.", false, 3000);
        }
    }

    /// <summary>Coche (enable) ou décoche les services audio, en arrière-plan.</summary>
    async Task<bool> SetServicesAsync(bool enable, string reason)
    {
        var device = _device;
        if (device == null) return false;

        _busy = true;
        UpdateUi();
        try
        {
            Log.Info($"{(enable ? "Activation" : "Désactivation")} de l'audio ({reason})…");
            var result = await Task.Run(() => ServiceSwitch.Apply(device.Address, enable, allowElevation: true));
            Log.Info($"→ {result.Message}");

            _servicesEnabled = ServiceSwitch.IsUnlocked(device.Address) ?? (result.Success ? enable : _servicesEnabled);
            if (result.Success)
            {
                _settings.ExpectLocked = !enable;
                _settings.Save();
            }
            else
            {
                Notify("PodGate", $"Impossible de {(enable ? "activer" : "désactiver")} l'audio Bluetooth : {result.Message}", ToolTipIcon.Warning);
            }
            return result.Success;
        }
        finally
        {
            _busy = false;
            _disconnectedSince = null;
            UpdateUi();
        }
    }

    // ======================================================== Événements système

    void OnSessionEnding(object? sender, SessionEndingEventArgs e) => LockSynchronously("arrêt de Windows / fermeture de session");

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            LockSynchronously("mise en veille");
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Log.Info("Sortie de veille");
            _present = false;
            _promptSuppressed = false;
        }
    }

    /// <summary>Verrouillage immédiat (pas d'UAC possible à l'arrêt) pour que Windows ne se reconnecte pas au prochain démarrage.</summary>
    void LockSynchronously(string reason)
    {
        if (_stopping) return;
        _awaitUntil = null;
        _settings.ExpectLocked = true;
        _settings.Save();
        if (_device == null || !_servicesEnabled) return;

        var result = ServiceSwitch.Apply(_device.Address, false, allowElevation: false);
        Log.Info($"Verrouillage ({reason}) : {result.Message}");
        if (result.Success) _servicesEnabled = false;
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Passage thème clair/sombre : on redessine l'icône.
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;
        IconFactory.ClearCache();
        _lastUiKey = "";
        UpdateUi();
    }

    // ===================================================================== Menu

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _miConnect.Click += (_, _) => Fire(() => ConnectAsync(true, "connexion demandée depuis le menu"), "Connexion");
        _miDisconnect.Click += (_, _) => Fire(DisconnectAsync, "Déconnexion");

        _miDevice.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        _miDevice.DropDownOpening += (_, _) => FillDeviceMenu();

        _miPrompt.Checked = _settings.PromptOnDetect;
        _miPrompt.Click += (_, _) =>
        {
            _settings.PromptOnDetect = !_settings.PromptOnDetect;
            _miPrompt.Checked = _settings.PromptOnDetect;
            _settings.Save();
        };

        _miAutostart.Click += (_, _) =>
        {
            AutoStart.Set(!AutoStart.IsEnabled);
            _miAutostart.Checked = AutoStart.IsEnabled;
        };

        _miLog.Click += (_, _) => OpenLog();
        _miReset.Click += (_, _) => Fire(ResetAsync, "Restauration");
        _miQuit.Click += (_, _) => Fire(QuitAsync, "Fermeture");

        menu.Items.AddRange(new ToolStripItem[]
        {
            _miStatus, _miBattery,
            new ToolStripSeparator(),
            _miConnect, _miDisconnect,
            new ToolStripSeparator(),
            _miDevice, _miPrompt, _miAutostart, _miLog,
            new ToolStripSeparator(),
            _miReset, _miQuit,
        });
        return menu;
    }

    void FillDeviceMenu()
    {
        _miDevice.DropDownItems.Clear();
        List<PairedDevice> devices;
        try { devices = BluetoothClassic.GetPairedDevices().Where(d => d.IsAudio).ToList(); }
        catch (Exception ex)
        {
            Log.Warn($"Liste des appareils indisponible : {ex.Message}");
            devices = new List<PairedDevice>();
        }

        if (devices.Count == 0)
        {
            _miDevice.DropDownItems.Add(new ToolStripMenuItem("Aucun appareil audio appairé") { Enabled = false });
            return;
        }
        foreach (var device in devices)
        {
            var item = new ToolStripMenuItem(device.Name) { Checked = _device?.Address == device.Address };
            item.Click += (_, _) => Fire(() => SwitchDeviceAsync(device), "Changement d'appareil");
            _miDevice.DropDownItems.Add(item);
        }
    }

    async Task SwitchDeviceAsync(PairedDevice device)
    {
        if (_busy || _device?.Address == device.Address) return;
        // On rend l'ancien appareil à Windows avant d'en gérer un autre.
        if (_device != null && !_servicesEnabled)
            await SetServicesAsync(true, $"restitution de {_device.Name} à Windows");
        _settings.LearnedModel = null;
        _present = false;
        _lastAdvert = null;
        await AttachDeviceAsync(device, allowTakeover: false);
    }

    void OpenLog()
    {
        try
        {
            Log.Info("Ouverture du journal");
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("PodGate", $"Impossible d'ouvrir le journal : {ex.Message}", ToolTipIcon.Warning);
        }
    }

    void UpdateUi()
    {
        if (_stopping) return;

        var state = _device == null ? TrayState.NoDevice
                  : _busy ? TrayState.Busy
                  : _servicesEnabled && _connected ? TrayState.Connected
                  : _present ? TrayState.Present
                  : TrayState.Locked;

        string status =
            _device == null ? "aucun AirPods appairé" :
            _busy ? "opération en cours…" :
            _servicesEnabled && _connected ? "connectés" :
            _servicesEnabled ? "audio autorisé, en attente de connexion" :
            _present ? "détectés à proximité, non connectés" :
            "verrouillés (Windows ne s'y connecte pas seul)";

        string battery = _present && _lastAdvert != null ? _lastAdvert.BatteryText : "";
        string key = $"{state}|{status}|{battery}|{_device?.Name}";
        if (key == _lastUiKey) return;
        _lastUiKey = key;

        _tray.Icon = IconFactory.Tray(state);
        string tip = _device == null ? $"PodGate : {status}" : $"{_device.Name} : {status}";
        _tray.Text = tip.Length > 120 ? tip[..120] : tip;

        _miStatus.Text = _device == null ? $"PodGate : {status}" : $"{_device.Name} : {status}";
        _miBattery.Text = battery;
        _miBattery.Available = battery.Length > 0;

        bool idle = _device != null && !_busy;
        _miConnect.Enabled = idle && !(_servicesEnabled && _connected);
        _miDisconnect.Enabled = idle && (_servicesEnabled || _connected);
    }

    // ================================================ Restauration et fermeture

    async Task ResetAsync()
    {
        if (!Dialogs.Confirm("Désactiver PodGate",
                "PodGate va :\n\n" +
                "• recocher les services audio de tes AirPods (comportement normal de Windows) ;\n" +
                "• retirer son lancement au démarrage ;\n" +
                "• effacer ses réglages ;\n" +
                "• puis se fermer.\n\nContinuer ?"))
            return;

        StopMonitoring();
        bool ok = true;
        if (_device != null)
        {
            var device = _device;
            var result = await Task.Run(() => ServiceSwitch.Apply(device.Address, true, allowElevation: true));
            Log.Info($"Restauration des services de {device.Name} : {result.Message}");
            ok = result.Success;
        }
        AutoStart.Set(false);
        Settings.Delete();
        Log.Info("PodGate désactivé, réglages Windows restaurés");

        Dialogs.Info("PodGate désactivé", ok
            ? "Windows gère de nouveau tes AirPods normalement (connexion automatique comprise).\n\nTu peux supprimer PodGate.exe si tu n'en veux plus."
            : "Les réglages de PodGate sont effacés, mais les services audio n'ont pas pu être recochés.\n\n" +
              "Fais-le à la main : Périphériques et imprimantes → AirPods → Propriétés → Services → coche tout.",
            warning: !ok);
        ExitApp();
    }

    async Task QuitAsync()
    {
        StopMonitoring();
        // On laisse l'audio verrouillé (sauf si les AirPods sont en cours d'utilisation).
        if (_device != null && _servicesEnabled && !_connected && !_busy)
        {
            var device = _device;
            var result = await Task.Run(() => ServiceSwitch.Apply(device.Address, false, allowElevation: false));
            Log.Info($"Verrouillage avant fermeture : {result.Message}");
            if (result.Success)
            {
                _settings.ExpectLocked = true;
                _settings.Save();
            }
        }
        Log.Info("PodGate fermé");
        ExitApp();
    }

    void StopMonitoring()
    {
        _stopping = true;
        _pollTimer.Stop();
        _foregroundTimer.Stop();
        _scanner.Dispose();
        _popup.HidePopup();
    }

    void ExitApp()
    {
        _stopping = true;
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        DetachWinRt();
        _pollTimer.Dispose();
        _foregroundTimer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _popup.Dispose();
        _invoker.Dispose();
        ExitThread();
    }

    // ================================================================ Utilitaires

    void DetachWinRt()
    {
        if (_winrtDevice == null) return;
        try
        {
            _winrtDevice.ConnectionStatusChanged -= OnWinRtConnectionChanged;
            _winrtDevice.Dispose();
        }
        catch { }
        _winrtDevice = null;
    }

    void Post(Action action)
    {
        if (_invoker.IsDisposed || !_invoker.IsHandleCreated) return;
        try { _invoker.BeginInvoke(action); }
        catch (InvalidOperationException) { /* application en cours de fermeture */ }
    }

    void Notify(string title, string text, ToolTipIcon icon)
    {
        try { _tray.ShowBalloonTip(6000, title, text, icon); } catch { }
    }

    static async void Fire(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception ex) { Log.Error(what, ex); }
    }
}
