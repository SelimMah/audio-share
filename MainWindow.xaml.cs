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

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;

        // Même ordre que DuckLogo.Draw : bec et aile dessous, corps par-dessus.
        LogoBeak.Data = Geometry.Parse(DuckLogo.BeakPath);
        LogoWing.Data = Geometry.Parse(DuckLogo.WingPath);
        LogoBody.Data = Geometry.Parse(DuckLogo.BodyPath);
        LogoBox.RenderTransform = new TransformGroup { Children = { _logoPop, _logoTilt, _logoHop } };
        LogoHost.MouseEnter += (_, _) => WaddleLogo();

        AttachSlidingPill(BalanceSegments);
        AttachSlidingPill(LangSegments);
        // Le contenu déplié pousse le bas de la page : on le suit, pour que
        // ce qui apparaît reste en vue pendant toute l'ouverture.
        AdvancedHost.SizeChanged += (_, _) => { if (_advancedOpen) PageScroll.ScrollToEnd(); };

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
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Vumètre : rafraîchi seulement quand le flyout est visible — l'app
        // passe sa vie cachée, pas de réveil 20 fois par seconde pour rien.
        // L'aurore est clippée aux coins arrondis de la carte (rayon du style
        // Card) : un Border ne rogne pas ses enfants sur les angles, on pose
        // la géométrie à la main à chaque changement de taille.
        VuClip.SizeChanged += (_, _) => VuClip.Clip = new RectangleGeometry(
            new Rect(0, 0, VuClip.ActualWidth, VuClip.ActualHeight), 14, 14);
        CreateVuBlobs();

        _vuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _vuTimer.Tick += (_, _) => UpdateVu();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _vuTimer.Start();
            else { _vuTimer.Stop(); _vuShown = 0; }
        };

        // Repère sonore du volume : au relâchement de la souris, sinon dès que
        // la valeur ne bouge plus (clavier). Tant que le bouton est enfoncé,
        // le minuteur patiente — il rattrape aussi un relâchement hors curseur.
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
        _ = Updater.CheckAndUpdateAsync(msg =>
            Dispatcher.Invoke(() => _tray.Notify("Audio Share", msg)));

        Loaded += (_, _) => AnimateIn();
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
    /// Déplie ou replie les réglages avancés sous leur ligne : la hauteur de
    /// AdvancedHost va de 0 à celle de son contenu. La fenêtre ne bouge pas,
    /// c'est la page qui défile (PageScroll).
    /// </summary>
    private void SetAdvancedOpen(bool open, bool animate)
    {
        _advancedOpen = open;
        AdvancedNavButton.Tag = open ? "open" : null; // chevron, voir le style NavRow
        AdvancedHost.IsHitTestVisible = open;

        double height = 0;
        if (open)
        {
            PopulateOutputCombo();
            PopulateDestCombo();
            _syncingOpus = true;
            OpusToggle.IsChecked = Prefs.OpusEnabled;
            _syncingOpus = false;
            // Mesure à part : dans un hôte de hauteur 0, la taille voulue
            // du contenu serait rabotée à 0.
            AdvancedContent.Measure(new System.Windows.Size(
                AdvancedHost.ActualWidth, double.PositiveInfinity));
            height = AdvancedContent.DesiredSize.Height;
        }

        if (!animate)
        {
            AdvancedHost.BeginAnimation(HeightProperty, null);
            AdvancedHost.BeginAnimation(OpacityProperty, null);
            AdvancedHost.Height = height;
            AdvancedHost.Opacity = open ? 1 : 0;
            return;
        }
        AdvancedHost.BeginAnimation(HeightProperty,
            new DoubleAnimation(height, TimeSpan.FromMilliseconds(340))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        AdvancedHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 340 : 180)));
    }

    private void DiagNav_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(AppPage.Diag, forward: true);

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
        OutputLabel.Text = Loc.T("Received audio output", "Sortie du son reçu");
        OutputRow.ToolTip = Loc.T(
            "Applies to audio received from the network. Bluetooth phone audio always follows Windows' default output.",
            "S'applique au son reçu par le réseau. Le son Bluetooth du téléphone suit toujours la sortie par défaut de Windows.");
        DestLabel.Text = Loc.T("Send to", "Envoyer vers");
        DestRow.ToolTip = Loc.T(
            "Which Audio Share receives this PC's audio. Automatic = the first one found.",
            "Quel Audio Share reçoit le son de ce PC. Automatique = le premier découvert.");
        OpusLabel.Text = Loc.T("Opus compression (Wi-Fi)", "Compression Opus (Wi-Fi)");
        OpusRow.ToolTip = Loc.T(
            "Compresses the sent stream to 256 kbit/s (transparent) — 6x less bandwidth, useful on busy Wi-Fi.",
            "Compresse le flux émis à 256 kbit/s (transparent) — 6x moins de bande passante, utile en Wi-Fi chargé.");
        AdvancedNavLabel.Text = Loc.T("Advanced", "Avancé");
        DiagNavLabel.Text = Loc.T("Diagnostics", "Diagnostic");
        DiagStreamHeader.Text = Loc.T("STREAM", "FLUX");
        DiagLogHeader.Text = Loc.T("LOG", "JOURNAL");
        DiagLatencyLabel.Text = Loc.T("Latency", "Latence");
        DiagRateLabel.Text = Loc.T("Bitrate", "Débit");
        if (_advancedOpen)
        {
            PopulateOutputCombo();
            PopulateDestCombo();
        }

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

        if (_page != AppPage.Main)
        {
            TitleText.Text = TitleFor(_page);
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

    private UIElement PanelFor(AppPage page) => page switch
    {
        AppPage.Main => MainPanel,
        AppPage.Settings => SettingsPanel,
        _ => DiagPanel,
    };

    private string TitleFor(AppPage page) => page switch
    {
        AppPage.Main => "Audio Share",
        AppPage.Settings => Loc.T("Settings", "Réglages"),
        _ => Loc.T("Diagnostics", "Diagnostic"),
    };

    private const double LogoWidth = 44; // largeur de LogoHost dans le XAML

    /// <summary>
    /// Bascule de page avec transition : la page quittée s'efface en glissant
    /// pendant que la nouvelle arrive depuis la droite (navigation avant) ou
    /// la gauche (retour) ; le titre, le logo et le bouton suivent. En
    /// quittant la page principale, la zone défilante est figée à sa hauteur
    /// du moment : le panneau (SizeToContent) garde sa taille sur toutes les
    /// pages, ce qui dépasse défile. <paramref name="animate"/> à false :
    /// bascule sèche, pour un changement fait fenêtre cachée.
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
            _syncingAutostart = true;
            AutostartToggle.IsChecked = IsAutostartEnabled();
            _syncingAutostart = false;
            _syncingLanguage = true;
            LangEnglish.IsChecked = !Loc.French;
            LangFrench.IsChecked = Loc.French;
            _syncingLanguage = false;
            UpdateStatus.Text = Loc.T($"Installed version: {Updater.CurrentVersion}",
                                      $"Version installée : {Updater.CurrentVersion}");
        }
        else if (target == AppPage.Diag)
        {
            UpdateDiagnostics();
        }

        var leaving = PanelFor(_page);
        var arriving = PanelFor(target);
        _page = target;
        TitleText.Text = TitleFor(target);
        bool main = target == AppPage.Main;

        if (animate)
        {
            AnimatePanelOut(leaving, toLeft: forward);
            AnimatePanelIn(arriving, fromRight: forward);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            TitleText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            TitleSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(forward ? 14 : -14, 0, TimeSpan.FromMilliseconds(320))
                { EasingFunction = ease });
            // Le logo se replie hors de la page principale : le titre prend
            // sa place en glissant, et la lui rend au retour.
            LogoHost.BeginAnimation(WidthProperty,
                new DoubleAnimation(main ? LogoWidth : 0, TimeSpan.FromMilliseconds(320))
                { EasingFunction = ease });
            LogoHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(main ? 1 : 0, TimeSpan.FromMilliseconds(main ? 320 : 160)));
            // Le pictogramme change en tournant d'un quart de tour.
            SettingsSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(forward ? -90 : 90, 0, TimeSpan.FromMilliseconds(340))
                { EasingFunction = ease });
            SettingsButton.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        }
        else
        {
            SnapPanel(leaving, visible: false);
            SnapPanel(arriving, visible: true);
            LogoHost.BeginAnimation(WidthProperty, null);
            LogoHost.BeginAnimation(OpacityProperty, null);
            LogoHost.Width = main ? LogoWidth : 0;
            LogoHost.Opacity = main ? 1 : 0;
        }

        SettingsButton.Content = target switch
        {
            AppPage.Main => "",     // engrenage
            AppPage.Settings => "", // croix
            _ => "",                // chevron retour
        };
        SettingsButton.ToolTip = target == AppPage.Main
            ? Loc.T("Settings", "Réglages") : Loc.T("Back", "Retour");
    }

    private static TranslateTransform SlideOf(UIElement panel)
    {
        if (panel.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform();
            panel.RenderTransform = slide;
        }
        return slide;
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
    /// apparaît un instant après le précédent. Images clés plutôt que
    /// BeginTime : avant son départ, une animation différée laisserait voir
    /// le bloc à pleine opacité.
    /// </summary>
    private static void Cascade(UIElement page)
    {
        if (page is not System.Windows.Controls.Panel panel) return;
        int rank = 0;
        foreach (UIElement block in panel.Children)
        {
            if (block.Visibility != Visibility.Visible) continue;
            var start = TimeSpan.FromMilliseconds(32 * rank++);
            var end = start + TimeSpan.FromMilliseconds(260);

            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.Zero));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, start));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, end));
            block.BeginAnimation(OpacityProperty, fade);

            var rise = new DoubleAnimationUsingKeyFrames();
            rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(14, TimeSpan.Zero));
            rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(14, start));
            rise.KeyFrames.Add(new EasingDoubleKeyFrame(0, end,
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            SlideOf(block).BeginAnimation(TranslateTransform.YProperty, rise);
        }
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
    /// Le canard se dandine sous le pointeur : il penche d'un côté puis de
    /// l'autre, de moins en moins, avec deux petits sauts. Un dandinement
    /// en cours va à son terme — repasser dessus ne le fait pas bégayer.
    /// </summary>
    private void WaddleLogo()
    {
        if (_waddling) return;
        _waddling = true;

        static DoubleAnimationUsingKeyFrames Keys(params (double Value, int Ms)[] steps)
        {
            var keys = new DoubleAnimationUsingKeyFrames();
            foreach (var (value, ms) in steps)
                keys.KeyFrames.Add(new EasingDoubleKeyFrame(value, TimeSpan.FromMilliseconds(ms),
                    new SineEase { EasingMode = EasingMode.EaseInOut }));
            return keys;
        }

        var tilt = Keys((-14, 110), (12, 260), (-9, 410), (6, 550), (0, 700));
        tilt.Completed += (_, _) => _waddling = false;
        _logoTilt.BeginAnimation(RotateTransform.AngleProperty, tilt);
        _logoHop.BeginAnimation(TranslateTransform.YProperty,
            Keys((-3, 110), (0, 260), (-2, 410), (0, 550)));
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

    private bool _syncingAutostart;

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

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingAutostart) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            using var approved = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunApprovedPath);
            if (AutostartToggle.IsChecked == true)
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
    /// Audio Share réellement découverts (sonde d'une seconde) en différé.
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
        if (SendToggle.IsChecked == true) _sender.Start();
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

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            await Updater.CheckAndUpdateAsync(
                msg => Dispatcher.Invoke(() => SetTextFaded(UpdateStatus, msg)),
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
            // Latence du tampon en petit : la santé du lien d'un coup d'œil.
            NetworkStatus.Text = _net.LatencyMs is int ms
                ? $"🎵 {_net.SenderName} · {ms} ms"
                : $"🎵 {_net.SenderName}";
            NetworkStatus.Foreground = (System.Windows.Media.Brush)FindResource("Green");
        }
        else
        {
            NetworkStatus.Text = Loc.T("listening", "en écoute");
            NetworkStatus.Foreground = (System.Windows.Media.Brush)FindResource("LabelTertiary");
        }
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

    private void SendToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (SendToggle.IsChecked == true) _sender.Start();
        else _sender.Stop();
        UpdateSendUi();
    }

    private void UpdateSendUi()
    {
        bool statusWasVisible = SendStatus.Visibility == Visibility.Visible;
        SendStatus.Text = _sender.State;
        SendStatus.Visibility = string.IsNullOrEmpty(_sender.State)
            ? Visibility.Collapsed : Visibility.Visible;
        if (!statusWasVisible && SendStatus.Visibility == Visibility.Visible)
            SendStatus.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
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
        public required GradientStop Core, Mid, Edge;
        public double RX, RY, SpeedX, SpeedY, PhaseX, PhaseY, Hue, HueSpeed;
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
        for (int i = 0; i < 5; i++)
        {
            // Trois arrêts : le cœur déjà doux, une mi-course encore présente,
            // un bord totalement transparent — la nappe n'a aucun contour.
            var core = new GradientStop(Colors.Transparent, 0);
            var mid = new GradientStop(Colors.Transparent, 0.55);
            var edge = new GradientStop(Colors.Transparent, 1);
            var pos = new TranslateTransform();
            var shape = new System.Windows.Shapes.Path
            {
                Fill = new RadialGradientBrush { GradientStops = { core, mid, edge } },
                RenderTransform = pos,
            };
            var blob = new VuBlob
            {
                Shape = shape, Pos = pos, Core = core, Mid = mid, Edge = edge,
                RX = 65 + _vuRng.NextDouble() * 55,
                RY = 30 + _vuRng.NextDouble() * 24,
                SpeedX = 0.30 + _vuRng.NextDouble() * 0.55,
                SpeedY = 0.25 + _vuRng.NextDouble() * 0.50,
                PhaseX = _vuRng.NextDouble() * Math.Tau,
                PhaseY = _vuRng.NextDouble() * Math.Tau,
                Hue = _vuRng.NextDouble() * 360,
                HueSpeed = 0.25 + _vuRng.NextDouble() * 0.9,
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
            double r = 1 + 0.34 * Math.Sin(_vuPhase * b.PtFreq[k] + b.PtPhase[k]);
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

    private static System.Windows.Media.Color Hsv(double hue, double sat, double val)
    {
        double c = val * sat;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = val - c;
        var (r, g, b) = ((int)(hue / 60) % 6) switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x),
        };
        return System.Windows.Media.Color.FromRgb(
            (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
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

    /// <summary>
    /// Aurore diffuse sur tout le fond de la carte volume : l'opacité respire
    /// avec le niveau (attaque immédiate, retombée douce) ; chaque nappe
    /// dérive en 2D, se déforme (étirement, rotation) et change lentement de
    /// couleur, le tout d'autant plus vite que le son est fort. Rien ne se
    /// répète jamais — abstrait, sans barres ni chiffres.
    /// </summary>
    private void UpdateVu()
    {
        if (CurrentLevel() is not { } level)
        {
            VuGlow.Opacity = 0;
            _vuShown = 0f;
            return;
        }

        _vuShown = Math.Max(Math.Min(1f, level), _vuShown * 0.86f);
        double vis = Math.Sqrt(_vuShown); // racine : lisible aux niveaux modérés
        VuGlow.Opacity = vis * 0.6;

        _vuPhase += 0.035 + vis * 0.12;
        double w = VuClip.ActualWidth, h = VuClip.ActualHeight;

        foreach (var b in _blobs)
        {
            // Dérive : le centre balaie toute la carte, en X comme en Y.
            b.Pos.X = w * (0.5 + 0.45 * Math.Sin(_vuPhase * b.SpeedX + b.PhaseX));
            b.Pos.Y = h * (0.5 + 0.45 * Math.Sin(_vuPhase * b.SpeedY + b.PhaseY));

            // Morphing : le contour est redessiné à chaque pas, chaque point
            // sur sa propre ondulation — la forme se transforme sans cesse.
            b.Shape.Data = BuildBlobGeometry(b);

            // La teinte glisse en continu ; le bord reste la même couleur en
            // totalement transparent pour un fondu propre entre nappes.
            b.Hue = (b.Hue + b.HueSpeed * (0.4 + vis)) % 360;
            var c = Hsv(b.Hue, 0.72, 1.0);
            b.Core.Color = System.Windows.Media.Color.FromArgb(0x96, c.R, c.G, c.B);
            b.Mid.Color = System.Windows.Media.Color.FromArgb(0x46, c.R, c.G, c.B);
            b.Edge.Color = System.Windows.Media.Color.FromArgb(0x00, c.R, c.G, c.B);
        }
    }

    private void PollPhoneSession()
    {
        // La latence affichée à côté du nom de l'émetteur vit au même rythme,
        // tout comme la page diagnostic quand elle est ouverte.
        if (_net.IsReceiving && IsVisible) UpdateNetworkUi();
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
        if (_page != AppPage.Main) NavigateTo(AppPage.Main, forward: false, animate: false);
        _hiding = false;
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
            DeviceList.SelectedItem = null;
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
            DeviceList.SelectedItem = null;
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

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // ne pas laisser le clic re-sélectionner la ligne
        if (_activeId != null) CloseConnection(_activeId);
        DeviceList.SelectedItem = null;
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
