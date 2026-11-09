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

/// <summary>Entrée du choix de sortie audio : null = sortie par défaut.</summary>
public class OutputChoice
{
    public string? Id { get; init; }
    public string Name { get; init; } = "";

    // La zone fermée de la liste déroulante affiche l'élément tel quel
    // (DisplayMemberPath ne vaut que pour les lignes de la liste).
    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<BtDevice> _devices = new();
    private readonly Dictionary<string, AudioPlaybackConnection> _connections = new();
    private readonly NetworkReceiver _net = new();
    private readonly NetworkSender _sender = new();
    private readonly TrayIcon _tray = new();
    private readonly AirPodsGate _airPods;
    private readonly AirPodsPopup _airPodsPopup;
    private DeviceWatcher? _watcher;
    private DispatcherTimer? _retryTimer;
    private DispatcherTimer? _pollTimer;
    private string? _activeId;
    private bool _quitting;
    private bool _trayHintShown;
    private bool _syncingVolume;
    private bool _syncingOutput;
    private VolumeKeyHook? _volumeKeys;
    private VolumeOsd? _osd;
    private DispatcherTimer? _vuTimer;
    private float _vuShown;

    // Niveaux G / D / B sous le statut de la tuile AirPods.
    private readonly BatteryBars _airPodsBars = new() { Margin = new Thickness(0, 5, 0, 0) };

    public MainWindow()
    {
        InitializeComponent();

        // Même ordre que DuckLogo.Draw : bec et aile dessous, corps par-dessus.
        LogoBeak.Data = Geometry.Parse(DuckLogo.BeakPath);
        LogoWing.Data = Geometry.Parse(DuckLogo.WingPath);
        LogoBody.Data = Geometry.Parse(DuckLogo.BodyPath);
        LogoBox.RenderTransform = new TransformGroup { Children = { _logoPop, _logoTilt, _logoHop } };
        LogoHost.MouseEnter += (_, _) => WaddleLogo();

        AttachSlidingPill(BalanceSegments);
        // Le contenu déplié pousse le bas de la page : on le suit, pour que
        // ce qui apparaît reste en vue pendant toute l'ouverture.
        AdvancedHost.SizeChanged += (_, _) => { if (_advancedOpen) PageScroll.ScrollToEnd(); };
        // Fond de l'engrenage : une brosse à nous, allumée en fondu dans les Réglages.
        SettingsButton.Background = _settingsBrush;

        // Les anciennes versions passaient par un câble virtuel ; si l'une
        // d'elles a été tuée brutalement, ses redirections ont survécu dans le
        // registre et détourneraient encore du son. On nettoie au démarrage.
        PurgeStaleCablePins();

        // Tactile : en butée de défilement, WPF fait « rebondir » toute la
        // fenêtre — on garde le panneau immobile.
        ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
        // Un geste qui commence sur un curseur (volume…) le pilote : pas de
        // défilement, le toucher redevient un clic-glisser.
        PageScroll.ManipulationStarting += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != PageScroll;
                 d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.Primitives.RangeBase)
                {
                    e.Cancel();
                    return;
                }
            }
        };

        WireTiles();

        _tray.ShowRequested += () => Dispatcher.Invoke(ToggleFlyout);
        _tray.IconClicked += () => Dispatcher.Invoke(OnTrayIconClick);
        _tray.AirPodsPopupRequested += () => Dispatcher.Invoke(OnTrayIconDoubleClick);
        _trayClickDelay.Tick += (_, _) =>
        {
            _trayClickDelay.Stop();
            ShowFlyout();
        };
        _tray.QuitRequested += () => Dispatcher.Invoke(Quit);
        _tray.BalanceRequested += (l, r) => Dispatcher.Invoke(() => SetBalanceFromTray(l, r));

        // Comportement « flyout » : la fenêtre se retire dès qu'on clique ailleurs.
        Deactivated += (_, _) => { if (IsVisible && !_quitting) HideFlyout(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideFlyout(); };

        // La hauteur suit le contenu : rester ancré en bas à droite quand
        // il change (tuile AirPods qui apparaît…).
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
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // AirPods (ex-PodGate) : Windows ne s'y connecte plus seul, on propose
        // la connexion quand ils sont détectés. Opt-in (Prefs.AirPodsEnabled).
        _airPods = new AirPodsGate(Dispatcher);
        _airPodsPopup = new AirPodsPopup { Anchor = this };
        _airPodsPopup.ConnectClicked += () => _ = _airPods.ConnectAsync();
        _airPodsPopup.CancelClicked += () => _ = _airPods.CancelConnectAsync();
        _airPodsPopup.UnlinkClicked += () => _ = _airPods.DisconnectAsync();
        _airPodsPopup.Dismissed += () => _airPods.DismissPrompt();
        _tray.AirPodsRequested += () => Dispatcher.Invoke(ToggleAirPodsConnection);
        _airPods.NotifyRequested += (title, text) => _tray.Notify(title, text);
        _airPods.Changed += OnAirPodsChanged;
        _airPods.PromptRequested += OnAirPodsPrompt;
        _airPods.PromptWithdrawn += OnAirPodsPromptWithdrawn;
        _airPods.MessageRequested += OnAirPodsMessage;
        _airPods.StatusPeekRequested += OnAirPodsStatusPeek;

        // Après l'AirPodsGate : la langue rafraîchit toutes les tuiles.
        Loc.Changed += ApplyLanguage;
        ApplyLanguage();
        _airPods.Start();

        // Vumètre : rafraîchi seulement quand le flyout est visible — l'app
        // passe sa vie cachée, pas de réveil 20 fois par seconde pour rien.
        // L'aurore est clippée aux coins arrondis de la tuile (rayon 18) : un
        // Border ne rogne pas ses enfants sur les angles, on pose la
        // géométrie à la main à chaque changement de taille.
        VuClip.SizeChanged += (_, _) => VuClip.Clip = new RectangleGeometry(
            new Rect(0, 0, VuClip.ActualWidth, VuClip.ActualHeight), 18, 18);
        CreateVuBlobs();

        _vuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _vuTimer.Tick += (_, _) => UpdateVu();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _vuTimer.Start();
            else { _vuTimer.Stop(); _vuShown = 0; }
        };

        // Repère sonore du volume : au relâchement de la souris, sinon dès que
        // la valeur ne bouge plus (clavier, molette). Tant que le bouton est
        // enfoncé, le minuteur patiente — il rattrape aussi un relâchement
        // hors de la barre.
        VolumeSlider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_toneTimer.IsEnabled) PlayVolumeTone();
        };
        _toneTimer.Tick += (_, _) =>
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) PlayVolumeTone();
        };

        // Suit la session du téléphone : volume synchronisé, et balance
        // réappliquée automatiquement après chaque reconnexion.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => PollPhoneSession();
        _pollTimer.Start();

        // Mise à jour automatique en arrière-plan : si une nouvelle release
        // existe, l'installateur silencieux remplace l'app et la relance.
        CheckUpdatesAtLaunch();

        Loaded += (_, _) => AnimateIn();
    }

    private async void CheckUpdatesAtLaunch()
    {
        await Updater.CheckAndUpdateAsync(msg =>
            Dispatcher.Invoke(() => _tray.Notify("Duckast", msg)));
        UpdateUpdateTile();
    }

    /// <summary>Gestes des tuiles : toucher = bascule (allume / éteint).</summary>
    private void WireTiles()
    {
        PhoneTile.Click += OnPhoneTileClick;
        PhoneTile.MouseRightButtonUp += (_, e) => e.Handled = ShowPhoneMenu();
        NetworkTile.Click += ToggleNetworkReceive;
        SendTile.Click += () => SetSending(!_sendWanted);
        AirPodsTile.Footer = _airPodsBars;
        AirPodsTile.Click += OnAirPodsTileClick;

        AutostartTile.Click += ToggleAutostart;
        DarkModeTile.Click += ToggleDarkMode;
        LanguageTile.Click += ToggleLanguage;
        UpdateTile.Click += CheckUpdate;
        AirPodsSettingsTile.Click += ToggleAirPodsManagement;

        VolumeSlider.IsEnabledChanged += (_, _) => UpdateVolumeReadout();
        VolumeSlider.SizeChanged += (_, _) => UpdateVolumeReadout();
    }

    /// <summary>
    /// Toute la ligne d'un interrupteur le bascule, pas seulement la pastille
    /// (un clic sur l'interrupteur lui-même ne remonte pas jusqu'ici).
    /// </summary>
    private void ToggleRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Panel row) return;
        var toggle = row.Children.OfType<System.Windows.Controls.CheckBox>().FirstOrDefault();
        if (toggle is { IsEnabled: true }) toggle.IsChecked = toggle.IsChecked != true;
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

    private enum AppPage { Main, Settings, Diag }
    private AppPage _page = AppPage.Main;

    /// <summary>Bouton en haut à droite : engrenage, croix ou retour selon la page.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case AppPage.Main: NavigateTo(AppPage.Settings, forward: true); break;
            case AppPage.Settings: NavigateTo(AppPage.Main, forward: false); break;
            case AppPage.Diag: NavigateTo(AppPage.Settings, forward: false); break;
        }
    }

    private bool _advancedOpen;

    private void AdvancedNav_Click(object sender, RoutedEventArgs e) =>
        SetAdvancedOpen(!_advancedOpen, animate: true);

    /// <summary>
    /// Déplie ou replie les réglages avancés sous leur ligne. La fenêtre ne
    /// bouge pas, c'est la page qui défile (PageScroll).
    /// </summary>
    private void SetAdvancedOpen(bool open, bool animate)
    {
        _advancedOpen = open;
        AdvancedNavButton.Tag = open ? "open" : null; // chevron, voir le style ExpandRow
        if (open)
        {
            PopulateOutputCombo();
            PopulateDestCombo();
            _syncingOpus = true;
            OpusToggle.IsChecked = Prefs.OpusEnabled;
            _syncingOpus = false;
        }
        Reveal(AdvancedHost, AdvancedContent, open, animate);
        // Le résumé s'efface quand le détail est sous les yeux.
        AdvancedSummary.BeginAnimation(OpacityProperty, null);
        if (animate) AdvancedSummary.BeginAnimation(OpacityProperty, Palette.Anim(open ? 0 : 1, 220));
        else AdvancedSummary.Opacity = open ? 0 : 1;
        if (open && animate) Cascade(AdvancedContent, step: 28, rise: 8);
    }

    /// <summary>
    /// Déplie ou replie un bloc sur place : la hauteur de <paramref name="host"/>
    /// va de 0 à celle de <paramref name="content"/> (mesurée à part : dans un
    /// hôte de hauteur 0, elle serait rabotée), avec un fondu.
    /// </summary>
    private static void Reveal(FrameworkElement host, FrameworkElement content, bool open, bool animate)
    {
        host.IsHitTestVisible = open;
        double height = 0;
        if (open)
        {
            double width = host.ActualWidth > 0 ? host.ActualWidth : double.PositiveInfinity;
            content.Measure(new System.Windows.Size(width, double.PositiveInfinity));
            height = content.DesiredSize.Height;
        }
        if (!animate)
        {
            host.BeginAnimation(HeightProperty, null);
            host.BeginAnimation(OpacityProperty, null);
            host.Height = height;
            host.Opacity = open ? 1 : 0;
            return;
        }
        host.BeginAnimation(HeightProperty,
            new DoubleAnimation(height, TimeSpan.FromMilliseconds(380))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        host.BeginAnimation(OpacityProperty,
            new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 340 : 180)));
    }

    private void DiagNav_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(AppPage.Diag, forward: true);

    /// <summary>
    /// Applique la langue à tous les textes fixes, puis régénère les états
    /// (tuiles, en-têtes dynamiques).
    /// </summary>
    private void ApplyLanguage()
    {
        GeneralHeader.Text = Loc.T("GENERAL", "GÉNÉRAL");
        OutputLabel.Text = Loc.T("Received audio output", "Sortie du son reçu");
        OutputRow.ToolTip = Loc.T(
            "Applies to audio received from the network. Bluetooth phone audio always follows Windows' default output.",
            "S'applique au son reçu par le réseau. Le son Bluetooth du téléphone suit toujours la sortie par défaut de Windows.");
        DestLabel.Text = Loc.T("Send to", "Envoyer vers");
        DestRow.ToolTip = Loc.T(
            "Which Duckast receives this PC's audio. Automatic = the first one found.",
            "Quel Duckast reçoit le son de ce PC. Automatique = le premier découvert.");
        OpusLabel.Text = Loc.T("Opus compression (Wi-Fi)", "Compression Opus (Wi-Fi)");
        OpusRow.ToolTip = Loc.T(
            "Compresses the sent stream to 256 kbit/s (transparent) — 6x less bandwidth, useful on busy Wi-Fi.",
            "Compresse le flux émis à 256 kbit/s (transparent) — 6x moins de bande passante, utile en Wi-Fi chargé.");
        OpusToggle.Content = Loc.T("On", "Activée");
        OpusToggle.Tag = Loc.T("Off", "Désactivée");
        AdvancedNavLabel.Text = Loc.T("Advanced", "Avancé");
        AdvancedSummary.Text = Loc.T("Output, destination, Opus, diagnostics",
                                     "Sortie, destination, Opus, diagnostics");
        DiagNavLabel.Text = Loc.T("Diagnostics", "Diagnostic");
        AirPodsDeviceLabel.Text = Loc.T("Device", "Appareil");
        AirPodsPromptLabel.Text = Loc.T("Offer when the case opens", "Proposer à l'ouverture du boîtier");
        AirPodsPromptToggle.Content = Loc.T("On", "Activé");
        AirPodsPromptToggle.Tag = Loc.T("Off", "Désactivé");
        AirPodsPreviewLabel.Text = Loc.T("Window preview", "Aperçu de la fenêtre");
        AirPodsPreviewButton.ToolTip = Loc.T("Show the window", "Afficher la fenêtre");
        DiagStreamHeader.Text = Loc.T("STREAM", "FLUX");
        DiagLogHeader.Text = Loc.T("LOG", "JOURNAL");
        DiagLatencyLabel.Text = Loc.T("Latency", "Latence");
        DiagRateLabel.Text = Loc.T("Bitrate", "Débit");
        if (_advancedOpen)
        {
            PopulateOutputCombo();
            PopulateDestCombo();
        }

        BalanceLeft.Content = Loc.T("Left", "Gauche");
        BalanceStereo.Content = Loc.T("Stereo", "Stéréo");
        BalanceRight.Content = Loc.T("Right", "Droite");
        BalanceCard.ToolTip = Loc.T(
            "Only affects the received audio (phone or other PC) — never this PC's own sounds.",
            "N'agit que sur le son reçu (téléphone ou autre PC) — jamais sur les sons de ce PC.");

        TitleText.Text = TitleFor(_page);
        SettingsButton.ToolTip = _page == AppPage.Main ? Loc.T("Settings", "Réglages") : Loc.T("Back", "Retour");
        _airPodsBars.ApplyLanguage();
        UpdateSettingsTiles();
        SyncAirPodsSettings(repopulateDevices: false);
        UpdatePhoneTile();
        UpdateNetworkUi();
        UpdateSendUi();
        UpdateAirPodsUi();
        UpdateVolumeReadout();
        _tray.ApplyLanguage();
    }

    private void ToggleLanguage() => Loc.SetFrench(!Loc.French);

    /// <summary>Toucher la tuile réseau : pause si elle reçoit, reprise sinon.</summary>
    private void ToggleNetworkReceive()
    {
        if (_net.IsReceiving) _net.PauseCurrentSender();
        else _net.ResumeSender();
    }

    private UIElement PanelFor(AppPage page) => page switch
    {
        AppPage.Main => MainPanel,
        AppPage.Settings => SettingsPanel,
        _ => DiagPanel,
    };

    private string TitleFor(AppPage page) => page switch
    {
        AppPage.Main => "Duckast",
        AppPage.Settings => Loc.T("Settings", "Réglages"),
        _ => Loc.T("Diagnostics", "Diagnostic"),
    };

    private static readonly System.Windows.Media.Color HeaderIdle = System.Windows.Media.Color.FromArgb(0x3D, 0x78, 0x78, 0x80);
    private static readonly System.Windows.Media.Color HeaderLit = System.Windows.Media.Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
    private readonly SolidColorBrush _settingsBrush = new(HeaderIdle);

    /// <summary>
    /// Bascule de page avec transition : la page quittée s'efface en glissant
    /// pendant que la nouvelle arrive depuis la droite (navigation avant) ou
    /// la gauche (retour), ses blocs en cascade ; le titre glisse, le logo
    /// rebondit, l'engrenage tourne et s'allume hors de l'accueil.
    ///
    /// Toutes les pages ont la taille de l'accueil : en la quittant, la zone
    /// défilante est figée à sa hauteur du moment, ce qui dépasse défile.
    /// <paramref name="animate"/> à false : bascule sèche, pour un changement
    /// fait fenêtre cachée.
    /// </summary>
    private void NavigateTo(AppPage target, bool forward, bool animate = true)
    {
        if (target == _page) return;

        if (_page == AppPage.Main) PageScroll.Height = PageScroll.ActualHeight;
        // Au retour, la hauteur n'est libérée qu'une fois la page quittée
        // effacée (voir AnimatePanelOut) : plus haute, elle agrandirait le
        // panneau le temps du fondu.
        if (target == AppPage.Main && !animate) PageScroll.Height = double.NaN;

        if (target == AppPage.Settings && _advancedOpen)
        {
            // Retour du diagnostic : on retrouve la section dépliée, en bas.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PageScroll.ScrollToEnd));
        }
        else
        {
            PageScroll.ScrollToTop();
        }

        if (target == AppPage.Settings)
        {
            // Les Réglages s'ouvrent toujours repliés.
            if (_page == AppPage.Main) SetAdvancedOpen(false, animate: false);
            UpdateSettingsTiles();
            SyncAirPodsSettings(repopulateDevices: true);
        }
        else if (target == AppPage.Diag)
        {
            UpdateDiagnostics();
        }

        var leaving = PanelFor(_page);
        var arriving = PanelFor(target);
        _page = target;
        bool main = target == AppPage.Main;

        if (animate)
        {
            AnimatePanelOut(leaving, toLeft: forward);
            AnimatePanelIn(arriving, fromRight: forward);

            TitleText.Text = TitleFor(target);
            TitleText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
            TitleSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(forward ? 16 : -16, 0, TimeSpan.FromMilliseconds(380))
                { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            Pop(_logoPop, 0.8, 380);
            // L'engrenage tourne d'un tiers de tour et s'allume hors de l'accueil.
            SettingsSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(forward ? -120 : 120, 0, TimeSpan.FromMilliseconds(560))
                { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 } });
            Palette.ColorTo(_settingsBrush, main ? HeaderIdle : HeaderLit, true, 320);
        }
        else
        {
            SnapPanel(leaving, visible: false);
            SnapPanel(arriving, visible: true);
            TitleText.Text = TitleFor(target);
            Palette.ColorTo(_settingsBrush, main ? HeaderIdle : HeaderLit, false);
        }

        // Engrenage partout (allumé dans les Réglages), flèche retour au diagnostic.
        SettingsButton.Content = target == AppPage.Diag ? "\uE72B" : "\uE713";
        SettingsButton.ToolTip = main ? Loc.T("Settings", "Réglages") : Loc.T("Back", "Retour");
    }

    private static TranslateTransform SlideOf(UIElement panel) => MotionOf(panel).Shift;

    /// <summary>
    /// Transformations de mouvement d'un bloc (échelle puis translation),
    /// créées à la première demande et réutilisées ensuite.
    /// </summary>
    private static (TranslateTransform Shift, ScaleTransform Scale) MotionOf(UIElement element)
    {
        if (element.RenderTransform is TransformGroup group && group.Children.Count == 2
            && group.Children[0] is ScaleTransform s && group.Children[1] is TranslateTransform t)
            return (t, s);
        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, shift } };
        return (shift, scale);
    }

    /// <summary>
    /// Arrivée d'une page : long glissement amorti, et un fondu qui démarre
    /// doucement — la page quittée a le temps de s'effacer, les deux textes
    /// ne se lisent jamais l'un sur l'autre.
    /// </summary>
    private static void AnimatePanelIn(UIElement panel, bool fromRight)
    {
        panel.Visibility = Visibility.Visible;
        panel.IsHitTestVisible = true;
        SlideOf(panel).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromRight ? 44 : -44, 0, TimeSpan.FromMilliseconds(380))
            { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        Cascade(panel);
    }

    /// <summary>
    /// Entrée en cascade : chaque bloc de la page (en-tête, carte…) monte et
    /// apparaît un instant après le précédent ; dans une grille de tuiles
    /// (Tag="cascade"), chaque tuile entre à son tour en se posant. Images
    /// clés plutôt que BeginTime : avant son départ, une animation différée
    /// laisserait voir le bloc à pleine opacité.
    /// </summary>
    private static void Cascade(UIElement page, int step = 36, double rise = 14)
    {
        if (page is not System.Windows.Controls.Panel panel) return;
        int rank = 0;
        foreach (UIElement block in panel.Children)
        {
            if (block.Visibility != Visibility.Visible) continue;
            if (block is System.Windows.Controls.Grid { Tag: "cascade" } grid)
            {
                foreach (UIElement tile in grid.Children)
                {
                    if (tile.Visibility == Visibility.Visible) Enter(tile, rank++, step, rise, pop: true);
                }
                continue;
            }
            Enter(block, rank++, step, rise, pop: false);
        }
    }

    /// <summary>Entrée d'un bloc, après <paramref name="rank"/> pas de <paramref name="step"/> ms.</summary>
    private static void Enter(UIElement block, int rank, int step, double rise, bool pop)
    {
        var start = TimeSpan.FromMilliseconds(step * rank);
        var end = start + TimeSpan.FromMilliseconds(pop ? 460 : 300);

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.Zero));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, start));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, start + TimeSpan.FromMilliseconds(240)));
        block.BeginAnimation(OpacityProperty, fade);

        var (shift, scale) = MotionOf(block);
        var up = new DoubleAnimationUsingKeyFrames();
        up.KeyFrames.Add(new DiscreteDoubleKeyFrame(rise, TimeSpan.Zero));
        up.KeyFrames.Add(new DiscreteDoubleKeyFrame(rise, start));
        up.KeyFrames.Add(new EasingDoubleKeyFrame(0, end, new CubicEase { EasingMode = EasingMode.EaseOut }));
        shift.BeginAnimation(TranslateTransform.YProperty, up);

        if (!pop) return;
        // Les tuiles se posent : légèrement plus petites, elles prennent
        // leur taille avec un soupçon de rebond.
        var grow = new DoubleAnimationUsingKeyFrames();
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.92, TimeSpan.Zero));
        grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.92, start));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, end,
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>Petit rebond d'échelle, pour marquer un changement (pictogramme, logo).</summary>
    private static void Pop(ScaleTransform scale, double from, int ms = 320)
    {
        var bounce = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    private readonly RotateTransform _logoTilt = new();
    private readonly TranslateTransform _logoHop = new();
    private bool _waddling;

    /// <summary>
    /// Le canard se dandine tant que le pointeur est dessus : il penche d'un
    /// côté puis de l'autre, avec un petit saut à chaque bascule. Quand le
    /// pointeur s'en va, la bascule en cours se termine et il se redresse —
    /// jamais de coupure nette, et repasser dessus ne le fait pas bégayer.
    /// </summary>
    private void WaddleLogo()
    {
        if (_waddling) return;
        _waddling = true;
        SwingLogo(-WaddleAngle);
    }

    private const double WaddleAngle = 12;

    // Le flyout peut se cacher (Échap) pointeur encore dessus : IsMouseOver
    // reste alors vrai, d'où le test de visibilité pour ne pas boucler caché.
    private bool LogoHovered => IsVisible && LogoHost.IsMouseOver;

    /// <summary>Une bascule vers <paramref name="angle"/> (0 = se redresser), puis la suivante.</summary>
    private void SwingLogo(double angle)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        // Une bascule complète va d'un bord à l'autre ; partir du centre ou
        // y revenir ne fait que la moitié du chemin.
        bool full = angle != 0 && _logoTilt.Angle != 0;
        var duration = TimeSpan.FromMilliseconds(full ? 260 : 150);

        var tilt = new DoubleAnimation(angle, duration) { EasingFunction = ease };
        tilt.Completed += (_, _) =>
        {
            if (angle != 0) SwingLogo(LogoHovered ? -angle : 0);
            // Redressé : le pointeur a pu revenir entre-temps sans MouseEnter.
            else if (LogoHovered) SwingLogo(-WaddleAngle);
            else _waddling = false;
        };
        _logoTilt.BeginAnimation(RotateTransform.AngleProperty, tilt);

        if (!full) return;
        var hop = new DoubleAnimationUsingKeyFrames();
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-3, TimeSpan.FromMilliseconds(130), ease));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, duration, ease));
        _logoHop.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    private readonly ScaleTransform _logoPop = new();
    private readonly ScaleTransform _mutePop = new();

    /// <summary>
    /// Sélecteur segmenté : une seule pastille, glissée derrière les segments,
    /// qui coulisse vers celui qu'on coche (les segments eux-mêmes n'en ont
    /// pas, voir le style Segment). Recalée sans animation quand la taille
    /// change — notamment au premier affichage.
    /// </summary>
    private void AttachSlidingPill(System.Windows.Controls.Grid segments)
    {
        var buttons = segments.Children.OfType<System.Windows.Controls.RadioButton>().ToList();
        var slide = new TranslateTransform();
        var pill = new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(7),
            Background = (System.Windows.Media.Brush)FindResource("SegmentActive"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(2),
            RenderTransform = slide,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        System.Windows.Controls.Grid.SetColumnSpan(pill, buttons.Count);
        segments.Children.Insert(0, pill);

        void Move(bool animate)
        {
            int index = buttons.FindIndex(b => b.IsChecked == true);
            double step = segments.ActualWidth / buttons.Count;
            if (index < 0 || step <= 4)
            {
                pill.Opacity = 0;
                return;
            }
            // Pas encore posée : elle n'a pas d'endroit d'où glisser.
            if (pill.Opacity == 0) animate = false;
            pill.Opacity = 1;
            pill.Width = step - 4;
            if (animate)
            {
                slide.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(index * step, TimeSpan.FromMilliseconds(280))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else
            {
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                slide.X = index * step;
            }
        }

        segments.SizeChanged += (_, _) => Move(animate: false);
        foreach (var button in buttons) button.Checked += (_, _) => Move(animate: true);
    }

    /// <summary>Départ d'une page : bref fondu en glissant du côté opposé.</summary>
    private void AnimatePanelOut(UIElement panel, bool toLeft)
    {
        panel.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        // Si on est revenu sur cette page entre-temps, elle reste affichée.
        fade.Completed += (_, _) =>
        {
            if (PanelFor(_page) != panel) panel.Visibility = Visibility.Collapsed;
            if (_page == AppPage.Main) PageScroll.Height = double.NaN;
        };
        SlideOf(panel).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(toLeft ? -30 : 30, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        panel.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Place une page dans son état final, sans animation.</summary>
    private static void SnapPanel(UIElement panel, bool visible)
    {
        SlideOf(panel).BeginAnimation(TranslateTransform.XProperty, null);
        panel.BeginAnimation(OpacityProperty, null);
        panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        panel.IsHitTestVisible = visible;
    }

    /// <summary>Change un texte d'état avec un court fondu, s'il change vraiment.</summary>
    private static void SetTextFaded(System.Windows.Controls.TextBlock block, string text)
    {
        if (block.Text == text) return;
        block.Text = text;
        block.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    // Là où le Gestionnaire des tâches (onglet Démarrage) et les Paramètres
    // de Windows notent qu'une entrée Run est désactivée : premier octet
    // impair = désactivée, l'entrée Run restant en place.
    private const string RunApprovedPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Tuiles « Général » des Réglages, d'après l'état réel.</summary>
    private void UpdateSettingsTiles()
    {
        string on = Loc.T("On", "Activé"), off = Loc.T("Off", "Désactivé");
        bool auto = IsAutostartEnabled();
        AutostartTile.Title = Loc.T("Launch with Windows", "Lancer avec Windows");
        AutostartTile.SetState(auto ? TileTint.Green : TileTint.Glass, auto ? on : off);
        bool dark = Prefs.DarkMode;
        DarkModeTile.Title = Loc.T("Dark mode", "Mode sombre");
        DarkModeTile.SetState(dark ? TileTint.Green : TileTint.Glass, dark ? on : off);
        LanguageTile.Title = Loc.T("Language", "Langue");
        LanguageTile.SetState(TileTint.Glass, Loc.French ? "Français" : "English");
        UpdateUpdateTile();
    }

    private void ToggleDarkMode()
    {
        Prefs.DarkMode = !Prefs.DarkMode;
        WindowEffects.ApplyVeil(this);
        UpdateSettingsTiles();
    }

    private void ToggleAutostart()
    {
        SetAutostart(!IsAutostartEnabled());
        UpdateSettingsTiles();
    }

    private static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(RunValueName) == null) return false;
            using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunApprovedPath);
            return approved?.GetValue(RunValueName) is not byte[] { Length: > 0 } state
                || (state[0] & 1) == 0;
        }
        catch { return false; }
    }

    private static void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            using var approved = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunApprovedPath);
            if (enabled)
            {
                string exe = Environment.ProcessPath
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "AudioShare.exe");
                key.SetValue(RunValueName, $"\"{exe}\"");
                // Lève une désactivation faite depuis Windows, sinon l'entrée
                // existerait sans que l'app démarre.
                approved.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                // L'installateur réécrit l'entrée Run à chaque mise à jour si
                // la case avait été cochée à l'installation : la marque
                // « désactivée » fait tenir le choix malgré tout.
                var state = new byte[12];
                state[0] = 3;
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(state, 4);
                approved.SetValue(RunValueName, state, Microsoft.Win32.RegistryValueKind.Binary);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Démarrage automatique : modification impossible ({ex.Message})");
        }
    }

    /// <summary>
    /// Sorties actives + « Périphérique par défaut » en tête. Relu à chaque
    /// ouverture des Réglages : la liste suit les branchements sans watcher.
    /// </summary>
    private void PopulateOutputCombo()
    {
        _syncingOutput = true;
        var items = new List<OutputChoice>
        {
            new() { Id = null, Name = Loc.T("Default device", "Périphérique par défaut") },
        };
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
            {
                items.Add(new OutputChoice { Id = device.ID, Name = device.FriendlyName });
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Réglages : énumération des sorties impossible ({ex.Message})");
        }
        OutputCombo.ItemsSource = items;
        var saved = Prefs.OutputDeviceId;
        OutputCombo.SelectedItem = items.FirstOrDefault(i => i.Id == saved) ?? items[0];
        _syncingOutput = false;
    }

    private void Output_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _syncingOutput) return;
        if (OutputCombo.SelectedItem is not OutputChoice choice) return;
        Prefs.OutputDeviceId = choice.Id;
        // Prend effet immédiatement si un flux réseau joue.
        _net.ApplyOutputDevice();
    }

    // ---------- Réseau (destination, compression) ----------

    private bool _syncingDest, _syncingOpus;

    /// <summary>
    /// « Automatique » + la destination mémorisée tout de suite, puis les
    /// Duckast réellement découverts (sonde d'une seconde) en différé.
    /// </summary>
    private async void PopulateDestCombo()
    {
        var saved = Prefs.SendTarget;
        var items = new List<OutputChoice> { new() { Id = null, Name = Loc.T("Automatic", "Automatique") } };
        if (!string.IsNullOrEmpty(saved)) items.Add(new OutputChoice { Id = saved, Name = saved });

        _syncingDest = true;
        DestCombo.ItemsSource = items;
        DestCombo.SelectedItem = items.FirstOrDefault(i => i.Id == saved) ?? items[0];
        _syncingDest = false;

        var found = await NetworkSender.DiscoverAllAsync();
        foreach (var name in found)
        {
            if (!items.Any(i => string.Equals(i.Id, name, StringComparison.OrdinalIgnoreCase)))
                items.Add(new OutputChoice { Id = name, Name = name });
        }

        _syncingDest = true;
        DestCombo.ItemsSource = null;
        DestCombo.ItemsSource = items;
        DestCombo.SelectedItem = items.FirstOrDefault(
            i => string.Equals(i.Id, Prefs.SendTarget, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        _syncingDest = false;
    }

    private async void Dest_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _syncingDest) return;
        if (DestCombo.SelectedItem is not OutputChoice choice) return;
        Prefs.SendTarget = choice.Id;
        await RestartSenderIfRunningAsync();
    }

    private async void Opus_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingOpus) return;
        Prefs.OpusEnabled = OpusToggle.IsChecked == true;
        await RestartSenderIfRunningAsync();
    }

    /// <summary>
    /// Destination ou compression changée en pleine émission : redémarrage,
    /// avec une pause le temps que la boucle rétablisse les réglages audio
    /// sauvegardés (sinon le nouveau départ mémoriserait le volume épinglé).
    /// </summary>
    private async Task RestartSenderIfRunningAsync()
    {
        if (!_sender.IsRunning) return;
        _sender.Stop();
        await Task.Delay(700);
        if (_sendWanted) _sender.Start();
    }

    // ---------- Diagnostic ----------

    private long _diagBytes;
    private long _diagAt;

    private void UpdateDiagnostics()
    {
        DiagLatency.Text = _net.LatencyMs is int ms ? $"{ms} ms" : "—";

        // Débit instantané : delta d'octets (émis + reçus) depuis le dernier
        // passage, converti en kbit/s.
        long bytes = _sender.BytesSent + _net.BytesReceived;
        long now = Environment.TickCount64;
        if (_diagAt > 0 && now > _diagAt)
        {
            double kbps = (bytes - _diagBytes) * 8.0 / (now - _diagAt);
            DiagRate.Text = kbps > 1 ? $"{kbps:0} kbit/s" : "—";
        }
        _diagBytes = bytes;
        _diagAt = now;

        DiagLog.Text = TailLog(6);
    }

    /// <summary>Dernières lignes du journal, lu sans gêner les écritures.</summary>
    private static string TailLog(int lines)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "audioshare.log");
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            if (fs.Length > 16384) fs.Seek(-16384, System.IO.SeekOrigin.End);
            using var reader = new System.IO.StreamReader(fs);
            var all = reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join("\n", all.TakeLast(lines));
        }
        catch { return ""; }
    }

    private bool _checkingUpdate;

    /// <summary>
    /// Tuile « Mises à jour » : « À jour · 1.9.1 » (point vert) une fois la
    /// vérification faite, la flèche tourne pendant qu'elle a lieu. Le
    /// message complet de la dernière vérification est dans l'infobulle.
    /// </summary>
    private void UpdateUpdateTile()
    {
        UpdateTile.Title = Loc.T("Updates", "Mises à jour");
        string v = Updater.CurrentVersion;
        if (_checkingUpdate)
        {
            UpdateTile.SetState(TileTint.Glass, Loc.T("Checking…", "Vérification…"));
            return;
        }
        switch (Updater.LastOutcome)
        {
            case UpdateOutcome.UpToDate:
                UpdateTile.SetState(TileTint.Glass, Loc.T($"Up to date · {v}", $"À jour · {v}"), dot: Palette.Green);
                break;
            case UpdateOutcome.Updating:
                UpdateTile.SetState(TileTint.Orange, Loc.T("Installing…", "Installation…"));
                break;
            case UpdateOutcome.Failed:
                UpdateTile.SetState(TileTint.Glass, Loc.T("Check failed", "Vérification impossible"), dot: Palette.Red);
                break;
            case UpdateOutcome.DevCopy:
                UpdateTile.SetState(TileTint.Glass, Loc.T($"Dev copy · {v}", $"Copie de dev · {v}"));
                break;
            default:
                UpdateTile.SetState(TileTint.Glass, $"Version {v}");
                break;
        }
    }

    private async void CheckUpdate()
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        UpdateTile.SetBusy(true);
        UpdateUpdateTile();
        var started = DateTime.UtcNow;
        try
        {
            await Updater.CheckAndUpdateAsync(
                msg => Dispatcher.Invoke(() => UpdateTile.ToolTip = msg),
                verbose: true);
            // Une réponse instantanée paraîtrait n'avoir rien fait : la
            // flèche fait au moins un tour.
            var left = TimeSpan.FromMilliseconds(800) - (DateTime.UtcNow - started);
            if (left > TimeSpan.Zero) await Task.Delay(left);
        }
        finally
        {
            _checkingUpdate = false;
            UpdateTile.SetBusy(false);
            UpdateUpdateTile();
        }
    }

    // ---------- Session du téléphone ----------

    /// <summary>
    /// Tuile « Réseau local » : en écoute (verre), reçoit (bleu, toucher = mettre
    /// l'émetteur en pause), en pause (un toucher reprend).
    /// </summary>
    private void UpdateNetworkUi()
    {
        if (_net.IsReceiving)
        {
            NetworkTile.SetState(TileTint.Blue, Loc.T("Receiving", "Reçoit"),
                Loc.T($"from {_net.SenderName}", $"depuis {_net.SenderName}"));
            // Latence du tampon en infobulle : la santé du lien sans faire
            // clignoter la tuile chaque seconde.
            NetworkTile.ToolTip = _net.LatencyMs is int ms
                ? Loc.T($"{_net.SenderName} · {ms} ms latency", $"{_net.SenderName} · latence {ms} ms")
                : null;
        }
        else if (_net.PausedSender is string paused)
        {
            NetworkTile.SetState(TileTint.Glass, Loc.T("Paused", "En pause"), Loc.T("Tap to resume", "Toucher pour reprendre"));
            NetworkTile.ToolTip = Loc.T($"Audio from {paused} is paused.", $"Le son de {paused} est en pause.");
        }
        else
        {
            NetworkTile.SetState(TileTint.Glass, Loc.T("Listening", "En écoute"), Loc.T("No PC", "Aucun PC"));
            NetworkTile.ToolTip = Loc.T(
                "Another PC running Duckast can stream its audio here: turn on “Send” over there, the connection is automatic.",
                "Un autre PC avec Duckast peut diffuser son audio ici : active « Envoyer » là-bas, la connexion est automatique.");
        }
        NetworkTile.Title = Loc.T("Local network", "Réseau local");
        NetworkTile.Clickable = _net.IsReceiving || _net.PausedSender != null;
        UpdateTrayIcon();
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

    // L'utilisateur veut émettre (la tuile « Envoyer » est allumée).
    private bool _sendWanted;
    // Dernier récepteur joint : la tuile éteinte dit vers qui elle enverrait.
    private string? _lastReceiver;

    private void SetSending(bool on)
    {
        _sendWanted = on;
        if (on) _sender.Start();
        else _sender.Stop();
        UpdateSendUi();
    }

    /// <summary>
    /// Tuile « Envoyer » : éteinte (verre), sinon bleue — recherche,
    /// connexion puis émission ; toucher arrête. La section du volume devient
    /// « SON ÉMIS » pendant l'émission.
    /// </summary>
    private void UpdateSendUi()
    {
        if (_sender.Phase == SendPhase.Streaming && _sender.PeerName != null) _lastReceiver = _sender.PeerName;
        string? target = Prefs.SendTarget ?? _lastReceiver;
        string? toTarget = target != null ? Loc.T($"to {target}", $"vers {target}") : null;

        SendTile.Title = Loc.T("Send", "Envoyer");
        if (!_sender.IsRunning)
        {
            SendTile.SetState(TileTint.Glass, Loc.T("Off", "Éteint"),
                toTarget ?? Loc.T("Tap to stream", "Toucher pour diffuser"));
        }
        else
        {
            string? peer = _sender.PeerName != null ? Loc.T($"to {_sender.PeerName}", $"vers {_sender.PeerName}") : null;
            switch (_sender.Phase)
            {
                case SendPhase.Streaming:
                    SendTile.SetState(TileTint.Purple, Loc.T("Streaming", "Émission"), peer);
                    break;
                case SendPhase.Connecting:
                    SendTile.SetState(TileTint.Purple, Loc.T("Connecting…", "Connexion…"), peer);
                    break;
                case SendPhase.Retrying:
                    SendTile.SetState(TileTint.Purple, Loc.T("Retrying…", "Nouvel essai…"), toTarget);
                    break;
                default:
                    SendTile.SetState(TileTint.Purple, Loc.T("Searching…", "Recherche…"),
                        toTarget ?? Loc.T("for a receiver", "d'un récepteur"));
                    break;
            }
        }
        SendTile.ToolTip = string.IsNullOrEmpty(_sender.State)
            ? Loc.T("Streams this PC's audio to another Duckast on the network.",
                    "Diffuse le son de ce PC vers un autre Duckast du réseau.")
            : _sender.State;

        SetTextFaded(VolumeHeader, _sender.IsRunning
            ? Loc.T("SENT AUDIO", "SON ÉMIS")
            : Loc.T("RECEIVED AUDIO", "SON REÇU"));
        UpdateTrayIcon();
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

    // ---------- Vumètre ----------

    private double _vuPhase;

    /// <summary>
    /// Une nappe de l'aurore : une forme organique (polygone lissé en Bézier)
    /// dont chaque point de contour ondule indépendamment, remplie d'un
    /// dégradé radial qui s'évanouit en transparence — les couleurs se fondent
    /// au lieu de s'empiler. Vitesses de dérive, d'ondulation et de glissement
    /// de teinte propres à chaque nappe.
    /// </summary>
    private sealed class VuBlob
    {
        public required System.Windows.Shapes.Path Shape;
        public required TranslateTransform Pos;
        public required GradientStop Core, S1, S2, Mid, Edge;
        public double RX, RY, SpeedX, SpeedY, PhaseX, PhaseY;
        public double CR = 255, CG = 255, CB = 255; // couleur courante, glisse vers la cible
        public double[] PtFreq = [], PtPhase = [];
    }

    private const int VuPoints = 7; // points de contour par nappe

    private readonly List<VuBlob> _blobs = new();
    private readonly Random _vuRng = new();

    /// <summary>
    /// Formes, tailles, vitesses et couleurs tirées au hasard : chaque
    /// lancement de l'app a sa propre aurore.
    /// </summary>
    private void CreateVuBlobs()
    {
        for (int i = 0; i < 8; i++)
        {
            // Trois arrêts : le cœur déjà doux, une mi-course encore présente,
            // un bord totalement transparent — la nappe n'a aucun contour.
            // Courbe de fondu en cloche (plusieurs arrêts) et bord transparent
            // atteint bien en deçà du contour de la forme : aucune arête.
            var core = new GradientStop(Colors.Transparent, 0);
            var s1 = new GradientStop(Colors.Transparent, 0.14);
            var s2 = new GradientStop(Colors.Transparent, 0.28);
            var mid = new GradientStop(Colors.Transparent, 0.42);
            var edge = new GradientStop(Colors.Transparent, 0.56);
            var pos = new TranslateTransform();
            var shape = new System.Windows.Shapes.Path
            {
                Fill = new RadialGradientBrush { GradientStops = { core, s1, s2, mid, edge, new GradientStop(Colors.Transparent, 1) } },
                RenderTransform = pos,
            };
            var blob = new VuBlob
            {
                Shape = shape, Pos = pos, Core = core, S1 = s1, S2 = s2, Mid = mid, Edge = edge,
                RX = 110 + _vuRng.NextDouble() * 90,
                RY = 52 + _vuRng.NextDouble() * 40,
                SpeedX = 0.30 + _vuRng.NextDouble() * 0.55,
                SpeedY = 0.25 + _vuRng.NextDouble() * 0.50,
                PhaseX = _vuRng.NextDouble() * Math.Tau,
                PhaseY = _vuRng.NextDouble() * Math.Tau,
                // Chaque point de contour a sa fréquence et sa phase : la
                // forme ondule sans jamais repasser par le même dessin.
                PtFreq = Enumerable.Range(0, VuPoints)
                    .Select(_ => 0.6 + _vuRng.NextDouble() * 1.3).ToArray(),
                PtPhase = Enumerable.Range(0, VuPoints)
                    .Select(_ => _vuRng.NextDouble() * Math.Tau).ToArray(),
            };
            VuGlow.Children.Add(shape);
            _blobs.Add(blob);
        }
    }

    /// <summary>
    /// Contour organique du moment : rayon de chaque point modulé par sa
    /// sinusoïde, puis lissage Catmull-Rom en Bézier fermées — aucune arête.
    /// La géométrie est centrée sur l'origine, la translation place la nappe.
    /// </summary>
    private StreamGeometry BuildBlobGeometry(VuBlob b)
    {
        var pts = new System.Windows.Point[VuPoints];
        for (int k = 0; k < VuPoints; k++)
        {
            double angle = k * Math.Tau / VuPoints;
            double r = 1 + 0.22 * Math.Sin(_vuPhase * b.PtFreq[k] + b.PtPhase[k]);
            pts[k] = new System.Windows.Point(
                b.RX * r * Math.Cos(angle), b.RY * r * Math.Sin(angle));
        }

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(pts[0], isFilled: true, isClosed: true);
            for (int k = 0; k < VuPoints; k++)
            {
                var p0 = pts[(k - 1 + VuPoints) % VuPoints];
                var p1 = pts[k];
                var p2 = pts[(k + 1) % VuPoints];
                var p3 = pts[(k + 2) % VuPoints];
                gc.BezierTo(
                    new System.Windows.Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                    new System.Windows.Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6),
                    p2, isStroked: false, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Crête instantanée (0..1) du son que pilote le curseur — émis, reçu du
    /// réseau ou du téléphone ; null quand il n'y a rien à piloter.
    /// </summary>
    private float? CurrentLevel() =>
        _sender.IsRunning ? _sender.Level
        : _net.IsReceiving ? _net.OutputLevel
        : PhoneAudio.IsPresent ? PhoneAudio.Peak
        : null;

    private readonly float[] _srcShown = new float[4];

    /// <summary>
    /// Aurore diffuse sur tout le fond de la carte volume. La couleur dit
    /// d'où vient le son : bleu = réseau, vert = téléphone, violet = notre
    /// propre son émis, rose (couleur des AirPods) ajouté quand ils sont
    /// connectés. Chaque source n'apparaît que tant qu'elle joue vraiment ;
    /// les nappes se répartissent les couleurs actives et glissent de l'une
    /// à l'autre sans saut. L'opacité respire avec le niveau (attaque
    /// immédiate, retombée douce) ; la dérive et le morphing accélèrent avec
    /// le volume. Abstrait, sans barres ni chiffres.
    /// </summary>
    private void UpdateVu()
    {
        // 0 = émis (violet), 1 = réseau (bleu), 2 = téléphone (vert)
        float[] raw =
        [
            _sender.IsRunning ? _sender.Level : 0f,
            _net.IsReceiving ? _net.OutputLevel : 0f,
            PhoneAudio.IsPresent ? PhoneAudio.Peak : 0f,
        ];
        System.Windows.Media.Color[] tints = [Palette.Purple, Palette.BlueLight, Palette.Green];

        float level = 0;
        var active = new List<(System.Windows.Media.Color Color, float Level)>();
        for (int i = 0; i < 3; i++)
        {
            _srcShown[i] = Math.Max(Math.Min(1f, raw[i]), _srcShown[i] * 0.94f);
            level = Math.Max(level, _srcShown[i]);
            if (_srcShown[i] > 0.01f) active.Add((tints[i], _srcShown[i]));
        }

        if (active.Count == 0)
        {
            // Plus de source : l'aurore s'éteint en fondu, pas d'un coup.
            VuGlow.Opacity = VuGlow.Opacity < 0.01 ? 0 : VuGlow.Opacity * 0.93;
            _vuShown = 0f;
            return;
        }

        // Les AirPods ne produisent pas de son eux-mêmes : ils colorent ce
        // qui joue, tant qu'ils sont connectés.
        if (_airPods.State == AirPodsState.Connected) active.Add((Palette.Rose, level));

        _vuShown = level;
        double vis = Math.Sqrt(_vuShown); // racine : lisible aux niveaux modérés
        VuGlow.Opacity = Math.Min(1, vis * 1.1);

        _vuPhase += 0.03 + vis * 0.10;
        double w = VuClip.ActualWidth, h = VuClip.ActualHeight;

        for (int i = 0; i < _blobs.Count; i++)
        {
            var b = _blobs[i];
            // Dérive : le centre balaie toute la carte, en X comme en Y.
            b.Pos.X = w * (0.5 + 0.45 * Math.Sin(_vuPhase * b.SpeedX + b.PhaseX));
            b.Pos.Y = h * (0.5 + 0.45 * Math.Sin(_vuPhase * b.SpeedY + b.PhaseY));

            // Morphing : le contour est redessiné à chaque pas, chaque point
            // sur sa propre ondulation — la forme se transforme sans cesse.
            b.Shape.Data = BuildBlobGeometry(b);

            // Couleur : la nappe prend une source active (répartition
            // cyclique) et y glisse ; une source faible donne une nappe pâle.
            var (target, srcLevel) = active[i % active.Count];
            b.CR += (target.R - b.CR) * 0.12;
            b.CG += (target.G - b.CG) * 0.12;
            b.CB += (target.B - b.CB) * 0.12;
            double k = 0.45 + 0.55 * Math.Sqrt(srcLevel);
            var c = System.Windows.Media.Color.FromRgb((byte)b.CR, (byte)b.CG, (byte)b.CB);
            b.Core.Color = System.Windows.Media.Color.FromArgb((byte)(0xA0 * k), c.R, c.G, c.B);
            b.S1.Color = System.Windows.Media.Color.FromArgb((byte)(0x84 * k), c.R, c.G, c.B);
            b.S2.Color = System.Windows.Media.Color.FromArgb((byte)(0x50 * k), c.R, c.G, c.B);
            b.Mid.Color = System.Windows.Media.Color.FromArgb((byte)(0x22 * k), c.R, c.G, c.B);
            b.Edge.Color = System.Windows.Media.Color.FromArgb(0x00, c.R, c.G, c.B);
        }
    }

    private void PollPhoneSession()
    {
        // La latence affichée à côté du nom de l'émetteur vit au même rythme,
        // tout comme la page diagnostic quand elle est ouverte.
        if (_net.IsReceiving && IsVisible) UpdateNetworkUi();
        _net.ExpirePause();
        if (IsVisible && _page == AppPage.Diag) UpdateDiagnostics();
        PhoneAudio.Refresh();
        LogPhoneSession();
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

    private string? _lastPhoneSession;

    /// <summary>
    /// Trace les apparitions, disparitions et changements d'état de la session
    /// du téléphone : c'est ce qui permet, après coup, de distinguer un
    /// « connecté mais muet » d'une simple pause en relisant le journal.
    /// </summary>
    private void LogPhoneSession()
    {
        var summary = PhoneAudio.Describe();
        if (summary == _lastPhoneSession) return;
        _lastPhoneSession = summary;
        Log.Write(summary == null
            ? "Bluetooth : plus aucune session audio du téléphone"
            : $"Bluetooth : session du téléphone {summary}");
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateMuteIcon();
        if (_syncingVolume || !IsLoaded) return;
        float v = (float)e.NewValue;

        // Repère sonore : pas pendant le geste, une seule note quand il se
        // termine (relâchement, ou valeur stable pour le clavier).
        _toneTimer.Stop();
        _toneTimer.Start();

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

    private readonly DispatcherTimer _toneTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    /// <summary>
    /// Une note brève au volume choisi, à la fin du geste — et seulement si
    /// rien ne joue : quand du son passe, c'est lui qui renseigne, inutile de
    /// lui superposer un repère. Elle sort là où sort le son piloté, avec sa
    /// balance.
    /// </summary>
    private void PlayVolumeTone()
    {
        _toneTimer.Stop();
        float v = (float)VolumeSlider.Value;
        if (v < 0.001f) return;
        if (CurrentLevel() is not { } level || level > 0.01f) return;

        if (_sender.IsRunning)
        {
            // Nos haut-parleurs sont coupés, mais la boucle prélève avant le
            // muet : la note part dans le flux, qui lui applique le volume
            // émis, puis le récepteur sa balance.
            VolumeTestTone.Play(1f, 1f);
        }
        else if (_net.IsReceiving)
        {
            VolumeTestTone.Play(v * _balL, v * _balR, NetworkReceiver.CreateOutput);
        }
        else
        {
            // Le flux du téléphone ne suit que la sortie par défaut.
            VolumeTestTone.Play(v * _balL, v * _balR);
        }
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
        var before = MuteButton.Content;
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

        // Le pictogramme change de palier : petit rebond pour le signaler.
        if (IsVisible && !Equals(before, MuteButton.Content))
        {
            MuteButton.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            MuteButton.RenderTransform = _mutePop;
            Pop(_mutePop, 0.78, 260);
        }
        UpdateVolumeReadout();
    }

    private static readonly System.Windows.Media.Color IconOnFill = System.Windows.Media.Color.FromRgb(0x1B, 0x1D, 0x22);

    /// <summary>
    /// Pourcentage à droite de « SON REÇU », et couleur du pictogramme posé
    /// dans la barre : sombre sur la partie remplie, blanc sur la piste.
    /// </summary>
    private void UpdateVolumeReadout()
    {
        int pct = (int)Math.Round(VolumeSlider.Value * 100);
        VolumePercent.Text = VolumeSlider.IsEnabled ? Loc.T($"{pct}%", $"{pct} %") : "—";
        bool onFill = VolumeSlider.IsEnabled && VolumeSlider.FilledWidth > 32;
        Palette.ColorTo(MuteBrush, onFill ? IconOnFill : Colors.White, IsVisible, 220);
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

    // Avec la gestion AirPods active, un double-clic sur l'icône ouvre la
    // popup AirPods : l'ouverture du panneau attend donc le délai de
    // double-clic, pour ne pas l'afficher puis le retirer aussitôt. La
    // fermeture, elle, reste immédiate.
    private readonly DispatcherTimer _trayClickDelay = new();

    private bool AirPodsPopupAvailable => _airPods.IsRunning || _airPods.IsOwnedElsewhere;

    private void OnTrayIconClick()
    {
        if (_trayClickDelay.IsEnabled) return; // second clic, le double-clic suit
        if (IsVisible || !AirPodsPopupAvailable)
        {
            ToggleFlyout();
            return;
        }
        // Clic qui vient de fermer le panneau (désactivation) : rien à rouvrir.
        if ((DateTime.UtcNow - _lastAutoHide).TotalMilliseconds < 350) return;

        _trayClickDelay.Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime);
        _trayClickDelay.Start();
    }

    private void OnTrayIconDoubleClick()
    {
        _trayClickDelay.Stop();
        ShowAirPodsPopup();
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
        if (_page != AppPage.Main) NavigateTo(AppPage.Main, forward: false, animate: false);
        _hiding = false;
        PlaceBottomRight();
        Show();
        WindowEffects.ForceForeground(this);
        Dispatcher.BeginInvoke(() => WindowEffects.RefreshBackdrop(this),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
        Cascade(MainPanel);
        Pop(_logoPop, 0.55, 420);
    }

    private bool _hiding;

    /// <summary>
    /// Le contenu retombe en fondu avant que la fenêtre disparaisse — bref,
    /// pour ne pas faire attendre. Rouvert entre-temps (ShowFlyout remet
    /// _hiding à false), le masquage en cours est abandonné.
    /// </summary>
    private void HideFlyout()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _lastAutoHide = DateTime.UtcNow;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return;
            _hiding = false;
            Hide();
        };
        RootSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, TimeSpan.FromMilliseconds(110))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        Root.BeginAnimation(OpacityProperty, fade);
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.Notify(Loc.T("Duckast keeps running in the background",
                               "Duckast reste actif en arrière-plan"),
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

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        // Laisse l'audio des AirPods verrouillé avant de partir (rapide, sans UAC).
        try { await _airPods.ShutdownAsync(); }
        catch (Exception ex) { Log.Write($"AirPods : fermeture — {ex.Message}"); }
        Close();
    }

    // ---------- AirPods ----------
    // Raccordement de AirPodsGate à l'interface : carte de la page principale,
    // section des Réglages, popup « Connecter », menu de l'icône.

    private void OnAirPodsChanged()
    {
        UpdateAirPodsUi();
        SyncAirPodsSettings(repopulateDevices: false);
        _airPodsPopup.UpdateBattery(_airPods.Battery);
        _airPodsPopup.RefreshLive();
    }

    private void OnAirPodsPrompt(AirPodsPrompt prompt) => _airPodsPopup.ShowPrompt(prompt);

    /// <summary>Boîtier ouvert, AirPods déjà connectés : batteries pendant quelques secondes.</summary>
    private void OnAirPodsStatusPeek()
    {
        if (!AirPodsPopupAvailable || (IsVisible && !_hiding)) return;
        _airPodsPopup.ShowLive(LiveAirPodsCard, 8000);
    }

    private void OnAirPodsPromptWithdrawn()
    {
        if (_airPodsPopup.IsShowingPrompt) _airPodsPopup.HideNow();
    }

    private void OnAirPodsMessage(AirPodsMessage message)
    {
        // Panneau ouvert : la tuile affiche déjà l'état, pas de fenêtre en
        // plus (sauf pour retirer une proposition devenue caduque).
        if (IsVisible && !_hiding)
        {
            if (_airPodsPopup.IsVisible) _airPodsPopup.HideNow();
            return;
        }
        _airPodsPopup.ShowMessage(message);
    }

    /// <summary>
    /// Tuile AirPods de la page principale (et entrée du menu de l'icône) :
    /// verre au repos (« À proximité » + niveaux), orange pendant la
    /// connexion, rose une fois connectés ; toucher annule ou déconnecte.
    /// </summary>
    private void UpdateAirPodsUi()
    {
        bool shown = _airPods.IsRunning || _airPods.IsOwnedElsewhere;
        bool wasShown = AirPodsTile.Visibility == Visibility.Visible;
        AirPodsTile.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown && !wasShown && IsVisible) Enter(AirPodsTile, 0, 0, 10, pop: true);

        var state = _airPods.State;
        var battery = _airPods.Battery;
        bool hasBattery = battery is { IsEmpty: false };
        AirPodsTile.Title = _airPods.DeviceName ?? "AirPods";

        if (_airPods.IsOwnedElsewhere)
            AirPodsTile.SetState(TileTint.Glass, Loc.T("Managed elsewhere", "Gérés ailleurs"),
                Loc.T("by another Duckast", "par un autre Duckast"));
        else if (state == AirPodsState.NoDevice)
            AirPodsTile.SetState(TileTint.Glass, Loc.T("None paired", "Aucun appairé"),
                Loc.T("Pair them in Windows", "Appaire-les dans Windows"));
        else if (state == AirPodsState.Connected)
            AirPodsTile.SetState(TileTint.Rose, Loc.T("Connected", "Connectés"),
                hasBattery ? null : Loc.T("Audio on AirPods", "Son sur les AirPods"));
        else if (_airPods.IsConnecting)
            AirPodsTile.SetState(TileTint.Orange, Loc.T("Connecting…", "Connexion…"),
                hasBattery ? null : Loc.T("Waiting for Windows", "Windows se connecte"));
        else if (_airPods.IsBusy)
            AirPodsTile.SetState(TileTint.Glass, Loc.T("Disconnecting…", "Déconnexion…"),
                hasBattery ? null : Loc.T("Locking audio", "Verrouillage"));
        else if (state == AirPodsState.Present)
            AirPodsTile.SetState(TileTint.Glass, Loc.T("Nearby", "À proximité"),
                hasBattery ? null : Loc.T("Tap to connect", "Toucher pour connecter"));
        else
            AirPodsTile.SetState(TileTint.Glass, Loc.T("Idle", "Au repos"),
                Loc.T("Tap to connect", "Toucher pour connecter"));

        _airPodsBars.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
        _airPodsBars.SetOnTint(AirPodsTile.Tint != TileTint.Glass, IsVisible);
        if (hasBattery) _airPodsBars.SetLevels(battery, IsVisible);
        AirPodsTile.Clickable = state == AirPodsState.Connected ? _airPods.CanDisconnect
            : _airPods.IsConnecting ? !_airPods.IsBusy : _airPods.CanConnect;
        AirPodsTile.ToolTip = _airPods.StatusText;

        bool hasDevice = _airPods.DeviceName != null;
        bool disconnect = _airPods.PrefersDisconnect;
        bool actionable = disconnect ? _airPods.CanDisconnect : _airPods.CanConnect;
        _tray.SetAirPods(
            _airPods.IsRunning && hasDevice
                ? (disconnect ? Loc.T("Disconnect AirPods", "Déconnecter les AirPods")
                              : Loc.T("Connect AirPods", "Connecter les AirPods"))
                : null,
            actionable);
    }

    /// <summary>Toucher la tuile AirPods la bascule : connecte, annule ou déconnecte.</summary>
    private void OnAirPodsTileClick()
    {
        if (_airPods.State == AirPodsState.Connected)
        {
            if (_airPods.CanDisconnect) _ = _airPods.DisconnectAsync();
        }
        else if (_airPods.IsConnecting)
        {
            if (!_airPods.IsBusy) _ = _airPods.CancelConnectAsync();
        }
        else if (_airPods.CanConnect) _ = _airPods.ConnectAsync();
    }

    /// <summary>
    /// État courant pour la fenêtre AirPods ouverte par double-clic ou à
    /// l'ouverture du boîtier : mêmes couleurs que la tuile.
    /// </summary>
    private PodsCard LiveAirPodsCard()
    {
        string title = _airPods.DeviceName ?? "AirPods";
        var battery = _airPods.Battery;
        if (_airPods.State == AirPodsState.Connected) return new(title, PodsCardState.Connected, null, battery);
        if (_airPods.IsConnecting) return new(title, PodsCardState.Connecting, null, battery);
        if (_airPods.CanConnect) return new(title, PodsCardState.Prompt, null, battery);
        return new(title, PodsCardState.Info, _airPods.StatusText, battery);
    }

    /// <summary>Double-clic sur l'icône : fenêtre AirPods avec l'état du moment.</summary>
    private void ShowAirPodsPopup()
    {
        if (!AirPodsPopupAvailable) return;
        if (IsVisible) HideFlyout();
        _airPodsPopup.ShowLive(LiveAirPodsCard);
    }

    /// <summary>Entrée du menu de l'icône : connecte ou déconnecte selon l'état.</summary>
    private async void ToggleAirPodsConnection()
    {
        try
        {
            if (_airPods.PrefersDisconnect) await _airPods.DisconnectAsync();
            else await _airPods.ConnectAsync();
        }
        catch (Exception ex) { Log.Write($"AirPods : action impossible — {ex.Message}"); }
    }

    private bool _syncingAirPods;
    private bool _airPodsToggling;

    /// <summary>
    /// Carte AIRPODS des Réglages. La liste des appareils n'est relue qu'à
    /// l'ouverture des Réglages ou à l'activation (pas à chaque changement
    /// d'état, qui fermerait la liste déroulante sous les doigts).
    /// </summary>
    private void SyncAirPodsSettings(bool repopulateDevices)
    {
        _syncingAirPods = true;
        try
        {
            AirPodsSettingsHeader.Text = "AIRPODS";
            AirPodsSettingsTile.Title = Loc.T("AirPods management", "Gestion des AirPods");
            if (!_airPodsToggling)
            {
                bool on = _airPods.IsEnabled;
                AirPodsSettingsTile.SetState(on ? TileTint.Green : TileTint.Glass,
                    _airPods.IsOwnedElsewhere ? Loc.T("Managed by another Duckast", "Gérée par un autre Duckast")
                    : on ? Loc.T("On", "Activée") : Loc.T("Off", "Désactivée"));
            }
            bool animate = IsVisible && _page == AppPage.Settings;
            bool open = _airPods.IsRunning;
            if (open != AirPodsOptionsHost.IsHitTestVisible || !animate)
                Reveal(AirPodsOptionsHost, AirPodsOptions, open, animate);
            AirPodsPromptToggle.IsChecked = _airPods.PromptOnDetect;

            if (_airPods.IsRunning && (repopulateDevices || AirPodsDeviceCombo.ItemsSource == null))
                AirPodsDeviceCombo.ItemsSource = _airPods.GetAudioDevices();
            if (AirPodsDeviceCombo.ItemsSource is IEnumerable<PairedDevice> devices)
                AirPodsDeviceCombo.SelectedItem = devices.FirstOrDefault(d => d.Address == _airPods.DeviceAddress);

            AirPodsHint.Text =
                _airPods.IsOwnedElsewhere
                    ? Loc.T("Another Duckast instance on this PC is already managing the AirPods.",
                            "Une autre instance de Duckast gère déjà les AirPods sur ce PC.")
                : _airPods.IsRunning
                    ? Loc.T("Windows no longer connects to your AirPods on its own: Duckast offers to connect them when it detects them. Turning this off gives them back to Windows.",
                            "Windows ne se connecte plus seul à tes AirPods : Duckast propose de les connecter quand il les détecte. Désactiver les rend à Windows.")
                    : Loc.T("Tap to stop Windows from grabbing your AirPods (e.g. while they're on your iPhone). Duckast then offers to connect them when they're detected.",
                            "Touche la carte pour empêcher Windows d'accaparer tes AirPods (par exemple quand ils sont sur ton iPhone). Duckast propose alors de les connecter quand ils sont détectés.");
        }
        finally
        {
            _syncingAirPods = false;
        }
    }

    /// <summary>Toucher l'en-tête de la carte AIRPODS : active / désactive la gestion.</summary>
    private async void ToggleAirPodsManagement()
    {
        if (_airPodsToggling || _airPods.IsOwnedElsewhere) return;
        _airPodsToggling = true;
        bool on = !_airPods.IsEnabled;
        AirPodsSettingsTile.SetState(TileTint.Orange,
            on ? Loc.T("Turning on…", "Activation…") : Loc.T("Turning off…", "Désactivation…"));
        try
        {
            await _airPods.SetEnabledAsync(on);
        }
        catch (Exception ex) { Log.Write($"AirPods : (dés)activation impossible — {ex.Message}"); }
        finally
        {
            _airPodsToggling = false;
            AirPodsDeviceCombo.ItemsSource = null;
            SyncAirPodsSettings(repopulateDevices: true);
            UpdateAirPodsUi();
        }
    }

    private async void AirPodsDevice_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _syncingAirPods) return;
        if (AirPodsDeviceCombo.SelectedItem is not PairedDevice device) return;
        try { await _airPods.SelectDeviceAsync(device); }
        catch (Exception ex) { Log.Write($"AirPods : changement d'appareil impossible — {ex.Message}"); }
    }

    /// <summary>
    /// Affiche la fenêtre telle qu'elle apparaît à la détection (données
    /// réelles si les AirPods sont à portée). La toucher connecte pour de
    /// vrai ; la fermer ne compte pas comme un refus.
    /// </summary>
    private void AirPodsPreview_Click(object sender, RoutedEventArgs e)
    {
        _airPodsPopup.ShowPrompt(
            new AirPodsPrompt(_airPods.DeviceName ?? "AirPods Pro", _airPods.Battery, 25_000),
            preview: true);
    }

    private void AirPodsPrompt_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingAirPods) return;
        _airPods.PromptOnDetect = AirPodsPromptToggle.IsChecked == true;
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
            UpdatePhoneTile();
        });

        _watcher.Removed += (_, update) => Dispatcher.Invoke(() =>
        {
            var dev = _devices.FirstOrDefault(d => d.Id == update.Id);
            if (dev != null) _devices.Remove(dev);
            CloseConnection(update.Id);
            UpdatePhoneTile();
        });

        _watcher.EnumerationCompleted += (_, _) => Dispatcher.Invoke(() =>
        {
            _phonesEnumerated = true;
            UpdatePhoneTile();
        });

        _watcher.Start();
    }

    private bool _phonesEnumerated;

    /// <summary>
    /// Téléphone montré par la tuile : celui qui est relié, sinon le dernier
    /// utilisé, sinon le premier appairé.
    /// </summary>
    private BtDevice? ShownPhone =>
        _devices.FirstOrDefault(d => d.Id == _activeId)
        ?? _devices.FirstOrDefault(d => d.Id == Prefs.LastPhoneId)
        ?? _devices.FirstOrDefault();

    private bool PhoneOpened =>
        _activeId != null
        && _connections.TryGetValue(_activeId, out var connection)
        && connection.State == AudioPlaybackConnectionState.Opened;

    /// <summary>
    /// Tuile du téléphone : disponible (verre, toucher pour recevoir), en
    /// attente (orange), connecté ; toucher arrête la réception.
    /// </summary>
    private void UpdatePhoneTile()
    {
        var dev = ShownPhone;
        PhoneTile.Title = dev?.Name ?? Loc.T("Phone", "Téléphone");
        if (dev == null)
        {
            PhoneTile.SetState(TileTint.Glass,
                _phonesEnumerated ? Loc.T("None paired", "Aucun appairé") : Loc.T("Looking…", "Recherche…"),
                _phonesEnumerated ? Loc.T("Pair it in Windows", "Appaire-le dans Windows") : null);
            PhoneTile.ToolTip = Loc.T("No paired phone — pair one in Windows Bluetooth settings.",
                                      "Aucun téléphone appairé — appaire-le dans les réglages Bluetooth de Windows.");
            return;
        }
        if (dev.Id == _activeId && PhoneOpened)
            PhoneTile.SetState(TileTint.Green, Loc.T("Connected", "Connecté"),
                Loc.T("Audio on this PC", "Son sur ce PC"));
        else if (dev.Id == _activeId)
            PhoneTile.SetState(TileTint.Orange, Loc.T("Waiting…", "En attente…"),
                Loc.T("Pick this PC on it", "Choisis ce PC dessus"));
        else
            PhoneTile.SetState(TileTint.Glass, Loc.T("Available", "Disponible"),
                Loc.T("Tap to receive", "Toucher pour recevoir"));
        PhoneTile.Clickable = true;
        PhoneTile.ToolTip = _devices.Count > 1
            ? Loc.T("Right-click to choose another phone.", "Clic droit pour choisir un autre téléphone.")
            : null;
    }

    private async void OnPhoneTileClick()
    {
        var dev = ShownPhone;
        if (dev == null)
        {
            // Aucun téléphone : on ouvre directement les réglages Bluetooth.
            try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write($"Bluetooth : réglages introuvables — {ex.Message}"); }
            return;
        }
        if (dev.Id == _activeId) { DisconnectPhone(); return; } // déjà choisi : toucher = arrêter
        await ConnectPhoneAsync(dev);
    }

    private async Task ConnectPhoneAsync(BtDevice dev)
    {
        // Une seule connexion active à la fois
        if (_activeId != null) CloseConnection(_activeId);
        Prefs.LastPhoneId = dev.Id;
        await StartConnectionAsync(dev);
    }

    /// <summary>Clic droit sur la tuile : choisir un autre téléphone appairé.</summary>
    private bool ShowPhoneMenu()
    {
        if (_devices.Count < 2) return false;
        var shown = ShownPhone;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = PhoneTile };
        foreach (var dev in _devices)
        {
            var item = new System.Windows.Controls.MenuItem { Header = dev.Name, IsChecked = dev == shown };
            item.Click += async (_, _) =>
            {
                Prefs.LastPhoneId = dev.Id;
                // Réception en cours sur un autre : on bascule ; sinon on
                // change seulement le téléphone affiché.
                if (_activeId != null && _activeId != dev.Id) await ConnectPhoneAsync(dev);
                else UpdatePhoneTile();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        return true;
    }

    // La connexion active a-t-elle déjà été ouverte ? Distingue une liaison
    // perdue d'une liaison pas encore établie.
    private bool _wasOpened;

    // Après une perte de liaison, on ne relance le téléphone que quelques
    // minutes : au-delà, il a sans doute été déconnecté exprès (depuis
    // l'iPhone, rien ne distingue ce cas d'une sortie de portée). La
    // connexion neuve reste prête : il peut toujours revenir de lui-même.
    private static readonly TimeSpan LossRetryWindow = TimeSpan.FromMinutes(2);
    private DateTime _retryUntil = DateTime.MaxValue;
    // Dernière reconnexion obtenue d'office (voir OnConnectionStateChanged).
    private DateTime _autoOpenedAt = DateTime.MinValue;

    private bool IsCurrent(string id, AudioPlaybackConnection connection) =>
        _connections.TryGetValue(id, out var current) && current == connection;

    /// <param name="retryFor">Durée pendant laquelle relancer le téléphone ;
    /// null = tant que l'appareil reste choisi (clic de l'utilisateur).</param>
    private async Task StartConnectionAsync(BtDevice dev, TimeSpan? retryFor = null)
    {
        // Toujours une connexion neuve : une connexion retombée à Closed puis
        // rouverte peut se dire « Opened » — et le téléphone « connecté » —
        // sans plus rien faire entendre. Seul un objet neuf rétablit le son
        // (c'est ce que faisait « ✕ puis re-clic » à la main).
        DisposeConnection(dev.Id);
        _retryTimer?.Stop();

        var connection = AudioPlaybackConnection.TryCreateFromId(dev.Id);
        if (connection == null)
        {
            CloseConnection(dev.Id);
            return;
        }

        _connections[dev.Id] = connection;
        _activeId = dev.Id;
        _wasOpened = false;

        // BeginInvoke et non Invoke : l'événement vient d'un rappel WinRT, et
        // Dispose() sur le thread d'interface pourrait attendre ce rappel.
        connection.StateChanged += (conn, _) =>
            Dispatcher.BeginInvoke(() => OnConnectionStateChanged(dev, conn));

        try
        {
            await connection.StartAsync();
            Log.Write($"Bluetooth : réception activée pour « {dev.Name} »");
        }
        catch (Exception ex)
        {
            // Sans cette trace, un échec ici est totalement invisible :
            // l'appareil reste « en attente » sans que rien ne l'explique.
            Log.Write($"Bluetooth : activation refusée pour « {dev.Name} » — {ex.Message}");
            if (!IsCurrent(dev.Id, connection)) return;
            CloseConnection(dev.Id);
            return;
        }
        // Arrêtée ou remplacée pendant l'activation.
        if (!IsCurrent(dev.Id, connection)) return;

        if (connection.State != AudioPlaybackConnectionState.Opened)
            dev.Status = Loc.T("waiting", "en attente");
        dev.IsActive = true;
        UpdateTrayIcon();

        // Tente d'ouvrir le flux audio toutes les 2 s jusqu'à ce que le téléphone soit connecté
        _retryUntil = retryFor is { } window ? DateTime.Now + window : DateTime.MaxValue;
        _retryTimer ??= CreateRetryTimer();
        _retryTimer.Start();
        await TryOpenAsync();
    }

    private DispatcherTimer CreateRetryTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await TryOpenAsync();
        return timer;
    }

    private void OnConnectionStateChanged(BtDevice dev, AudioPlaybackConnection conn)
    {
        // Rappel d'une connexion déjà remplacée ou fermée : sans objet.
        if (!IsCurrent(dev.Id, conn)) return;

        if (conn.State == AudioPlaybackConnectionState.Opened)
        {
            if (!_wasOpened)
            {
                Log.Write("Bluetooth : connexion établie");
                if (_retryUntil != DateTime.MaxValue) _autoOpenedAt = DateTime.Now;
            }
            _wasOpened = true;
            _retryTimer?.Stop();
            dev.Status = Loc.T("connected", "connecté");
            UpdateTrayIcon();
            return;
        }

        dev.Status = Loc.T("waiting", "en attente");
        UpdateTrayIcon();
        // Pas encore établie : la boucle d'ouverture s'en occupe déjà.
        if (!_wasOpened) return;

        // Liaison perdue (portée, veille, téléphone…) : on ne rouvre jamais
        // cette connexion-là, on en recrée une. On ne relance le téléphone
        // que si on ne vient pas déjà de le reconnecter d'office : s'il
        // recoupe aussitôt, c'est qu'on le déconnecte exprès depuis l'iPhone,
        // et on ne lutte pas contre l'utilisateur.
        bool insist = DateTime.Now - _autoOpenedAt > LossRetryWindow;
        Log.Write(insist
            ? "Bluetooth : liaison perdue — nouvelle connexion, on relance le téléphone"
            : "Bluetooth : liaison recoupée aussitôt — nouvelle connexion, sans relancer le téléphone");
        _ = StartConnectionAsync(dev, insist ? LossRetryWindow : TimeSpan.Zero);
    }

    /// <summary>
    /// À la sortie de veille, la radio Bluetooth repart de zéro : la connexion
    /// d'avant peut rester « Opened » sans plus rien recevoir. On la remplace.
    /// </summary>
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        Dispatcher.BeginInvoke(async () =>
        {
            // Laisse à la pile Bluetooth le temps de se réveiller.
            await Task.Delay(TimeSpan.FromSeconds(3));
            var dev = _devices.FirstOrDefault(d => d.Id == _activeId);
            if (dev == null) return;
            Log.Write("Bluetooth : sortie de veille — nouvelle connexion");
            await StartConnectionAsync(dev, LossRetryWindow);
        });
    }

    /// <summary>Toucher la tuile du téléphone connecté : arrête la réception.</summary>
    private void DisconnectPhone()
    {
        if (_activeId != null) CloseConnection(_activeId);
    }

    // Connexion dont un OpenAsync est en cours. Il peut patienter ~25 s le
    // temps que le téléphone réponde : le minuteur de 2 s empilait alors
    // jusqu'à 5 ouvertures simultanées de la même connexion.
    private AudioPlaybackConnection? _opening;

    private async Task TryOpenAsync()
    {
        if (_activeId == null || !_connections.TryGetValue(_activeId, out var connection)) return;
        if (connection == _opening) return;
        if (connection.State == AudioPlaybackConnectionState.Opened)
        {
            _retryTimer?.Stop();
            return;
        }
        if (DateTime.Now >= _retryUntil)
        {
            _retryTimer?.Stop();
            Log.Write("Bluetooth : on attend que le téléphone revienne de lui-même");
            return;
        }

        var id = _activeId;
        _opening = connection;
        try
        {
            var result = await connection.OpenAsync();
            if (!IsCurrent(id, connection)) return; // remplacée pendant l'attente
            if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success)
            {
                _retryTimer?.Stop();
                // Toujours tracé, y compris quand tout se passe bien du
                // premier coup : sans ça, un démarrage réussi ne laisse aucune
                // trace et on ne sait pas distinguer « ouvert » de « jamais
                // tenté » en relisant le journal.
                Log.Write("Bluetooth : flux audio ouvert");
                _lastOpenIssue = null;
                // Sans attendre StateChanged : une perte ultérieure doit être
                // reconnue comme telle même si l'événement manquait à l'appel.
                if (_devices.FirstOrDefault(d => d.Id == id) is { } dev)
                    OnConnectionStateChanged(dev, connection);
                return;
            }
            NoteOpenIssue(result.Status.ToString());
        }
        catch (Exception ex)
        {
            // Le téléphone n'est pas encore connecté — on réessaie au prochain
            // tick. On journalise quand même, mais une seule fois par cause :
            // la tentative revient toutes les 2 s et noierait le journal.
            if (IsCurrent(id, connection)) NoteOpenIssue(ex.Message);
        }
        finally
        {
            if (_opening == connection) _opening = null;
        }
    }

    // Dernière cause d'échec d'ouverture déjà journalisée, pour ne l'écrire
    // qu'une fois tant qu'elle ne change pas.
    private string? _lastOpenIssue;

    private void NoteOpenIssue(string issue)
    {
        if (_lastOpenIssue == issue) return;
        _lastOpenIssue = issue;
        Log.Write($"Bluetooth : ouverture du flux impossible — {issue}");
    }

    /// <summary>Libère la connexion de l'appareil sans toucher à l'interface.</summary>
    private void DisposeConnection(string id)
    {
        if (!_connections.Remove(id, out var connection)) return;
        try { connection.Dispose(); }
        catch (Exception ex) { Log.Write($"Bluetooth : libération de la connexion — {ex.Message}"); }
    }

    private void CloseConnection(string id)
    {
        _retryTimer?.Stop();
        DisposeConnection(id);
        if (_activeId == id) _activeId = null;
        var dev = _devices.FirstOrDefault(d => d.Id == id);
        if (dev != null)
        {
            dev.Status = "";
            dev.IsActive = false;
        }
        UpdateTrayIcon();
    }

    /// <summary>
    /// Le logo de la zone de notification prend ses couleurs dès qu'un autre
    /// appareil est réellement relié : téléphone connecté (et pas seulement
    /// attendu), émetteur réseau reçu, ou récepteur réseau joint.
    /// </summary>
    private void UpdateTrayIcon()
    {
        bool phone = _activeId != null
            && _connections.TryGetValue(_activeId, out var connection)
            && connection.State == AudioPlaybackConnectionState.Opened;
        _tray.SetConnected(phone || _net.IsReceiving || _sender.IsStreaming);
        // Mêmes moments que l'icône : la tuile du téléphone suit l'état réel.
        UpdatePhoneTile();
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
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _airPods.Dispose();
        _airPodsPopup.Destroy();

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
