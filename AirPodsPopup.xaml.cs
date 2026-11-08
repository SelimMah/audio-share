using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AudioShare;

/// <summary>État affiché par la fenêtre AirPods (mêmes couleurs que la tuile).</summary>
internal enum PodsCardState { Prompt, Connecting, Connected, Failed, Info }

/// <param name="Text">Échec : le conseil affiché à la place des niveaux ; Info : le statut lui-même.</param>
internal sealed record PodsCard(string Title, PodsCardState State, string? Text, AirPodsBattery? Battery);

/// <summary>
/// Fenêtre AirPods de la charte (design/README.md) : 336 × 92 sur acrylique,
/// boîtier au trait qui déborde en haut à droite, nom, statut avec une
/// petite icône d'action ronde, niveaux G / D / B en bas. Toute la fenêtre
/// est cliquable pour l'action principale (connecter, réessayer). États :
/// proposition (verre), connexion en cours (orange), connectés (vert),
/// échec (rouge) — en fondu croisé de l'un à l'autre.
///
/// Apparaît comme le flyout (glissement vers le haut + fondu) et repart en
/// fondu. Ne prend jamais le focus ; un clic n'importe où en dehors la ferme
/// (crochet souris bas niveau, posé seulement pendant l'affichage).
/// </summary>
internal sealed partial class AirPodsPopup : Window
{
    private const double W = 336, H = 92;

    /// <summary>Fenêtre touchée en proposition ou en échec, ou ↻ : connecter.</summary>
    public event Action? ConnectClicked;
    /// <summary>■ pendant la connexion : annuler.</summary>
    public event Action? CancelClicked;
    /// <summary>Maillon barré une fois connectés : dissocier.</summary>
    public event Action? UnlinkClicked;
    /// <summary>Clic à l'extérieur, ✕ ou expiration d'une proposition (hors aperçu).</summary>
    public event Action? Dismissed;

    /// <summary>Fenêtre au-dessus de laquelle se placer quand elle est ouverte (le flyout).</summary>
    public Window? Anchor { get; set; }

    public bool IsShowingPrompt => _shown && _isPrompt;

    /// <summary>Affiche l'état courant (double-clic, ouverture du boîtier) : suivi en direct.</summary>
    public bool IsShowingLive => _shown && _live != null;

    private readonly DispatcherTimer _autoClose = new();
    private bool _shown;        // affichée (ou en train d'apparaître), pas en train de partir
    private bool _isPrompt;
    private bool _isPreview;    // aperçu depuis les Réglages : un refus ne compte pas
    private Func<PodsCard>? _live;
    private PodsCard? _card;
    private bool _allowClose;

    // ---- Visuel (construit en code, voir BuildCard) ----
    private readonly ScaleTransform _rootScale = new(1, 1);
    private readonly TranslateTransform _rootSlide = new();
    private readonly ScaleTransform _press = new(1, 1);
    private readonly TranslateTransform _shake = new();
    private readonly Grid _surface = new();
    private readonly Dictionary<TileTint, Border> _tints = new();
    private readonly SolidColorBrush _frameBrush = new(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private readonly Border _hover;
    private readonly ArtVisual _art;
    private readonly TranslateTransform _artSlide = new();
    private readonly Canvas _layout = new() { Width = W, Height = H };
    private readonly TextBlock _name, _statusText, _advice;
    private readonly StackPanel _statusRow;
    private readonly TranslateTransform _statusShift = new();
    private readonly SolidColorBrush _dotBrush = new(Palette.BlueLight), _statusBrush = new(Palette.BlueText);
    private readonly Ellipse _dot, _dotGlow;
    private readonly GradientStop _glowCore = new(Colors.Transparent, 0), _glowEdge = new(Colors.Transparent, 1);
    private readonly Canvas _spinner;
    private readonly RotateTransform _spin = new(0, 6, 6);
    private readonly Border _action;
    private readonly ScaleTransform _actionScale = new(1, 1);
    private readonly BatteryBars _bars = new(11, 10) { Width = 196 };
    private readonly Border _close;
    private readonly SolidColorBrush _closeBrush = new(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private readonly Canvas _bursts = new() { IsHitTestVisible = false };
    private bool _pressed;
    private TileTint _tint = TileTint.Glass;

    // Voyant du boîtier, en coordonnées de la fenêtre (boîtier 150 × 103 en
    // (216, −28), voyant en (38, 35) de son repère 76 × 52).
    private static readonly Point Led = new(216 + 38 * 150.0 / 76, -28 + 35 * 150.0 / 76);

    public AirPodsPopup()
    {
        InitializeComponent();

        Root.RenderTransform = new TransformGroup { Children = { _rootScale, _rootSlide } };

        // Couches : teintes d'état, cadre, boîtier, survol, ondes, contenu.
        _surface.RenderTransformOrigin = new Point(0.5, 0.5);
        _surface.RenderTransform = new TransformGroup { Children = { _press, _shake } };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = _frameBrush, IsHitTestVisible = false,
        };
        var shine = new Border
        {
            CornerRadius = new CornerRadius(7), Margin = new Thickness(1), BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Palette.Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false,
        };
        _hover = new Border { Background = Palette.Frozen(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)), Opacity = 0, IsHitTestVisible = false };
        _art = TileArtwork.Build(TileArt.CaseCorner, caseTop: -28)!;
        _art.Root.RenderTransform = _artSlide;
        var artHost = new Grid { IsHitTestVisible = false };
        artHost.Children.Add(_art.Root);

        // Nom
        _name = new TextBlock
        {
            FontFamily = Palette.Body, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.White, MaxWidth = 250,
            TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 20,
        };
        Place(_name, 16, 11);

        // Statut : point (ou anneau de chargement), texte, icône d'action
        _dotGlow = new Ellipse
        {
            Width = 18, Height = 18, Margin = new Thickness(-6),
            Fill = new RadialGradientBrush { GradientStops = { _glowCore, _glowEdge } },
        };
        _dot = new Ellipse { Width = 6, Height = 6, Fill = _dotBrush };
        var dotBox = new Grid { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        dotBox.Children.Add(_dotGlow);
        dotBox.Children.Add(_dot);
        _spinner = new Canvas { Width = 12, Height = 12, RenderTransform = _spin, Visibility = Visibility.Collapsed };
        _spinner.Children.Add(new Ellipse
        {
            Width = 9.2, Height = 9.2, Margin = new Thickness(1.4),
            Stroke = Palette.Frozen(Palette.A(Palette.OrangeText, 0.3)), StrokeThickness = 1.6,
        });
        _spinner.Children.Add(new Path
        {
            Data = Geometry.Parse("M6 1.4 A4.6 4.6 0 0 1 10.6 6"),
            Stroke = Palette.Frozen(Palette.OrangeText), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        var marker = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        marker.Children.Add(dotBox);
        marker.Children.Add(_spinner);
        _statusText = new TextBlock { FontFamily = Palette.Body, FontSize = 12, Foreground = _statusBrush, VerticalAlignment = VerticalAlignment.Center };
        _action = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(8, -3, 0, -3),
            Background = Palette.Frozen(Color.FromArgb(0x47, 0, 0, 0)), Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _actionScale,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        WireRoundButton(_action, _actionScale, () =>
        {
            switch (_card?.State)
            {
                case PodsCardState.Connecting: CancelClicked?.Invoke(); break;
                case PodsCardState.Connected: UnlinkClicked?.Invoke(); break;
                case PodsCardState.Failed: ConnectClicked?.Invoke(); break;
            }
        });
        _statusRow = new StackPanel { Orientation = Orientation.Horizontal, RenderTransform = _statusShift };
        _statusRow.Children.Add(marker);
        _statusRow.Children.Add(_statusText);
        _statusRow.Children.Add(_action);
        Place(_statusRow, 13, 32);

        // Niveaux, ou conseil après un échec, en bas
        Canvas.SetLeft(_bars, 16);
        Canvas.SetBottom(_bars, 11);
        _layout.Children.Add(_bars);
        _advice = new TextBlock
        {
            FontFamily = Palette.Body, FontSize = 12, LineHeight = 16, Width = 230,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 32,
            Foreground = Palette.Frozen(Color.FromArgb(0xBF, 0xEB, 0xEB, 0xF5)), Visibility = Visibility.Collapsed,
        };
        Canvas.SetLeft(_advice, 16);
        Canvas.SetBottom(_advice, 11);
        _layout.Children.Add(_advice);

        // ✕ : ferme seulement
        var closeScale = new ScaleTransform(1, 1);
        _close = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = _closeBrush, Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = closeScale,
            Child = new TextBlock
            {
                Text = "", FontFamily = Palette.Icons, FontSize = 9, Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        WireRoundButton(_close, closeScale, () => HideCore(dismissed: _isPrompt));
        Place(_close, W - 14 - 24, 11);

        _surface.Children.Add(frame);
        _surface.Children.Add(shine);
        _surface.Children.Add(artHost);
        _surface.Children.Add(_hover);
        _surface.Children.Add(_bursts);
        _surface.Children.Add(_layout);
        Root.Children.Add(_surface);

        // Toute la fenêtre est le bouton de l'action principale.
        Root.MouseEnter += (_, _) => { if (PrimaryAllowed) Palette.Fade(_hover, 1, 140); };
        Root.MouseLeave += (_, _) => Palette.Fade(_hover, 0, 260);
        Root.MouseLeftButtonDown += (_, e) =>
        {
            if (!PrimaryAllowed) return;
            _pressed = true;
            Root.CaptureMouse();
            Palette.ScaleTo(_press, 0.975, 100, Palette.Out);
            e.Handled = true;
        };
        Root.MouseLeftButtonUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            bool inside = Root.IsMouseOver;
            Root.ReleaseMouseCapture();
            Palette.ScaleTo(_press, 1, 380, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });
            e.Handled = true;
            if (inside && PrimaryAllowed) ConnectClicked?.Invoke();
        };
        Root.LostMouseCapture += (_, _) =>
        {
            if (!_pressed) return;
            _pressed = false;
            Palette.ScaleTo(_press, 1, 300, Palette.Out);
        };

        _autoClose.Tick += (_, _) =>
        {
            // On ne ferme pas sous le curseur : l'utilisateur est peut-être en train de cliquer.
            if (IsMouseOver) return;
            HideCore(dismissed: _isPrompt);
        };

        // Acrylique permanent (la fenêtre n'est jamais active), coins arrondis,
        // fenêtre outil non activable.
        SourceInitialized += (_, _) =>
        {
            WindowEffects.ApplyPopup(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TouchInputHook);
        };
        // Toucher sur la popup elle-même (voir TouchInputHook).
        PreviewStylusDown += (_, _) => { _touchInside = true; _lastInsideTouch = DateTime.UtcNow; };
        PreviewStylusUp += (_, _) => _touchInside = false;
        LostStylusCapture += (_, _) => _touchInside = false;
        // Alt+F4 & co. : on masque au lieu de détruire.
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            HideCore(dismissed: _isPrompt);
        };
    }

    private void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        _layout.Children.Add(element);
    }

    /// <summary>Petit bouton rond (✕, action) : survol, pression, rebond, sans laisser le clic atteindre la fenêtre.</summary>
    private static void WireRoundButton(Border button, ScaleTransform scale, Action onClick)
    {
        bool down = false;
        button.MouseEnter += (_, _) => { if (!down) Palette.ScaleTo(scale, 1.1, 180, Palette.Out); };
        button.MouseLeave += (_, _) => Palette.ScaleTo(scale, 1, 250, Palette.Out);
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            down = true;
            button.CaptureMouse();
            Palette.ScaleTo(scale, 0.82, 80);
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!down) return;
            down = false;
            bool inside = button.IsMouseOver;
            button.ReleaseMouseCapture();
            Palette.ScaleTo(scale, inside ? 1.1 : 1, 320, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 });
            if (inside) onClick();
        };
        button.LostMouseCapture += (_, _) => down = false;
    }

    private bool PrimaryAllowed => _card?.State is PodsCardState.Prompt or PodsCardState.Failed;

    // ================================================================== API

    /// <param name="preview">Aperçu (bouton des Réglages) : la fermeture ne compte pas comme un refus.</param>
    public void ShowPrompt(AirPodsPrompt prompt, bool preview = false)
    {
        _isPrompt = true;
        _isPreview = preview;
        _live = null;
        Present(new PodsCard(prompt.Title, PodsCardState.Prompt, null, prompt.Battery), prompt.TimeoutMs);
    }

    public void ShowMessage(AirPodsMessage message)
    {
        _isPrompt = false;
        _isPreview = false;
        _live = null;
        var state = message.Kind switch
        {
            AirPodsMessageKind.Progress => PodsCardState.Connecting,
            AirPodsMessageKind.Success => PodsCardState.Connected,
            AirPodsMessageKind.Error => PodsCardState.Failed,
            _ => PodsCardState.Info,
        };
        Present(new PodsCard(message.Title, state, message.Text, message.Battery), message.AutoCloseMs);
    }

    /// <summary>
    /// État courant des AirPods, fourni par <paramref name="source"/> et
    /// suivi en direct (RefreshLive). Jamais compté comme un refus.
    /// </summary>
    public void ShowLive(Func<PodsCard> source, int autoCloseMs = 0)
    {
        _isPrompt = false;
        _isPreview = false;
        _live = source;
        Present(source(), autoCloseMs);
    }

    /// <summary>L'état a changé : la fenêtre ouverte en mode « état courant » suit.</summary>
    public void RefreshLive()
    {
        if (_shown && _live != null) Apply(_live(), animate: true);
    }

    /// <summary>Batterie mise à jour en direct pendant une proposition.</summary>
    public void UpdateBattery(AirPodsBattery? battery)
    {
        if (!_shown || !_isPrompt || battery == null || _card == null) return;
        _card = _card with { Battery = battery };
        _bars.SetLevels(battery, animate: true);
        _bars.Visibility = battery.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Masque sans compter comme un refus (la proposition n'a plus lieu d'être).</summary>
    public void HideNow() => HideCore(dismissed: false);

    /// <summary>Ferme pour de bon (fermeture de l'app).</summary>
    public void Destroy()
    {
        _autoClose.Stop();
        RemoveMouseHook();
        RemoveTouchWatch();
        _allowClose = true;
        Close();
    }

    // ============================================================== États

    private void Present(PodsCard card, int closeAfterMs)
    {
        Apply(card, animate: _shown);
        ShowCore(closeAfterMs);
    }

    /// <summary>Habille la fenêtre pour <paramref name="card"/>, en fondu croisé si elle est déjà visible.</summary>
    private void Apply(PodsCard card, bool animate)
    {
        var previous = _card;
        _card = card;
        _name.Text = card.Title;

        var tint = card.State switch
        {
            PodsCardState.Connecting => TileTint.Orange,
            PodsCardState.Connected => TileTint.Green,
            PodsCardState.Failed => TileTint.Red,
            _ => TileTint.Glass,
        };
        SetTint(tint, animate);

        // Statut : point (bleu « toucher pour connecter », gris pour une
        // simple info) ou anneau de chargement pendant la connexion.
        var (dot, text) = card.State switch
        {
            PodsCardState.Prompt => (Palette.BlueLight, Palette.BlueText),
            PodsCardState.Info => (Palette.IdleDot, Palette.Secondary),
            _ => (Palette.Dot(tint), Palette.Text(tint)),
        };
        Palette.ColorTo(_dotBrush, dot, animate);
        Palette.ColorTo(_statusBrush, text, animate);
        _glowCore.Color = Palette.A(dot, card.State == PodsCardState.Info ? 0 : 0.6);
        _glowEdge.Color = Palette.A(dot, 0);
        string status = card.State switch
        {
            PodsCardState.Prompt => Loc.T("Tap to connect", "Toucher pour connecter"),
            PodsCardState.Connecting => Loc.T("Connecting…", "Connexion en cours…"),
            PodsCardState.Connected => Loc.T("Connected", "Connectés"),
            PodsCardState.Failed => Loc.T("Failed", "Échec"),
            _ => card.Text ?? "",
        };
        bool connecting = card.State == PodsCardState.Connecting;
        _spinner.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
        _dot.Visibility = _dotGlow.Visibility = connecting ? Visibility.Collapsed : Visibility.Visible;
        SetSpinner(connecting && _shown);

        // Icône d'action : ■ annuler, maillon barré dissocier, ↻ réessayer.
        var glyph = card.State switch
        {
            PodsCardState.Connecting => StopGlyph(),
            PodsCardState.Connected => UnlinkGlyph(),
            PodsCardState.Failed => RetryGlyph(),
            _ => null,
        };
        bool sameState = previous?.State == card.State;
        if (glyph == null) _action.Visibility = Visibility.Collapsed;
        else if (!sameState || _action.Visibility != Visibility.Visible)
        {
            _action.Child = glyph;
            _action.Visibility = Visibility.Visible;
            _action.ToolTip = card.State switch
            {
                PodsCardState.Connecting => Loc.T("Cancel", "Annuler"),
                PodsCardState.Connected => Loc.T("Disconnect", "Dissocier"),
                _ => Loc.T("Try again", "Réessayer"),
            };
            if (animate)
            {
                var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 1 } };
                _actionScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                _actionScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }

        if (_statusText.Text != status)
        {
            _statusText.Text = status;
            if (animate)
            {
                _statusRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
                _statusShift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = Palette.Out });
            }
        }

        // Bas : conseil après un échec, sinon les niveaux (s'ils sont connus).
        bool failed = card.State == PodsCardState.Failed;
        bool hasBattery = card.Battery is { IsEmpty: false };
        _advice.Text = failed ? card.Text ?? "" : "";
        Swap(_advice, failed, animate);
        Swap(_bars, !failed && hasBattery, animate);
        _bars.SetColors(
            tint == TileTint.Glass ? Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x3D, 0, 0, 0),
            tint == TileTint.Glass ? Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF) : Colors.White,
            tint == TileTint.Glass ? Palette.Secondary : Color.FromArgb(0xB3, 0xEB, 0xEB, 0xF5), animate);
        if (hasBattery) _bars.SetLevels(card.Battery, animate || !_shown);
        // Pendant la connexion, les niveaux passent au second plan.
        double barsOpacity = connecting ? 0.55 : 1;
        if (_bars.Visibility == Visibility.Visible && !animate) _bars.Opacity = barsOpacity;
        else if (_bars.Visibility == Visibility.Visible) _bars.BeginAnimation(OpacityProperty, Palette.Anim(barsOpacity, 300));

        Palette.ColorTo(_closeBrush, tint == TileTint.Glass ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x40, 0, 0, 0), animate);
        Root.Cursor = PrimaryAllowed ? Cursors.Hand : null;
        if (!PrimaryAllowed) Palette.Fade(_hover, 0, 200);
        UpdateLoops();

        if (!animate || sameState) return;
        // Les moments forts : une onde quand ça se connecte, un tremblement quand ça échoue.
        if (card.State == PodsCardState.Connected) Burst(Palette.Green);
        else if (failed) Shake();
    }

    private void SetTint(TileTint tint, bool animate)
    {
        _tint = tint;
        if (tint != TileTint.Glass && !_tints.ContainsKey(tint))
        {
            var (top, bottom, _) = Palette.Strength(tint);
            var c = Palette.Base(tint);
            // Fenêtre : dégradé plus appuyé en haut, presque nul en bas (maquette).
            var layer = new Border
            {
                Background = new LinearGradientBrush(Palette.A(c, top), Palette.A(c, Math.Min(bottom, 0.06)),
                    new Point(0.33, 0.03), new Point(0.67, 0.97)),
                Opacity = 0, IsHitTestVisible = false,
            };
            _tints[tint] = layer;
            _surface.Children.Insert(0, layer);
        }
        foreach (var (key, layer) in _tints)
        {
            double target = key == tint ? 1 : 0;
            if (animate) layer.BeginAnimation(OpacityProperty, Palette.Anim(target, 380, Palette.Out));
            else { layer.BeginAnimation(OpacityProperty, null); layer.Opacity = target; }
        }
        var frame = tint == TileTint.Glass
            ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
            : Palette.A(Palette.Base(tint), tint == TileTint.Orange ? 0.55 : 0.5);
        Palette.ColorTo(_frameBrush, frame, animate);
        _art.SetAccent(tint, animate);
    }

    /// <summary>Fondu d'un bloc du bas (niveaux / conseil) qui apparaît ou disparaît.</summary>
    private static void Swap(FrameworkElement element, bool show, bool animate)
    {
        bool visible = element.Visibility == Visibility.Visible;
        if (show == visible) return;
        if (!animate)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (show)
        {
            element.Visibility = Visibility.Visible;
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        }
        else
        {
            var fade = Palette.Anim(0, 150);
            fade.Completed += (_, _) => { if (element.Opacity == 0) element.Visibility = Visibility.Collapsed; };
            element.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>Boucles tant que la fenêtre est visible : halo du boîtier qui respire, lueur du point.</summary>
    private void UpdateLoops()
    {
        bool on = _shown && _card?.State is PodsCardState.Prompt or PodsCardState.Connecting or PodsCardState.Connected;
        if (on)
        {
            _art.StartLoops();
            _dotGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(1100))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
        else
        {
            _art.StopLoops();
            _dotGlow.BeginAnimation(OpacityProperty, null);
        }
        SetSpinner(_shown && _card?.State == PodsCardState.Connecting);
    }

    private void SetSpinner(bool on) =>
        _spin.BeginAnimation(RotateTransform.AngleProperty, on
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);

    /// <summary>Onde de couleur qui part du voyant du boîtier.</summary>
    private void Burst(Color color)
    {
        const double r = 70;
        var scale = new ScaleTransform(0.1, 0.1);
        var ring = new Ellipse
        {
            Width = 2 * r, Height = 2 * r, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Palette.A(color, 0.0), 0),
                    new GradientStop(Palette.A(color, 0.45), 0.72),
                    new GradientStop(Palette.A(color, 0), 1),
                },
            },
        };
        Canvas.SetLeft(ring, Led.X - r);
        Canvas.SetTop(ring, Led.Y - r);
        _bursts.Children.Add(ring);
        var grow = new DoubleAnimation(0.1, 1.6, TimeSpan.FromMilliseconds(760)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(800)) { EasingFunction = Palette.Out };
        fade.Completed += (_, _) => _bursts.Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Petit « non » de la tête : la fenêtre tremble latéralement.</summary>
    private void Shake()
    {
        var shake = new DoubleAnimationUsingKeyFrames();
        double[] xs = { -6, 5, -4, 3, -1.5, 0 };
        for (int i = 0; i < xs.Length; i++)
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], TimeSpan.FromMilliseconds(55 * (i + 1)), new SineEase()));
        _shake.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    // ---- Pictogrammes des icônes d'action (tracés de la maquette, repère 24) ----

    private static UIElement StopGlyph() => new System.Windows.Shapes.Rectangle
    {
        Width = 8, Height = 8, RadiusX = 1.8, RadiusY = 1.8, Fill = System.Windows.Media.Brushes.White,
    };

    private static UIElement UnlinkGlyph() => StrokeGlyph(2.4,
        "M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1",
        "M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1",
        "M4 4 L20 20");

    private static UIElement RetryGlyph() => StrokeGlyph(2.6, "M20 12a8 8 0 1 1-2.5-5.8M20 4v5h-5");

    private static UIElement StrokeGlyph(double thickness, params string[] paths)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var d in paths)
        {
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse(d), Stroke = System.Windows.Media.Brushes.White, StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            });
        }
        return new Viewbox { Width = 12, Height = 12, Child = canvas };
    }

    // ============================================================ Affichage

    private void ShowCore(int closeAfterMs)
    {
        _autoClose.Stop();
        if (closeAfterMs > 0)
        {
            _autoClose.Interval = TimeSpan.FromMilliseconds(closeAfterMs);
            _autoClose.Start();
        }

        if (_shown)
        {
            Place();
            UpdateLoops();
            return;
        }

        _shown = true;
        WindowEffects.ApplyVeil(this); // suit le réglage « mode sombre »
        if (!IsVisible) Show();
        Place();
        AnimateIn();
        UpdateLoops();
        InstallMouseHook();
        InstallTouchWatch();
    }

    private void HideCore(bool dismissed)
    {
        if (!_shown) return;
        _shown = false;
        _live = null;
        bool countsAsRefusal = dismissed && _isPrompt && !_isPreview;
        _isPrompt = false;
        _autoClose.Stop();
        RemoveMouseHook();
        RemoveTouchWatch();
        AnimateOut();
        if (countsAsRefusal) Dismissed?.Invoke();
    }

    /// <summary>En bas à droite (comme le flyout), ou juste au-dessus du flyout s'il est ouvert.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - W - 12;
        double bottom = area.Bottom - 12;
        if (Anchor is { IsVisible: true } anchor && anchor.ActualHeight > 1)
            bottom = Math.Min(bottom, anchor.Top - 10);
        Top = Math.Max(area.Top + 12, bottom - H);
    }

    /// <summary>
    /// Entrée : la fenêtre monte et se pose (léger rebond d'échelle), le
    /// boîtier glisse depuis la droite, puis nom, statut et niveaux arrivent
    /// l'un après l'autre.
    /// </summary>
    private void AnimateIn()
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        _rootSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        var settle = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(460))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        _artSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(640)) { EasingFunction = ease });
        _art.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));

        int rank = 0;
        foreach (var element in new UIElement[] { _name, _statusRow, _bars, _advice })
        {
            var start = TimeSpan.FromMilliseconds(70 + 55 * rank++);
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.Zero));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, start));
            double final = element == _bars && _card?.State == PodsCardState.Connecting ? 0.55 : 1;
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(final, start + TimeSpan.FromMilliseconds(260)));
            element.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>Le contenu retombe en fondu, puis la fenêtre est masquée.</summary>
    private void AnimateOut()
    {
        _rootSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        Palette.ScaleTo(_rootScale, 0.97, 160);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            if (_shown) return; // réaffichée pendant le départ
            Hide();
            UpdateLoops();
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    // ====================================================== Clic à l'extérieur

    // La fenêtre n'est jamais activée : pas d'événement « Deactivated » sur
    // lequel s'appuyer. Un crochet souris bas niveau voit tous les clics du
    // système ; il n'est posé que pendant l'affichage et ne fait qu'un test
    // de rectangle (le travail est renvoyé au Dispatcher).

    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMButtonDown = 0x0207, WmXButtonDown = 0x020B;

    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseProc; // gardé en champ : sinon collecté par le GC

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookProc;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Write($"AirPods : crochet souris impossible (erreur {Marshal.GetLastWin32Error()})");
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = (int)wParam;
            if (nCode >= 0 && _shown &&
                msg is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!WindowContains(info.pt.X, info.pt.Y))
                    Dispatcher.BeginInvoke(() => HideCore(dismissed: _isPrompt));
            }
        }
        catch { /* un crochet ne doit jamais lever */ }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>Le point (pixels écran) est-il sur la popup ?</summary>
    private bool WindowContains(int x, int y)
    {
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        return x >= topLeft.X && x <= bottomRight.X && y >= topLeft.Y && y <= bottomRight.Y;
    }

    // ==================================================== Toucher à l'extérieur

    // Le tactile ne passe pas par le crochet souris : une fenêtre qui gère
    // les pointeurs (WPF, navigateur, Explorateur…) ne reçoit jamais de
    // clic souris simulé. On écoute donc l'écran tactile lui-même (Raw
    // Input, même en arrière-plan) : à chaque nouveau contact, si la popup
    // n'a pas été touchée dans la foulée, c'est que le doigt était ailleurs.
    // On ne lit pas les rapports (coordonnées propres à chaque écran) :
    // seul leur rythme compte — un écart marque un nouveau contact.

    private const int WmInput = 0x00FF;
    private const uint RidevInputSink = 0x00000100, RidevRemove = 0x00000001;
    private const ushort HidUsagePageDigitizer = 0x0D, HidUsageTouchScreen = 0x04, HidUsagePen = 0x02;

    private bool _touchWatch;
    private bool _touchInside;
    private DateTime _lastInsideTouch, _lastTouchReport, _shownAt;

    private void InstallTouchWatch()
    {
        _shownAt = _lastTouchReport = DateTime.UtcNow; // contact en cours (le tap qui a ouvert) : pas nouveau
        if (_touchWatch) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        _touchWatch = RegisterDigitizers(RidevInputSink, hwnd);
        if (!_touchWatch)
            Log.Write($"AirPods : écoute tactile impossible (erreur {Marshal.GetLastWin32Error()})");
    }

    private void RemoveTouchWatch()
    {
        if (!_touchWatch) return;
        RegisterDigitizers(RidevRemove, IntPtr.Zero);
        _touchWatch = false;
        _touchInside = false;
    }

    private static bool RegisterDigitizers(uint flags, IntPtr hwnd)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = HidUsagePageDigitizer, usUsage = HidUsageTouchScreen, dwFlags = flags, hwndTarget = hwnd },
            new RAWINPUTDEVICE { usUsagePage = HidUsagePageDigitizer, usUsage = HidUsagePen, dwFlags = flags, hwndTarget = hwnd },
        };
        return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private IntPtr TouchInputHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Pas « handled » : DefWindowProc doit libérer l'entrée brute.
        if (msg != WmInput || !_shown || !_touchWatch) return IntPtr.Zero;

        var now = DateTime.UtcNow;
        bool newContact = (now - _lastTouchReport).TotalMilliseconds > 120;
        _lastTouchReport = now;
        if (!newContact || (now - _shownAt).TotalMilliseconds < 300) return IntPtr.Zero;

        // Laisse à WPF le temps de livrer le toucher à la popup, s'il est dessus.
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (!_shown || _touchInside || _lastInsideTouch >= now.AddMilliseconds(-60)) return;
            HideCore(dismissed: _isPrompt);
        };
        check.Start();
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
