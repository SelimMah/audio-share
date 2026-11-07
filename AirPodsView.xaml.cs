using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
// Le projet référence WPF ET WinForms (icône tray) : lever les ambiguïtés.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AudioShare;

/// <summary>
/// Bloc AirPods commun à la popup et à la carte de la page principale :
/// vignette du boîtier (voyant coloré, rebond à l'apparition), nom, bouton
/// d'action, batteries en mini-anneaux — ou une ligne d'état à la place des
/// batteries (progression, succès, erreur, texte seul).
/// </summary>
internal sealed partial class AirPodsView : System.Windows.Controls.UserControl
{
    /// <summary>Clic sur le bouton d'action (« Connecter » / « Déconnecter »).</summary>
    public event Action? ActionClicked;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush Red = Frozen(0xFF, 0x45, 0x3A);
    private static readonly Brush Amber = Frozen(0xFF, 0x9F, 0x0A);
    private static readonly Brush Neutral = Frozen(0x63, 0x63, 0x66);

    private readonly ScaleTransform _caseScale = new(1, 1);
    private Canvas _caseGroup = null!;
    private Ellipse _led = null!, _ledGlow = null!;

    public AirPodsView()
    {
        InitializeComponent();
        BuildCase();
    }

    private Brush AppBrush(string key) => (Brush)FindResource(key);

    // ================================================================== API

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>Texte du bouton d'action ; null = bouton masqué.</summary>
    public void SetAction(string? label, bool enabled = true)
    {
        ActionButton.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = label ?? "";
        ActionButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Ligne d'état à la place des batteries. <paramref name="kind"/> null =
    /// texte seul, sans icône ; <paramref name="text"/> null = retour aux batteries.
    /// </summary>
    public void SetStatus(AirPodsMessageKind? kind, string? text)
    {
        if (text == null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            SetSpinner(false);
            UpdateBatteryVisibility();
            return;
        }
        StatusPanel.Visibility = Visibility.Visible;
        BatteryRow.Visibility = Visibility.Collapsed;
        StatusText.Text = text;

        bool progress = kind == AirPodsMessageKind.Progress;
        StatusIcon.Visibility = kind == null ? Visibility.Collapsed : Visibility.Visible;
        SetSpinner(progress);
        StatusBadge.Visibility = progress ? Visibility.Collapsed : Visibility.Visible;
        (StatusBadge.Background, StatusGlyph.Text) = kind switch
        {
            AirPodsMessageKind.Success => (AppBrush("Green"), ""),  // coche
            AirPodsMessageKind.Error => (Red, ""),                  // croix
            _ => (Neutral, ""),                                      // info
        };
    }

    private bool _hasBattery;

    public void SetBattery(AirPodsBattery? battery)
    {
        _hasBattery = battery is { IsEmpty: false };
        UpdateBatteryVisibility();
        if (!_hasBattery) return;
        SetRing(LeftRing, LeftText, battery!.Left, battery.LeftCharging, Loc.T("L", "G"));
        SetRing(RightRing, RightText, battery.Right, battery.RightCharging, Loc.T("R", "D"));
        SetRing(CaseRing, CaseText, battery.Case, battery.CaseCharging, Loc.T("Case", "Boîtier"));
    }

    private void UpdateBatteryVisibility() =>
        BatteryRow.Visibility = _hasBattery && StatusPanel.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

    public enum Led { White, Green, Amber }

    /// <summary>Voyant du boîtier : couleur, et pulsation (null = fixe).</summary>
    public void SetLed(Led led, TimeSpan? pulse = null)
    {
        var color = led switch { Led.Green => AppBrush("Green"), Led.Amber => Amber, _ => Brushes.White };
        _led.Fill = color;
        var c = (color as SolidColorBrush)?.Color ?? Colors.White;
        _ledGlow.Fill = new RadialGradientBrush(Color.FromArgb(0x90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
        if (pulse is TimeSpan period)
        {
            var anim = new DoubleAnimation(1, 0.25, period)
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            _led.BeginAnimation(OpacityProperty, anim);
            _ledGlow.BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            _led.BeginAnimation(OpacityProperty, null);
            _ledGlow.BeginAnimation(OpacityProperty, null);
            _led.Opacity = _ledGlow.Opacity = 1;
        }
    }

    /// <summary>Le boîtier se pose avec un léger rebond.</summary>
    public void PlayIntro()
    {
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        _caseScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = pop });
        _caseScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = pop });
        _caseGroup.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>Plus rien ne tourne (fenêtre masquée).</summary>
    public void StopLoops()
    {
        _led.BeginAnimation(OpacityProperty, null);
        _ledGlow.BeginAnimation(OpacityProperty, null);
        SetSpinner(false);
    }

    // ============================================================ Interne

    private void Action_Click(object sender, RoutedEventArgs e) => ActionClicked?.Invoke();

    /// <summary>Mini-anneau + « G 90 % » (éclair si en charge, « — » si inconnu).</summary>
    private void SetRing(RingGauge ring, TextBlock text, int? level, bool charging, string name)
    {
        var green = AppBrush("Green");
        text.Inlines.Clear();
        if (charging && level != null)
            text.Inlines.Add(new Run("") { FontFamily = IconFont, Foreground = green, FontSize = 9.5 });
        text.Inlines.Add(new Run(name + " ") { Foreground = AppBrush("LabelSecondary") });

        if (level is not int value)
        {
            text.Inlines.Add(new Run("—") { Foreground = AppBrush("LabelTertiary") });
            ring.BeginAnimation(RingGauge.ValueProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            return;
        }
        text.Inlines.Add(new Run(Loc.T($"{value}%", $"{value} %")));
        ring.RingBrush = value <= 20 && !charging ? Red : green;
        ring.BeginAnimation(RingGauge.ValueProperty,
            new DoubleAnimation(value / 100.0, TimeSpan.FromMilliseconds(700))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void SetSpinner(bool on)
    {
        Spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, on
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    /// <summary>Boîtier des AirPods Pro, vu de face : coque blanche, jointure du couvercle, voyant.</summary>
    private void BuildCase()
    {
        const double cw = 144, ch = 100, cr = 34;
        _caseGroup = new Canvas
        {
            Width = cw, Height = ch,
            RenderTransform = _caseScale,
            RenderTransformOrigin = new Point(0.5, 1),
            Clip = new RectangleGeometry(new Rect(0, 0, cw, ch), cr, cr),
        };
        var body = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xEE, 0xEE, 0xF1), 0.45));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0xBE, 0xBE, 0xC4), 1));
        body.Freeze();
        _caseGroup.Children.Add(new Border
        {
            Width = cw, Height = ch, CornerRadius = new CornerRadius(cr),
            Background = body,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0xA2)),
            BorderThickness = new Thickness(0.8),
        });
        // Reflet du couvercle
        _caseGroup.Children.Add(new Rectangle
        {
            Width = cw, Height = 34,
            Fill = new LinearGradientBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90),
        });
        // Jointure du couvercle
        var seam = new Rectangle { Width = cw, Height = 1.6, Fill = new SolidColorBrush(Color.FromRgb(0xA4, 0xA4, 0xAA)) };
        Canvas.SetTop(seam, 34);
        _caseGroup.Children.Add(seam);
        // Voyant (halo + point)
        _ledGlow = new Ellipse { Width = 22, Height = 22 };
        Canvas.SetLeft(_ledGlow, cw / 2 - 11);
        Canvas.SetTop(_ledGlow, 55);
        _caseGroup.Children.Add(_ledGlow);
        _led = new Ellipse
        {
            Width = 9, Height = 9, Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), StrokeThickness = 0.6,
        };
        Canvas.SetLeft(_led, cw / 2 - 4.5);
        Canvas.SetTop(_led, 61.5);
        _caseGroup.Children.Add(_led);

        Canvas.SetLeft(_caseGroup, (Scene.Width - cw) / 2);
        Canvas.SetTop(_caseGroup, (Scene.Height - ch) / 2);
        Scene.Children.Add(_caseGroup);
        SetLed(Led.White);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
