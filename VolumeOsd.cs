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
    private readonly Border _fill;
    private readonly TextBlock _percent;
    private readonly DispatcherTimer _hide;

    public VolumeOsd()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var iconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var label = Res<Brush>("Label", Brushes.White);
        var accent = Res<Brush>("Accent", new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

        _icon = new TextBlock
        {
            FontFamily = iconFont, FontSize = 16, Foreground = label,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
        };
        var track = new Border
        {
            Width = TrackWidth, Height = 4, CornerRadius = new CornerRadius(2),
            Background = Res<Brush>("SegmentBed", new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fill = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Background = accent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        track.Child = _fill;
        _percent = new TextBlock
        {
            FontFamily = Res<FontFamily>("Body", new FontFamily("Segoe UI Variable Text")),
            FontSize = 13, Foreground = label, Width = 30,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 18, 14) };
        row.Children.Add(_icon);
        row.Children.Add(track);
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
        _fill.Width = TrackWidth * Math.Clamp(volume, 0f, 1f);
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
