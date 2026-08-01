using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
// Le projet référence WPF ET WinForms (icône tray) : lever les ambiguïtés.
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;

namespace AudioShare;

/// <summary>
/// Aperçu de volume façon Windows 11 : pendant l'émission les touches volume
/// sont interceptées, l'OSD système ne s'affiche donc plus — celui-ci le
/// remplace. Petite pilule en bas au centre de l'écran, jamais activée (pas
/// de vol de focus), qui s'estompe toute seule.
/// </summary>
internal sealed class VolumeOsd : Window
{
    private const double TrackWidth = 170;

    private readonly TextBlock _icon;
    private readonly Slider _bar;
    private readonly TextBlock _percent;
    private readonly DispatcherTimer _hide;

    public VolumeOsd()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        // Sans vitre étendue (GlassFrameThickness -1), WPF peint un fond
        // opaque et l'acrylique DWM reste invisible — même recette que le
        // flyout principal.
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
        });
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var iconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var label = Res<Brush>("Label", Brushes.White);

        _icon = new TextBlock
        {
            FontFamily = iconFont, FontSize = 16, Foreground = label,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
        };
        // Le même curseur que dans l'app (style IosSlider), en lecture seule :
        // l'aperçu a exactement le rendu du contrôle de volume de l'app.
        _bar = new Slider
        {
            Width = TrackWidth,
            IsHitTestVisible = false,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Application.Current.TryFindResource("IosSlider") is Style ios)
            _bar.Style = ios;
        _percent = new TextBlock
        {
            FontFamily = Res<FontFamily>("Body", new FontFamily("Segoe UI Variable Text")),
            FontSize = 13, Foreground = label, Width = 30,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 18, 14) };
        row.Children.Add(_icon);
        row.Children.Add(_bar);
        row.Children.Add(_percent);
        Content = row;

        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _hide.Tick += (_, _) => FadeOut();

        SourceInitialized += (_, _) => WindowEffects.ApplyOsd(this);
    }

    private static T Res<T>(string key, T fallback) where T : class =>
        Application.Current.TryFindResource(key) as T ?? fallback;

    public void ShowVolume(float volume)
    {
        _icon.Text = volume switch
        {
            <= 0f => "",   // muet
            < 0.34f => "",
            < 0.67f => "",
            _ => "",
        };
        _bar.Value = Math.Clamp(volume, 0f, 1f);
        _percent.Text = Math.Round(volume * 100).ToString();

        var area = SystemParameters.WorkArea;
        // La taille suit le contenu : forcer une mesure avant de positionner.
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        UpdateLayout();
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 24;

        BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _hide.Stop();
        _hide.Start();
    }

    private void FadeOut()
    {
        _hide.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => { if (Opacity < 0.05) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    public void HideNow()
    {
        _hide.Stop();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }
}
