using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AudioShare;

/// <summary>
/// Anneau de niveau façon iOS (widget Batteries) : piste grise, arc coloré
/// qui part de midi dans le sens horaire. Value va de 0 à 1 et s'anime.
/// </summary>
internal sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Couleur de la piste (partie vide de l'anneau).</summary>
    public Brush Track { get; set; } = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3C)));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public double Thickness { get; set; } = 4.5;

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 52 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 52 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        double radius = (size - Thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        dc.DrawEllipse(null, new System.Windows.Media.Pen(Track, Thickness), center, radius, radius);

        double value = Math.Clamp(Value, 0, 1);
        if (value <= 0.001) return;
        var pen = new System.Windows.Media.Pen(RingBrush, Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        if (value >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        double angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, isLargeArc: value > 0.5,
                      SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
