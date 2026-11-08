using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;

namespace AudioShare;

internal enum AirPodsState
{
    /// <summary>Gestion désactivée (ou tenue par une autre instance de Duckast).</summary>
    Off,
    /// <summary>Activée, mais aucun AirPods appairé.</summary>
    NoDevice,
    /// <summary>Services décochés : Windows ne s'y connecte pas seul.</summary>
    Locked,
    /// <summary>Verrouillés, mais détectés à proximité (annonces BLE).</summary>
    Present,
    /// <summary>Modification des services en cours.</summary>
    Busy,
    /// <summary>Connectés à ce PC, audio autorisé.</summary>
    Connected,
}

/// <summary>Niveaux de batterie (par tranches de 10 %, null = inconnu) lus dans les annonces BLE.</summary>
internal sealed record AirPodsBattery(
    int? Left, int? Right, int? Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging)
{
    public bool IsEmpty => Left == null && Right == null && Case == null;

    public static AirPodsBattery From(AirPodsAdvert a) =>
        new(a.Left, a.Right, a.Case, a.LeftCharging, a.RightCharging, a.CaseCharging);
}

/// <summary>Proposition « Connecter » à afficher.</summary>
internal sealed record AirPodsPrompt(string Title, AirPodsBattery? Battery, int TimeoutMs);

internal enum AirPodsMessageKind { Info, Progress, Success, Error }

/// <summary>Message d'état à afficher (0 = pas de fermeture auto).</summary>
internal sealed record AirPodsMessage(string Title, string Text, AirPodsMessageKind Kind, int AutoCloseMs, AirPodsBattery? Battery);

/// <summary>
/// Gestion des AirPods, reprise de PodGate. Aucune interface ici : l'UI
/// s'abonne aux événements et appelle les méthodes publiques.
///
/// Principe : les services audio des AirPods (A2DP, AVRCP, HFP) restent
/// décochés, donc Windows ne s'y connecte jamais tout seul. On les coche à la
/// demande, puis on les décoche dès que les AirPods se déconnectent.
///
/// Tout tourne sur le thread du Dispatcher passé au constructeur ; les
/// événements y sont levés aussi.
/// </summary>
internal sealed class AirPodsGate : IDisposable
{
    // Seuils repris de PodGate (réglables dans son settings.json, constantes ici).
    private const int RssiThreshold = -72;          // signal minimum pour « à proximité »
    private const int LearnRssi = -60;              // signal minimum pour apprendre le modèle
    private const int LearnVotes = 5;
    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AutoLockDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WindowsUiGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);
    // Délai minimal entre deux propositions automatiques (anti-rafale si le
    // signal hésite) ; rangés puis ressortis, ils doivent pouvoir reproposer vite.
    private static readonly TimeSpan PromptCooldown = TimeSpan.FromSeconds(10);
    // Annonces « rangés » sans aucune « disponibles » pendant ce délai = rangés.
    private static readonly TimeSpan StowedConfirm = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan JustBooted = TimeSpan.FromMinutes(5);
    private const int PromptTimeoutMs = 25_000;

    // Copie de dev et copie installée partagent les réglages : une seule
    // instance à la fois gère les services, sinon elles se battraient.
    private const string OwnershipMutexName = @"Local\Duckast.AirPodsGate";

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _foregroundTimer;
    private AirPodsScanner? _scanner;
    private Mutex? _ownership;

    private bool _running;
    private bool _activating;
    private bool _ownedElsewhere;
    private bool _disposed;

    private PairedDevice? _device;
    private BluetoothDevice? _winrtDevice;

    private bool _servicesEnabled;      // services audio cochés (= « déverrouillé »)
    private bool _connected;            // lien Bluetooth établi avec les AirPods
    private bool _busy;                 // modification des services en cours
    private bool _busyEnabling;         // … dans le sens « autoriser l'audio »

    private bool _present;              // annonces BLE reçues récemment
    private bool _promptSuppressed;     // proposition refusée : pas de nouvelle avant qu'ils soient rangés
    private bool _promptShown;
    private DateTime _lastAdvertAt = DateTime.MinValue;
    private DateTime _lastPromptAt = DateTime.MinValue;
    private AirPodsAdvert? _lastAdvert;
    private readonly Dictionary<ushort, int> _modelVotes = new();
    // Dernier état du couvercle vu, par écouteur émetteur (true = gauche/droite selon le bit 0x20).
    private readonly Dictionary<bool, (bool Closed, int Count)> _lastLid = new();

    private DateTime? _disconnectedSince;
    private DateTime? _awaitUntil;      // on attend que Windows se connecte après déverrouillage
    private bool _awaitShowsProgress;

    private int _pollCount;

    /// <summary>L'état ou les textes ont changé : rafraîchir l'affichage.</summary>
    public event Action? Changed;
    /// <summary>Afficher la proposition « Connecter ».</summary>
    public event Action<AirPodsPrompt>? PromptRequested;
    /// <summary>La proposition n'a plus lieu d'être (connectés, rangés…) : la masquer.</summary>
    public event Action? PromptWithdrawn;
    /// <summary>Afficher un message d'état (remplace la proposition s'il y en a une).</summary>
    public event Action<AirPodsMessage>? MessageRequested;
    /// <summary>Ouverture du boîtier alors qu'ils sont déjà connectés : afficher l'état (batteries) un moment.</summary>
    public event Action? StatusPeekRequested;
    /// <summary>Notification système (bulle de la zone de notification).</summary>
    public event Action<string, string>? NotifyRequested;

    public AirPodsGate(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _foregroundTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += (_, _) => Poll();
        _foregroundTimer.Tick += (_, _) => ForegroundTracker.Sample();

        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Loc.Changed += RaiseChanged;
    }

    // ================================================================ État public

    /// <summary>Réglage utilisateur (persisté).</summary>
    public bool IsEnabled => Prefs.AirPodsEnabled;

    /// <summary>Vrai si cette instance surveille effectivement les AirPods.</summary>
    public bool IsRunning => _running;

    /// <summary>Activée, mais une autre instance de Duckast tient déjà la gestion.</summary>
    public bool IsOwnedElsewhere => _ownedElsewhere;

    public AirPodsState State =>
        !_running ? AirPodsState.Off :
        _device == null ? AirPodsState.NoDevice :
        _busy ? AirPodsState.Busy :
        _servicesEnabled && _connected ? AirPodsState.Connected :
        _present ? AirPodsState.Present :
        AirPodsState.Locked;

    public string? DeviceName => _device?.Name;

    public ulong? DeviceAddress => _device?.Address;

    /// <summary>Batterie lue dans les annonces BLE (vide s'ils ne sont pas détectés).</summary>
    public string BatteryText => _present && _lastAdvert != null ? _lastAdvert.BatteryText : "";

    /// <summary>Batterie détaillée (null s'ils ne sont pas détectés).</summary>
    public AirPodsBattery? Battery => _present && _lastAdvert != null ? AirPodsBattery.From(_lastAdvert) : null;

    public string StatusText =>
        _ownedElsewhere ? Loc.T("Managed by another Duckast instance", "Gérés par une autre instance de Duckast") :
        !_running ? Loc.T("Off", "Désactivé") :
        _device == null ? Loc.T("No AirPods paired", "Aucun AirPods appairé") :
        _busy ? Loc.T("Working…", "Opération en cours…") :
        _servicesEnabled && _connected ? Loc.T("Connected", "Connectés") :
        _servicesEnabled ? Loc.T("Audio allowed, waiting for connection", "Audio autorisé, en attente de connexion") :
        _present ? Loc.T("Nearby, not connected", "Détectés à proximité, non connectés") :
        Loc.T("Locked (Windows won't connect on its own)", "Verrouillés (Windows ne s'y connecte pas seul)");

    /// <summary>Services audio cochés : connectés, ou en attente que Windows se connecte.</summary>
    public bool AudioAllowed => _running && _servicesEnabled;

    /// <summary>
    /// L'action principale est « Déconnecter » (connectés, ou connexion en
    /// cours d'attente qu'on peut annuler) plutôt que « Connecter ».
    /// </summary>
    public bool PrefersDisconnect => State == AirPodsState.Connected || (AudioAllowed && !_busy);

    public bool CanConnect => _running && _device != null && !_busy && !(_servicesEnabled && _connected);

    public bool CanDisconnect => _running && _device != null && !_busy && (_servicesEnabled || _connected);

    /// <summary>Proposer la connexion quand les AirPods sont détectés.</summary>
    public bool PromptOnDetect
    {
        get => Prefs.AirPodsPrompt;
        set
        {
            Prefs.AirPodsPrompt = value;
            if (!value) WithdrawPrompt();
            RaiseChanged();
        }
    }

    // ================================================== Activation / arrêt

    /// <summary>Au lancement de l'app : démarre la surveillance si l'utilisateur l'a activée.</summary>
    public void Start()
    {
        if (Prefs.AirPodsEnabled) Fire(ActivateAsync, "Démarrage");
    }

    /// <summary>
    /// Interrupteur de l'UI. Désactiver rend les AirPods à Windows (services
    /// recochés) et efface les réglages. Renvoie faux si la restauration a échoué
    /// (une notification explique alors comment le faire à la main).
    /// </summary>
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (_disposed) return false;
        if (enabled)
        {
            Prefs.AirPodsEnabled = true;
            await ActivateAsync();
            return _running;
        }

        bool wasRunning = _running;
        var device = _device;
        StopMonitoring();

        bool ok = true;
        if (wasRunning && device != null)
        {
            var result = await Task.Run(() => ServiceSwitch.Apply(device.Address, true, allowElevation: true));
            Log.Write($"AirPods : restauration des services de {device.Name} — {result.Message}");
            ok = result.Success;
        }
        Prefs.ClearAirPods();
        Log.Write("AirPods : gestion désactivée, Windows reprend la main");

        if (!ok)
            NotifyRequested?.Invoke("Duckast", Loc.T(
                "Couldn't restore the AirPods audio services. Do it manually: Devices and Printers → AirPods → Properties → Services → check everything.",
                "Les services audio des AirPods n'ont pas pu être recochés. Fais-le à la main : Périphériques et imprimantes → AirPods → Propriétés → Services → coche tout."));
        RaiseChanged();
        return ok;
    }

    private async Task ActivateAsync()
    {
        if (_running || _activating || _disposed) return;

        // Pris sur le thread UI, avant tout await : un Mutex se libère sur le thread qui l'a pris.
        var mutex = new Mutex(true, OwnershipMutexName, out bool owned);
        if (!owned)
        {
            mutex.Dispose();
            if (!_ownedElsewhere) Log.Write("AirPods : une autre instance de Duckast gère déjà les AirPods");
            _ownedElsewhere = true;
            RaiseChanged();
            return;
        }
        _ownership = mutex;
        _ownedElsewhere = false;
        _activating = true;
        _running = true;

        try
        {
            bool firstRun = !Prefs.AirPodsFirstRunDone;
            Prefs.AirPodsFirstRunDone = true;
            Log.Write($"AirPods : surveillance démarrée{(firstRun ? " (première activation)" : "")}");

            await AttachDeviceAsync(FindDevice(), allowTakeover: !firstRun);
            if (!_running) return;

            _scanner = new AirPodsScanner();
            _scanner.AdvertReceived += advert => _dispatcher.BeginInvoke(() => OnAdvert(advert));
            _scanner.Start();
            _pollTimer.Start();
            _foregroundTimer.Start();

            if (firstRun)
                NotifyRequested?.Invoke("Duckast", Loc.T(
                    "Windows will no longer connect to your AirPods on its own. Duckast will offer to connect them when it detects them.",
                    "Windows ne se connectera plus seul à tes AirPods. Duckast te proposera de les connecter quand il les détectera."));
        }
        finally
        {
            _activating = false;
            RaiseChanged();
        }
    }

    /// <summary>
    /// À la fermeture de l'app : on laisse l'audio verrouillé (sauf si les
    /// AirPods sont en cours d'utilisation), sans demande UAC.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (!_running) return;
        var device = _device;
        bool lockIt = device != null && _servicesEnabled && !_connected && !_busy;
        StopMonitoring();
        if (!lockIt) return;

        var result = await Task.Run(() => ServiceSwitch.Apply(device!.Address, false, allowElevation: false));
        Log.Write($"AirPods : verrouillage avant fermeture — {result.Message}");
        if (result.Success) Prefs.AirPodsExpectLocked = true;
    }

    private void StopMonitoring()
    {
        if (!_running) return;
        _running = false;
        _pollTimer.Stop();
        _foregroundTimer.Stop();
        _scanner?.Dispose();
        _scanner = null;
        DetachWinRt();
        WithdrawPrompt();

        _device = null;
        _connected = _servicesEnabled = _busy = _present = _promptSuppressed = false;
        _awaitUntil = _disconnectedSince = null;
        _lastAdvert = null;
        _loggedState = null;
        _modelVotes.Clear();
        _lastLid.Clear();

        if (_ownership != null)
        {
            try { _ownership.ReleaseMutex(); } catch { }
            _ownership.Dispose();
            _ownership = null;
        }
        Log.Write("AirPods : surveillance arrêtée");
    }

    public void Dispose()
    {
        if (_disposed) return;
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Loc.Changed -= RaiseChanged;
        StopMonitoring();
        _disposed = true;
    }

    // ===================================================== Appareil géré

    /// <summary>Écouteurs appairés proposables (classe audio : le téléphone n'y figure pas).</summary>
    public IReadOnlyList<PairedDevice> GetAudioDevices()
    {
        try { return BluetoothClassic.GetPairedDevices().Where(d => d.IsAudio).ToList(); }
        catch (Exception ex)
        {
            Log.Write($"AirPods : liste des appareils indisponible — {ex.Message}");
            return Array.Empty<PairedDevice>();
        }
    }

    /// <summary>Change d'appareil géré. L'ancien est rendu à Windows (services recochés).</summary>
    public async Task SelectDeviceAsync(PairedDevice device)
    {
        if (!_running || _busy || _device?.Address == device.Address) return;
        if (_device != null && !_servicesEnabled)
            await SetServicesAsync(true, $"restitution de {_device.Name} à Windows");
        Prefs.AirPodsModel = null;
        _present = false;
        _lastAdvert = null;
        await AttachDeviceAsync(device, allowTakeover: false);
    }

    private PairedDevice? FindDevice()
    {
        List<PairedDevice> devices;
        try { devices = BluetoothClassic.GetPairedDevices(); }
        catch (Exception ex)
        {
            Log.Write($"AirPods : liste des appareils Bluetooth indisponible — {ex.Message}");
            return null;
        }

        if (Prefs.AirPodsAddress is ulong saved && devices.FirstOrDefault(d => d.Address == saved) is PairedDevice known)
            return known;
        return devices.FirstOrDefault(d => d.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="allowTakeover">
    /// Si Windows s'est connecté tout seul (au démarrage), on coupe et on propose
    /// la connexion. Désactivé à la première activation pour ne pas couper des
    /// AirPods en cours d'utilisation.
    /// </param>
    private async Task AttachDeviceAsync(PairedDevice? device, bool allowTakeover)
    {
        DetachWinRt();
        _device = device;
        _connected = false;
        _servicesEnabled = false;
        _awaitUntil = null;
        _modelVotes.Clear();

        if (device == null)
        {
            Log.Write("AirPods : aucun AirPods appairé — en attente d'un appairage depuis les Paramètres Windows");
            RaiseChanged();
            return;
        }

        if (Prefs.AirPodsAddress != device.Address)
        {
            Prefs.AirPodsAddress = device.Address;
            Prefs.AirPodsName = device.Name;
        }

        _connected = device.Connected;
        _servicesEnabled = ServiceSwitch.IsUnlocked(device.Address) ?? false;
        Log.Write($"AirPods : appareil géré {device.Name} [{device.AddressText}] ; lien {(_connected ? "connecté" : "déconnecté")} ; audio {(_servicesEnabled ? "autorisé" : "verrouillé")}");

        try
        {
            var winrt = await BluetoothDevice.FromBluetoothAddressAsync(device.Address);
            if (winrt != null && _running && _device == device)
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
            Log.Write($"AirPods : suivi de connexion WinRT indisponible (le sondage prend le relais) — {ex.Message}");
        }
        if (!_running || _device != device) return;

        if (_servicesEnabled)
        {
            bool justBooted = TimeSpan.FromMilliseconds(Environment.TickCount64) < JustBooted;
            if (!_connected)
            {
                await SetServicesAsync(false, "verrouillage au démarrage");
            }
            else if (allowTakeover && (Prefs.AirPodsExpectLocked || justBooted))
            {
                Log.Write("AirPods : Windows s'est connecté tout seul — on reprend la main");
                if (await SetServicesAsync(false, "connexion automatique de Windows annulée"))
                    ShowPrompt();
            }
            else
            {
                Log.Write("AirPods : en cours d'utilisation sur ce PC — on n'y touche pas pour l'instant");
            }
        }
        RaiseChanged();
    }

    // ========================================================== Boucle de sondage

    private void Poll()
    {
        if (!_running) return;
        var now = DateTime.Now;
        _pollCount++;

        if (_device == null)
        {
            if (_pollCount % 10 == 0 && FindDevice() is PairedDevice found)
                Fire(() => AttachDeviceAsync(found, allowTakeover: false), "Rattachement de l'appareil");
            UpdatePresence(now);
            RaiseChanged();
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
                Log.Write("AirPods : connectés à ce PC");
                if (_awaitShowsProgress)
                    ShowMessage(Loc.T("Connected. Sound now plays through your AirPods.",
                                      "Connectés. Le son passe maintenant par tes AirPods."), AirPodsMessageKind.Success, 3500);
            }
            else if (now > until && !_busy)
            {
                _awaitUntil = null;
                Log.Write("AirPods : Windows ne s'est pas connecté dans le délai — reverrouillage");
                if (_awaitShowsProgress)
                    ShowMessage(Loc.T("Take them out of the case, then try again.",
                                      "Sors-les du boîtier, puis réessaie."), AirPodsMessageKind.Error, 8000);
                Fire(() => SetServicesAsync(false, "échec de connexion"), "Verrouillage");
            }
        }
        else if (_servicesEnabled && !connected && !_busy &&
                 _disconnectedSince is DateTime since && now - since >= AutoLockDelay)
        {
            Fire(() => SetServicesAsync(false, "AirPods déconnectés"), "Verrouillage");
        }

        UpdatePresence(now);
        RaiseChanged();
    }

    private void OnWinRtConnectionChanged(BluetoothDevice sender, object args) =>
        _dispatcher.BeginInvoke(Poll);

    private void OnConnectionChanged(bool connected)
    {
        Log.Write($"AirPods : lien Bluetooth {(connected ? "établi" : "coupé")} (audio {(_servicesEnabled ? "autorisé" : "verrouillé")})");

        if (connected && _servicesEnabled) WithdrawPrompt();

        if (!connected || _servicesEnabled || _busy) return;

        // Lien établi alors que l'audio est verrouillé : soit l'utilisateur vient
        // de cliquer « Connecter » dans Windows, soit c'est une reconnexion
        // automatique. On tranche selon la fenêtre qui était au premier plan.
        if (ForegroundTracker.BluetoothUiActiveWithin(WindowsUiGrace))
        {
            Log.Write("AirPods : connexion lancée depuis l'interface Windows — activation de l'audio");
            Fire(() => ConnectCoreAsync(true, "connexion depuis les Paramètres Windows"), "Connexion depuis Windows");
        }
        else
        {
            Log.Write("AirPods : lien établi sans action dans Windows — ignoré (l'audio reste verrouillé)");
        }
    }

    /// <summary>Détecte une case cochée/décochée à la main dans le Panneau de configuration.</summary>
    private void RefreshServicesState()
    {
        if (_device == null) return;
        if (ServiceSwitch.IsUnlocked(_device.Address) is not bool enabled || enabled == _servicesEnabled) return;

        Log.Write($"AirPods : services audio {(enabled ? "cochés" : "décochés")} en dehors de Duckast");
        _servicesEnabled = enabled;
        if (enabled && !_connected && _awaitUntil == null)
        {
            _awaitUntil = DateTime.Now + ConnectTimeout;
            _awaitShowsProgress = false;
        }
    }

    // =================================================== Détection BLE et proposition

    private void OnAdvert(AirPodsAdvert advert)
    {
        if (!_running || _device == null) return;

        // Apprentissage du modèle : quand les AirPods sont connectés à ce PC,
        // l'annonce forte qui revient le plus est la leur. Ensuite, on ignore
        // les autres modèles (AirPods des voisins).
        if (Prefs.AirPodsModel == null && _connected && _servicesEnabled && advert.Rssi >= LearnRssi)
        {
            int votes = _modelVotes.TryGetValue(advert.Model, out var v) ? v + 1 : 1;
            _modelVotes[advert.Model] = votes;
            if (votes >= LearnVotes)
            {
                Prefs.AirPodsModel = advert.Model;
                Log.Write($"AirPods : modèle appris — {advert.ModelName} (0x{advert.Model:X4})");
            }
        }

        if (Prefs.AirPodsModel is ushort learned && advert.Model != learned) return;
        if (advert.Rssi < RssiThreshold) return;

        var now = DateTime.Now;
        LogAdvertState(advert);

        // Note : le compteur d'ouvertures du couvercle n'est PAS fiable pour
        // détecter une ouverture — chaque écouteur annonce le sien, et leur
        // alternance faisait croire à des ouvertures en rafale (popup en
        // rangeant les AirPods). Seul le passage « rangés → disponibles »
        // relance une proposition.

        // Ouverture du couvercle : détectée PAR écouteur émetteur (bit 0x20 de
        // l'état), car chacun a son propre compteur. Comparer des annonces de
        // deux écouteurs différents donnait de fausses ouvertures ; comparer
        // un écouteur à lui-même est fiable (fermé→ouvert, ou compteur changé).
        bool emitter = (advert.RawStatus & 0x20) != 0;
        bool lidJustOpened = false;
        if (_lastLid.TryGetValue(emitter, out var prev) && !advert.LidClosed &&
            (prev.Closed || prev.Count != advert.LidOpenCount))
            lidJustOpened = true;
        _lastLid[emitter] = (advert.LidClosed, advert.LidOpenCount);

        // Rangés (boîtier fermé, écouteurs dedans) : ils émettent encore, mais
        // ne sont pas disponibles. Une annonce isolée ne suffit pas (les deux
        // écouteurs émettent en alternance) : il faut que plus rien ne dise
        // « disponibles » depuis quelques secondes.
        if (advert.Stowed)
        {
            if (_present && now - _lastAdvertAt > StowedConfirm) SetAbsent("rangés dans le boîtier");
            return;
        }

        bool batteryChanged = _lastAdvert?.BatteryText != advert.BatteryText;
        _lastAdvert = advert;
        _lastAdvertAt = now;
        if (_present)
        {
            if (lidJustOpened)
            {
                // Réouverture alors qu'ils étaient encore tenus pour présents
                // (fermé puis rouvert vite) : on repropose tout de suite.
                Log.Write("AirPods : couvercle rouvert — nouvelle proposition");
                _promptSuppressed = false;
                MaybePrompt(force: true);
            }
            if (batteryChanged) RaiseChanged();
            return;
        }

        _present = true;
        Log.Write($"AirPods : disponibles — {advert.ModelName}, {advert.Rssi} dBm. {advert.BatteryText}");
        MaybePrompt();
        RaiseChanged();
    }

    private void UpdatePresence(DateTime now)
    {
        if (_present && now - _lastAdvertAt > PresenceTimeout) SetAbsent("plus détectés (hors de portée)");
    }

    /// <summary>Plus disponibles : la prochaine sortie du boîtier pourra de nouveau proposer la connexion.</summary>
    private void SetAbsent(string reason)
    {
        _present = false;
        _promptSuppressed = false;
        Log.Write($"AirPods : {reason}");
        WithdrawPrompt();
        RaiseChanged();
    }

    private (bool InCase, bool LidClosed)? _loggedState;

    /// <summary>Trace les changements boîtier/couvercle (octets bruts compris), pour diagnostiquer la détection.</summary>
    private void LogAdvertState(AirPodsAdvert advert)
    {
        var state = (advert.BothInCase, advert.LidClosed);
        if (_loggedState == state) return;
        _loggedState = state;
        Log.Write($"AirPods : annonce — dans le boîtier {(advert.BothInCase ? "oui" : "non")}, couvercle {(advert.LidClosed ? "fermé" : "ouvert")} " +
                  $"(état 0x{advert.RawStatus:X2}, couvercle 0x{advert.RawLid:X2}, {advert.Rssi} dBm)");
    }

    private void MaybePrompt(bool force = false)
    {
        if (!Prefs.AirPodsPrompt || _device == null || _busy || _awaitUntil != null) return;
        if (_promptShown) return;
        // Une ouverture explicite du couvercle passe outre le délai anti-rafale
        // (gardé court pour ne pas doubler la proposition).
        var cooldown = force ? TimeSpan.FromSeconds(1) : PromptCooldown;
        if (_promptSuppressed || DateTime.Now - _lastPromptAt < cooldown) return;

        if (_servicesEnabled && _connected)
        {
            // Déjà connectés : pas de bouton « Connecter », mais on montre la
            // batterie à chaque ouverture du boîtier.
            _lastPromptAt = DateTime.Now;
            Log.Write("AirPods : déjà connectés — affichage de la batterie");
            StatusPeekRequested?.Invoke();
            return;
        }
        ShowPrompt();
    }

    private void ShowPrompt()
    {
        if (_device == null) return;
        _lastPromptAt = DateTime.Now;
        _promptShown = true;
        Log.Write("AirPods : proposition affichée");
        PromptRequested?.Invoke(new AirPodsPrompt(_device.Name, Battery, PromptTimeoutMs));
    }

    private void WithdrawPrompt()
    {
        if (!_promptShown) return;
        _promptShown = false;
        PromptWithdrawn?.Invoke();
    }

    private void ShowMessage(string text, AirPodsMessageKind kind, int autoCloseMs)
    {
        if (_device == null) return;
        _promptShown = false; // le message prend la place de la proposition
        MessageRequested?.Invoke(new AirPodsMessage(_device.Name, text, kind, autoCloseMs, Battery));
    }

    /// <summary>
    /// Croix, clic à l'extérieur ou expiration de la proposition : pas de
    /// nouvelle proposition tant que les AirPods restent à proximité.
    /// </summary>
    public void DismissPrompt()
    {
        _promptShown = false;
        if (_promptSuppressed) return;
        _promptSuppressed = true;
        Log.Write("AirPods : proposition ignorée — pas de rappel tant qu'ils restent à proximité");
    }

    /// <summary>
    /// Action « principale » (clic sur une entrée AirPods dans l'UI) :
    /// propose la connexion, ou rappelle l'état s'ils sont déjà connectés.
    /// </summary>
    public void Offer()
    {
        if (!_running) return;
        if (_device == null)
        {
            NotifyRequested?.Invoke("Duckast", Loc.T(
                "No AirPods paired. Pair them once from the Windows Bluetooth settings.",
                "Aucun AirPods appairé. Appaire-les une première fois depuis les Paramètres Bluetooth de Windows."));
            return;
        }
        if (_servicesEnabled && _connected)
            ShowMessage(Loc.T("Connected to this PC.", "Connectés à ce PC."), AirPodsMessageKind.Success, 4000);
        else if (!_busy)
            ShowPrompt();
    }

    // ============================================== Connexion / déconnexion

    /// <summary>« Connecter » : coche les services, Windows se connecte de lui-même.</summary>
    public Task ConnectAsync() => ConnectCoreAsync(true, "connexion demandée depuis Duckast");

    private async Task ConnectCoreAsync(bool showProgress, string reason)
    {
        var device = _device;
        if (!_running || device == null || _busy) return;
        if (_servicesEnabled && _connected)
        {
            if (showProgress) ShowMessage(Loc.T("Already connected to this PC.", "Déjà connectés à ce PC."), AirPodsMessageKind.Success, 3000);
            return;
        }

        if (showProgress) ShowMessage(Loc.T("Connecting…", "Connexion en cours…"), AirPodsMessageKind.Progress, 0);
        if (!await SetServicesAsync(true, reason))
        {
            if (showProgress)
                ShowMessage(Loc.T("Bluetooth audio couldn't be enabled (see the log).",
                                  "L'audio Bluetooth n'a pas pu être activé (voir le journal)."), AirPodsMessageKind.Error, 8000);
            return;
        }
        // Windows se connecte de lui-même dès que les services sont cochés ; Poll() suit la suite.
        _awaitUntil = DateTime.Now + ConnectTimeout;
        _awaitShowsProgress = showProgress;
    }

    /// <summary>
    /// ■ de la fenêtre « Connexion en cours… » : on cesse d'attendre Windows
    /// et on reverrouille. Sans effet pendant la bascule des services elle-même.
    /// </summary>
    public async Task CancelConnectAsync()
    {
        if (!_running || _device == null || _busy) return;
        _awaitUntil = null;
        WithdrawPrompt();
        if (await SetServicesAsync(false, "connexion annulée"))
        {
            _promptSuppressed = true;
            ShowMessage(Loc.T("Connection cancelled.", "Connexion annulée."), AirPodsMessageKind.Info, 2500);
        }
    }

    /// <summary>Bascule des services en cours (rien ne peut l'interrompre).</summary>
    public bool IsBusy => _busy;

    /// <summary>Une connexion est en cours : bascule des services, ou attente que Windows se connecte.</summary>
    public bool IsConnecting => _running && (_busy ? _busyEnabling : _servicesEnabled && !_connected);

    /// <summary>« Déconnecter » : décoche les services, Windows ne s'y reconnectera pas seul.</summary>
    public async Task DisconnectAsync()
    {
        var device = _device;
        if (!_running || device == null || _busy) return;
        _awaitUntil = null;
        WithdrawPrompt();
        if (await SetServicesAsync(false, "déconnexion demandée"))
        {
            _promptSuppressed = true; // pas de proposition immédiate juste après avoir déconnecté
            ShowMessage(Loc.T("Disconnected. Windows won't reconnect to them on its own.",
                              "Déconnectés. Windows ne s'y reconnectera pas seul."), AirPodsMessageKind.Info, 3000);
        }
    }

    /// <summary>Coche (enable) ou décoche les services audio, en arrière-plan.</summary>
    private async Task<bool> SetServicesAsync(bool enable, string reason)
    {
        var device = _device;
        if (device == null) return false;

        _busy = true;
        _busyEnabling = enable;
        RaiseChanged();
        try
        {
            Log.Write($"AirPods : {(enable ? "activation" : "désactivation")} de l'audio ({reason})…");
            var result = await Task.Run(() => ServiceSwitch.Apply(device.Address, enable, allowElevation: true));
            Log.Write($"AirPods : → {result.Message}");

            _servicesEnabled = ServiceSwitch.IsUnlocked(device.Address) ?? (result.Success ? enable : _servicesEnabled);
            if (result.Success)
                Prefs.AirPodsExpectLocked = !enable;
            else
                NotifyRequested?.Invoke("Duckast", enable
                    ? Loc.T($"Couldn't enable Bluetooth audio: {result.Message}", $"Impossible d'activer l'audio Bluetooth : {result.Message}")
                    : Loc.T($"Couldn't disable Bluetooth audio: {result.Message}", $"Impossible de désactiver l'audio Bluetooth : {result.Message}"));
            return result.Success;
        }
        finally
        {
            _busy = false;
            _disconnectedSince = null;
            RaiseChanged();
        }
    }

    // ======================================================== Événements système

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) =>
        RunOnUiNow(() => LockSynchronously("arrêt de Windows / fermeture de session"));

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            RunOnUiNow(() => LockSynchronously("mise en veille"));
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_running) return;
                Log.Write("AirPods : sortie de veille");
                _present = false;
                _promptSuppressed = false;
            });
        }
    }

    /// <summary>
    /// Verrouillage immédiat (pas d'UAC possible à l'arrêt) pour que Windows
    /// ne se reconnecte pas au prochain démarrage.
    /// </summary>
    private void LockSynchronously(string reason)
    {
        if (!_running) return;
        _awaitUntil = null;
        Prefs.AirPodsExpectLocked = true;
        if (_device == null || !_servicesEnabled) return;

        var result = ServiceSwitch.Apply(_device.Address, false, allowElevation: false);
        Log.Write($"AirPods : verrouillage ({reason}) — {result.Message}");
        if (result.Success) _servicesEnabled = false;
    }

    /// <summary>
    /// SystemEvents lève ses événements sur son propre thread ; l'arrêt et la
    /// veille n'attendent pas : on exécute sur l'UI de façon synchrone (borné).
    /// </summary>
    private void RunOnUiNow(Action action)
    {
        try
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.Invoke(action, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Write($"AirPods : verrouillage impossible — {ex.Message}");
        }
    }

    // ================================================================ Utilitaires

    private void DetachWinRt()
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

    private string _lastChangeKey = "";

    /// <summary>Le sondage tourne chaque seconde : on ne prévient l'UI que si ce qu'elle affiche a changé.</summary>
    private void RaiseChanged()
    {
        if (_disposed) return;
        string key = $"{State}|{StatusText}|{BatteryText}|{DeviceName}|{CanConnect}|{CanDisconnect}|{Prefs.AirPodsPrompt}|{_ownedElsewhere}";
        if (key == _lastChangeKey) return;
        _lastChangeKey = key;
        Changed?.Invoke();
    }

    private static async void Fire(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception ex) { Log.Write($"AirPods : {what} — {ex}"); }
    }
}
