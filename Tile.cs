using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
// Le projet référence WPF ET WinForms (icône tray) : lever les ambiguïtés.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace AudioShare;

/// <summary>Teinte d'état d'une tuile : verre au repos, couleur quand il se passe quelque chose.</summary>
internal enum TileTint { Glass, Green, Blue, Orange, Red, Purple, Rose }

/// <summary>Illustration au trait du fond d'une tuile (tracés de design/maquettes).</summary>
internal enum TileArt { None, Phone, Globe, Monitor, Case, CaseCorner, Power, Moon, Language, Refresh }

/// <summary>
/// Couleurs de la charte (design/README.md), en code : les tuiles, la
/// fenêtre AirPods et le vumètre les animent, ce que des ressources XAML
/// figées ne permettent pas.
/// </summary>
internal static class Palette
{
    public static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    public static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color BlueLight = Color.FromRgb(0x4D, 0xA3, 0xFF);
    public static readonly Color BlueText = Color.FromRgb(0x7D, 0xBB, 0xFF);
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    public static readonly Color OrangeText = Color.FromRgb(0xFF, 0xB3, 0x40);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x69, 0x61);
    public static readonly Color RedText = Color.FromRgb(0xFF, 0x8A, 0x84);
    public static readonly Color Purple = Color.FromRgb(0xBF, 0x5A, 0xF2);
    public static readonly Color PurpleText = Color.FromRgb(0xD4, 0xA6, 0xFF);
    public static readonly Color Rose = Color.FromRgb(0xFF, 0x64, 0x82);
    public static readonly Color RoseText = Color.FromRgb(0xFF, 0x9A, 0xB0);
    public static readonly Color IdleDot = Color.FromArgb(0x66, 0xEB, 0xEB, 0xF5);
    public static readonly Color Secondary = Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5);

    public static Color A(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    /// <summary>Couleur de fond (dégradé, bordure).</summary>
    public static Color Base(TileTint t) => t switch
    {
        TileTint.Green => Green,
        TileTint.Blue => Blue,
        TileTint.Orange => Orange,
        TileTint.Red => Red,
        TileTint.Purple => Purple,
        TileTint.Rose => Rose,
        _ => Colors.White,
    };

    /// <summary>Point du statut, et halo des illustrations.</summary>
    public static Color Dot(TileTint t) => t switch
    {
        TileTint.Green => Green,
        TileTint.Blue => BlueLight,
        TileTint.Orange => Orange,
        TileTint.Red => Red,
        TileTint.Purple => Purple,
        TileTint.Rose => Rose,
        _ => IdleDot,
    };

    public static Color Text(TileTint t) => t switch
    {
        TileTint.Green => Green,
        TileTint.Blue => BlueText,
        TileTint.Orange => OrangeText,
        TileTint.Red => RedText,
        TileTint.Purple => PurpleText,
        TileTint.Rose => RoseText,
        _ => Secondary,
    };

    /// <summary>Opacités (haut du dégradé, bas, bordure) propres à chaque teinte.</summary>
    public static (double Top, double Bottom, double Border) Strength(TileTint t) => t switch
    {
        TileTint.Green => (0.30, 0.10, 0.45),
        TileTint.Blue => (0.32, 0.10, 0.55),
        TileTint.Orange => (0.30, 0.06, 0.55),
        TileTint.Red => (0.24, 0.04, 0.50),
        TileTint.Purple => (0.32, 0.10, 0.55),
        TileTint.Rose => (0.30, 0.08, 0.55),
        _ => (0, 0, 0),
    };

    /// <summary>Dégradé « 160° » des maquettes : du haut un peu à gauche vers le bas un peu à droite.</summary>
    public static Brush Gradient(TileTint t)
    {
        var (top, bottom, _) = Strength(t);
        var c = Base(t);
        var brush = new LinearGradientBrush(A(c, top), A(c, bottom), new Point(0.33, 0.03), new Point(0.67, 0.97));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Courbe des mouvements « qui se posent » (sortie douce).</summary>
    public static IEasingFunction Out => new CubicEase { EasingMode = EasingMode.EaseOut };

    public static DoubleAnimation Anim(double to, int ms, IEasingFunction? ease = null) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };

    public static void Fade(UIElement element, double to, int ms) =>
        element.BeginAnimation(UIElement.OpacityProperty, Anim(to, ms));

    public static void ScaleTo(ScaleTransform scale, double to, int ms, IEasingFunction? ease = null)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(to, ms, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(to, ms, ease));
    }

    /// <summary>Change la couleur d'une brosse en fondu (ou d'un coup).</summary>
    public static void ColorTo(SolidColorBrush brush, Color to, bool animate, int ms = 300)
    {
        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    /// <summary>
    /// Change un texte avec un fondu qui monte de quelques pixels — s'il
    /// change vraiment. Masque le bloc quand le texte est vide.
    /// </summary>
    public static void SetText(TextBlock block, string? text, bool animate)
    {
        text ??= "";
        block.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (block.Text == text) return;
        block.Text = text;
        if (!animate || text.Length == 0) return;
        if (block.RenderTransform is not TranslateTransform shift)
            block.RenderTransform = shift = new TranslateTransform();
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = Out });
        block.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }
}

/// <summary>
/// Une illustration au trait construite : ses éléments « d'accent » (halo,
/// voyant) prennent la couleur de l'état, et elle peut s'animer en boucle
/// quand la tuile est active (halo qui respire, ondes, rotation).
/// </summary>
internal sealed class ArtVisual
{
    public required FrameworkElement Root;
    /// <summary>Brosses d'accent : opacité au repos, opacité active.</summary>
    public readonly List<(SolidColorBrush Brush, double Idle, double Active)> Accents = new();
    /// <summary>Couleur d'accent au repos (blanc, ou bleu pour l'écran).</summary>
    public Color IdleAccent = Colors.White;
    public ScaleTransform? Halo;
    public readonly List<UIElement> Waves = new();
    public RotateTransform? Spinner;

    public void SetAccent(TileTint tint, bool animate)
    {
        bool active = tint != TileTint.Glass;
        var color = active ? Palette.Dot(tint) : IdleAccent;
        foreach (var (brush, idle, act) in Accents)
            Palette.ColorTo(brush, Palette.A(color, active ? act : idle), animate, 420);
    }

    public void StartLoops()
    {
        if (Halo != null)
        {
            var breathe = new DoubleAnimation(1, 1.16, TimeSpan.FromMilliseconds(1500))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Halo.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            Halo.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        }
        // Les ondes partent l'une après l'autre, comme un signal émis.
        for (int i = 0; i < Waves.Count; i++)
        {
            var pulse = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1400) };
            var start = TimeSpan.FromMilliseconds(220 * i);
            pulse.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.25, TimeSpan.Zero));
            pulse.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.25, start));
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, start + TimeSpan.FromMilliseconds(300), Palette.Out));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, start + TimeSpan.FromMilliseconds(900)));
            Waves[i].BeginAnimation(UIElement.OpacityProperty, pulse);
        }
    }

    public void StopLoops()
    {
        if (Halo != null)
        {
            Halo.BeginAnimation(ScaleTransform.ScaleXProperty, Palette.Anim(1, 400, Palette.Out));
            Halo.BeginAnimation(ScaleTransform.ScaleYProperty, Palette.Anim(1, 400, Palette.Out));
        }
        foreach (var wave in Waves) wave.BeginAnimation(UIElement.OpacityProperty, Palette.Anim(1, 300));
    }

    public void Spin(bool on)
    {
        if (Spinner == null) return;
        if (on)
        {
            Spinner.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            // Finit son tour au lieu de s'arrêter net.
            double from = Spinner.Angle % 360;
            Spinner.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(from, 360, TimeSpan.FromMilliseconds(Math.Max(120, (360 - from) * 2.2)))
                { EasingFunction = Palette.Out });
        }
    }
}

/// <summary>
/// Les illustrations des maquettes, transposées du SVG. Repère : la tuile
/// (161 de large), ancrée en haut à droite — elles débordent à droite,
/// derrière le texte. Trait blanc 30 %, remplissage blanc 5 %.
/// </summary>
internal static class TileArtwork
{
    private static Brush White(double alpha) => Palette.Frozen(Palette.A(Colors.White, alpha));

    private static Path Shape(Geometry geometry, Brush? fill, Brush? stroke = null, double thickness = 0, bool round = false)
    {
        var path = new Path { Data = geometry, Fill = fill, Stroke = stroke, StrokeThickness = thickness };
        if (round)
        {
            path.StrokeStartLineCap = path.StrokeEndLineCap = PenLineCap.Round;
            path.StrokeLineJoin = PenLineJoin.Round;
        }
        return path;
    }

    private static Path Line(string data, double alpha, double thickness, bool round = false) =>
        Shape(Geometry.Parse(data), null, White(alpha), thickness, round);

    private static Path Rect(double x, double y, double w, double h, double r, Brush? fill, Brush? stroke, double thickness) =>
        Shape(new RectangleGeometry(new Rect(x, y, w, h), r, r), fill, stroke, thickness);

    private static Path Circle(double cx, double cy, double r, Brush fill) =>
        Shape(new EllipseGeometry(new Point(cx, cy), r, r), fill);

    /// <summary>Cercle dont la couleur suit l'état.</summary>
    private static Path Accent(ArtVisual art, double cx, double cy, double r, double idle, double active)
    {
        var brush = new SolidColorBrush(Palette.A(art.IdleAccent, idle));
        art.Accents.Add((brush, idle, active));
        return Circle(cx, cy, r, brush);
    }

    /// <summary>Groupe du halo (cercles concentriques), qui respire autour de son centre.</summary>
    private static Canvas Halo(ArtVisual art, double cx, double cy)
    {
        art.Halo = new ScaleTransform(1, 1, cx, cy);
        return new Canvas { RenderTransform = art.Halo };
    }

    /// <summary>Symbole SVG (viewBox) mis à l'échelle dans une boîte, posée dans le repère de la tuile.</summary>
    private static Viewbox Symbol(Canvas symbol, double left, double top, double width, double height, double rotate = 0)
    {
        var box = new Viewbox { Width = width, Height = height, Stretch = Stretch.Uniform, Child = symbol };
        if (rotate != 0)
        {
            box.RenderTransformOrigin = new Point(0.5, 0.5);
            box.RenderTransform = new RotateTransform(rotate);
        }
        Canvas.SetLeft(box, left);
        Canvas.SetTop(box, top);
        return box;
    }

    /// <param name="caseTop">Décalage vertical du boîtier « en coin » (carte des Réglages : −34, fenêtre AirPods : −28).</param>
    public static ArtVisual? Build(TileArt kind, double caseTop = -34)
    {
        if (kind == TileArt.None) return null;
        var canvas = new Canvas { Width = 161, Height = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var art = new ArtVisual { Root = canvas };

        switch (kind)
        {
            case TileArt.Phone:
            {
                // Téléphone penché de 9°, îlot, écran qui luit.
                var s = new Canvas { Width = 44, Height = 80 };
                s.Children.Add(Rect(3, 3, 38, 74, 10, White(0.05), White(0.30), 0.8));
                s.Children.Add(Rect(15, 8, 14, 4, 2, White(0.22), null, 0));
                var halo = Halo(art, 22, 52);
                halo.Children.Add(Accent(art, 22, 52, 15, 0.10, 0.10));
                halo.Children.Add(Accent(art, 22, 52, 9, 0.20, 0.20));
                halo.Children.Add(Accent(art, 22, 52, 4, 0.45, 0.45));
                s.Children.Add(halo);
                s.Children.Add(Accent(art, 22, 52, 1.8, 1, 1));
                canvas.Children.Add(Symbol(s, 161 - 12 - 56, 5, 56, 102, rotate: 9));
                break;
            }
            case TileArt.Globe:
            {
                canvas.Children.Add(Accent(art, 122, 58, 36, 0.06, 0.08));
                foreach (var d in new[]
                {
                    "M86,58 A36,36 0 1 1 158,58 A36,36 0 1 1 86,58 Z",
                    "M108,58 A14,36 0 1 1 136,58 A14,36 0 1 1 108,58 Z",
                    "M95,58 A27,36 0 1 1 149,58 A27,36 0 1 1 95,58 Z",
                    "M86 58 H158", "M89.8 42 Q122 48 154.2 42", "M89.8 74 Q122 68 154.2 74",
                })
                    canvas.Children.Add(Line(d, 0.30, 0.9));
                var halo = Halo(art, 140, 46);
                halo.Children.Add(Accent(art, 140, 46, 9, 0.16, 0.25));
                canvas.Children.Add(halo);
                canvas.Children.Add(Accent(art, 140, 46, 3, 1, 1));
                break;
            }
            case TileArt.Monitor:
            {
                // Écran + pied, ondes qui sortent à droite (violet même au repos : couleur de la tuile Envoyer).
                art.IdleAccent = Palette.Purple;
                var s = new Canvas { Width = 76, Height = 56 };
                s.Children.Add(Rect(6, 4, 58, 36, 6, White(0.05), White(0.30), 0.6));
                s.Children.Add(Line("M31 40 L29 50 L47 50 L45 40", 0.30, 0.6));
                s.Children.Add(Line("M24 51 H52", 0.30, 0.8, round: true));
                var halo = Halo(art, 35, 22);
                halo.Children.Add(Accent(art, 35, 22, 13, 0.12, 0.12));
                halo.Children.Add(Accent(art, 35, 22, 7, 0.24, 0.24));
                s.Children.Add(halo);
                s.Children.Add(Accent(art, 35, 22, 2, 1, 1));
                foreach (var d in new[] { "M68 15 a9 9 0 0 1 0 14", "M72.5 10 a16 16 0 0 1 0 24" })
                {
                    var brush = new SolidColorBrush(Palette.A(art.IdleAccent, 0.5));
                    art.Accents.Add((brush, 0.5, 0.5));
                    var wave = Shape(Geometry.Parse(d), null, brush, 0.8, round: true);
                    art.Waves.Add(wave);
                    s.Children.Add(wave);
                }
                canvas.Children.Add(Symbol(s, 161 + 34 - 140, 2, 140, 104));
                break;
            }
            case TileArt.Case:
            case TileArt.CaseCorner:
            {
                // Le boîtier seul (pas d'écouteurs dessinés), agrandi, qui déborde.
                bool corner = kind == TileArt.CaseCorner;
                var s = new Canvas { Width = 76, Height = 52 };
                s.Children.Add(Rect(3, 4, 70, 46, 20, White(0.05), White(0.30), 0.6));
                s.Children.Add(Line("M3 24 L73 24", 0.30, 0.6));
                var halo = Halo(art, 38, 35);
                halo.Children.Add(Accent(art, 38, 35, 11, corner ? 0.14 : 0.14, corner ? 0.20 : 0.14));
                halo.Children.Add(Accent(art, 38, 35, 6, corner ? 0.26 : 0.28, corner ? 0.36 : 0.28));
                s.Children.Add(halo);
                s.Children.Add(Accent(art, 38, 35, 1.8, 1, 1));
                canvas.Children.Add(corner
                    ? Symbol(s, 161 + 30 - 150, caseTop, 150, 103)
                    : Symbol(s, 161 + 44 - 170, -14, 170, 116));
                break;
            }
            case TileArt.Power:
            {
                canvas.Children.Add(Accent(art, 124, 40, 26, 0.05, 0.10));
                canvas.Children.Add(Line("M111 24 A21 21 0 1 0 137 24", 0.34, 1.4, round: true));
                canvas.Children.Add(Line("M124 16 V38", 0.34, 1.4, round: true));
                canvas.Children.Add(Accent(art, 124, 40, 2.6, 0.5, 1));
                break;
            }
            case TileArt.Moon:
                canvas.Children.Add(Shape(Geometry.Parse("M136 22 A23 23 0 1 0 142 62 A18 18 0 1 1 136 22 Z"),
                    White(0.05), White(0.32), 1.2, round: true));
                break;
            case TileArt.Language:
            {
                canvas.Children.Add(Line("M100,36 A24,24 0 1 1 148,36 A24,24 0 1 1 100,36 Z", 0.30, 1));
                canvas.Children.Add(Line("M114,36 A10,24 0 1 1 134,36 A10,24 0 1 1 114,36 Z", 0.30, 1));
                canvas.Children.Add(Line("M100 36 H148", 0.30, 1));
                canvas.Children.Add(Circle(136, 26, 6, White(0.16)));
                canvas.Children.Add(Circle(136, 26, 2, White(1)));
                break;
            }
            case TileArt.Refresh:
            {
                // Flèche circulaire ; elle tourne pendant la vérification.
                var arrow = Line("M146 40 A22 22 0 1 1 138 22 M146 12 V24 H134", 0.30, 1.3, round: true);
                art.Spinner = new RotateTransform(0, 124, 39);
                arrow.RenderTransform = art.Spinner;
                canvas.Children.Add(arrow);
                break;
            }
        }
        return art;
    }
}

/// <summary>
/// Tuile de la charte (design/README.md) : nom en haut à gauche, ✕ rond en
/// haut à droite s'il y a quelque chose à arrêter, statut (point + texte)
/// et détail en bas, illustration au trait qui déborde à droite. Elle
/// change de teinte avec l'état (fondu croisé), réagit au survol et à la
/// pression (enfoncement + onde depuis le point touché), et respire
/// doucement quand elle est active. Pas d'interrupteur : toute la tuile est
/// le bouton.
///
/// Construite en code : les états et leurs transitions y sont plus simples
/// à piloter qu'en déclencheurs XAML. Aucun Effect WPF (ça casse
/// l'acrylique DWM) : halos et lueurs sont des dégradés radiaux.
/// </summary>
internal sealed class Tile : Grid
{
    private const double Radius = 18;

    /// <summary>Toucher sur la tuile (hors ✕ et hors pied interactif).</summary>
    public event Action? Click;
    /// <summary>Toucher sur le ✕.</summary>
    public event Action? CloseClick;

    private readonly Grid _body = new();
    private readonly ScaleTransform _press = new(1, 1);
    private readonly ScaleTransform _bloom = new(1, 1);
    private readonly Border _glass, _shine, _hover;
    private readonly Grid _layers = new();
    private readonly Dictionary<TileTint, Border> _tints = new();
    private readonly Canvas _ripples = new() { IsHitTestVisible = false };
    private readonly Grid _artHost = new() { IsHitTestVisible = false };
    private readonly Grid _content = new();
    private readonly TextBlock _title, _status, _detail;
    private readonly StackPanel _statusRow;
    private readonly Ellipse _dot, _dotGlow;
    private readonly SolidColorBrush _dotBrush = new(Palette.IdleDot);
    private readonly SolidColorBrush _statusBrush = new(Palette.Secondary);
    private readonly GradientStop _glowCore = new(Colors.Transparent, 0), _glowEdge = new(Colors.Transparent, 1);
    private readonly Border _close;
    private readonly ScaleTransform _closeScale = new(1, 1);
    private readonly SolidColorBrush _closeBrush = new(Color.FromArgb(0x40, 0, 0, 0));
    private readonly Border _footerHost = new();
    private ArtVisual? _art;
    private TileArt _artKind;
    private TileTint _tint = TileTint.Glass;
    private bool _closeShown;
    private bool _pressed;
    private bool _closePressed;
    private bool _loops;

    public Tile()
    {
        _glass = new Border
        {
            CornerRadius = new CornerRadius(Radius),
            Background = Palette.Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Palette.Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        // Reflet intérieur (box-shadow inset 0 1px des maquettes).
        _shine = new Border
        {
            CornerRadius = new CornerRadius(Radius - 1),
            Margin = new Thickness(1),
            BorderBrush = Palette.Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsHitTestVisible = false,
        };
        _hover = new Border
        {
            CornerRadius = new CornerRadius(Radius),
            Background = Palette.Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _layers.IsHitTestVisible = false;

        _title = new TextBlock
        {
            FontFamily = Palette.Body, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var closeGlyph = new TextBlock
        {
            Text = "", FontFamily = Palette.Icons, FontSize = 9,
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _close = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
            Background = _closeBrush, Child = closeGlyph, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _closeScale,
            Visibility = Visibility.Collapsed,
        };
        _close.MouseEnter += (_, _) =>
        {
            Palette.ColorTo(_closeBrush, Color.FromArgb(0x6B, 0, 0, 0), true, 150);
            if (!_closePressed) Palette.ScaleTo(_closeScale, 1.1, 180, Palette.Out);
        };
        _close.MouseLeave += (_, _) =>
        {
            Palette.ColorTo(_closeBrush, Color.FromArgb(0x40, 0, 0, 0), true, 250);
            Palette.ScaleTo(_closeScale, 1, 250, Palette.Out);
        };
        _close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _closePressed = true;
            _close.CaptureMouse();
            Palette.ScaleTo(_closeScale, 0.82, 80);
        };
        _close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_closePressed) return;
            _closePressed = false;
            bool inside = _close.IsMouseOver;
            _close.ReleaseMouseCapture();
            Palette.ScaleTo(_closeScale, inside ? 1.1 : 1, 300, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 });
            if (inside) CloseClick?.Invoke();
        };
        _close.LostMouseCapture += (_, _) => _closePressed = false;

        // Le ✕ a sa propre colonne : titre et statut s'arrêtent avant lui.
        _close.Margin = new Thickness(6, 0, 0, 0);
        _close.VerticalAlignment = VerticalAlignment.Top;
        var titleBox = new Grid { MinHeight = 24 };
        titleBox.Children.Add(_title);

        // Point du statut : lueur radiale derrière (pas d'Effect).
        var glow = new RadialGradientBrush { GradientStops = { _glowCore, _glowEdge } };
        _dotGlow = new Ellipse { Width = 18, Height = 18, Margin = new Thickness(-6), Fill = glow, Opacity = 0 };
        _dot = new Ellipse { Width = 6, Height = 6, Fill = _dotBrush };
        var dotBox = new Grid { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
        dotBox.Children.Add(_dotGlow);
        dotBox.Children.Add(_dot);
        _status = new TextBlock
        {
            FontFamily = Palette.Body, FontSize = 11.5, Foreground = _statusBrush,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        _statusRow = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        _statusRow.Children.Add(dotBox);
        _statusRow.Children.Add(_status);
        _detail = new TextBlock
        {
            FontFamily = Palette.Body, FontSize = 12, Foreground = Palette.Frozen(Palette.Secondary),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        // Statut juste sous le titre ; détail et pied restent en bas.
        var heading = new StackPanel();
        heading.Children.Add(titleBox);
        heading.Children.Add(_statusRow);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(heading);
        SetColumn(_close, 1);
        top.Children.Add(_close);

        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        bottom.Children.Add(_detail);
        bottom.Children.Add(_footerHost);

        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.Children.Add(top);
        SetRow(bottom, 2);
        _content.Children.Add(bottom);
        _content.Margin = new Thickness(14);

        _body.RenderTransformOrigin = new Point(0.5, 0.5);
        _body.RenderTransform = new TransformGroup { Children = { _press, _bloom } };
        _body.Children.Add(_glass);
        _body.Children.Add(_layers);
        _body.Children.Add(_shine);
        _body.Children.Add(_artHost);
        _body.Children.Add(_hover);
        _body.Children.Add(_ripples);
        _body.Children.Add(_content);
        // Les coins arrondis rognent l'illustration et l'onde de pression.
        _body.SizeChanged += (_, _) => _body.Clip = new RectangleGeometry(
            new Rect(0, 0, _body.ActualWidth, _body.ActualHeight), Radius, Radius);
        Children.Add(_body);

        Cursor = Cursors.Hand;
        IsVisibleChanged += (_, _) => UpdateLoops();
        IsEnabledChanged += (_, _) => Palette.Fade(_body, IsEnabled ? 1 : 0.45, 200);
    }

    // ================================================================== API

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public TileArt Art
    {
        get => _artKind;
        set
        {
            _artKind = value;
            _artHost.Children.Clear();
            _art = TileArtwork.Build(value);
            if (_art == null) return;
            _artHost.Children.Add(_art.Root);
            _art.SetAccent(_tint, animate: false);
        }
    }

    /// <summary>Marges intérieures (14 par défaut ; 12/14 pour les tuiles des Réglages).</summary>
    public Thickness ContentPadding
    {
        get => _content.Margin;
        set => _content.Margin = value;
    }

    /// <summary>Titre sur deux lignes au besoin (tuiles étroites des Réglages), sinon tronqué.</summary>
    public bool WrapTitle
    {
        get => _title.TextWrapping == TextWrapping.Wrap;
        set
        {
            _title.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _title.LineStackingStrategy = value ? LineStackingStrategy.BlockLineHeight : LineStackingStrategy.MaxHeight;
            _title.LineHeight = value ? 18 : double.NaN;
            _title.MaxHeight = value ? 36 : double.PositiveInfinity;
        }
    }

    /// <summary>Faux : la tuile n'est qu'un affichage (pas de survol ni de pression).</summary>
    public bool Clickable
    {
        get => _clickable;
        set
        {
            _clickable = value;
            Cursor = value ? Cursors.Hand : null;
            if (!value) SetHover(false);
        }
    }

    private bool _clickable = true;

    /// <summary>Faux : pas de respiration en boucle quand elle est active (tuiles de réglage).</summary>
    public bool Breathe { get; set; } = true;

    /// <summary>Contenu sous le statut (niveaux de batterie, sélecteur, lignes de réglage…).</summary>
    public UIElement? Footer
    {
        get => _footerHost.Child;
        set => _footerHost.Child = value;
    }

    /// <summary>
    /// Vrai : le pied contient ses propres options (carte AirPods des
    /// Réglages). Tout le bandeau du pied sort alors de la tuile : ni survol,
    /// ni pression, ni clic de la tuile, pointeur normal — seules ses lignes
    /// réagissent, on voit ce qu'on va toucher.
    /// </summary>
    public bool FooterIsSeparate
    {
        get => _footerSeparate;
        set
        {
            _footerSeparate = value;
            _footerHost.Cursor = value ? Cursors.Arrow : null;
        }
    }

    private bool _footerSeparate;

    public TileTint Tint => _tint;

    /// <summary>
    /// État complet de la tuile. <paramref name="status"/> null = pas de
    /// ligne de statut ; <paramref name="detail"/> null = pas de détail ;
    /// <paramref name="dot"/> impose la couleur du point (sinon celle de la teinte).
    /// </summary>
    public void SetState(TileTint tint, string? status, string? detail = null, bool close = false, Color? dot = null)
    {
        bool animate = IsLoaded && IsVisible;
        SetTint(tint, animate);
        var dotColor = dot ?? Palette.Dot(tint);
        if (_dotBrush.Color != dotColor) Palette.ColorTo(_dotBrush, dotColor, animate);
        bool hadStatus = _statusRow.Visibility == Visibility.Visible;
        Palette.SetText(_status, status, animate && hadStatus);
        _statusRow.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        Palette.SetText(_detail, detail, animate);
        SetClose(close, animate);
    }

    /// <summary>Rotation de l'illustration « flèche circulaire » (vérification en cours).</summary>
    public void SetBusy(bool busy) => _art?.Spin(busy);

    // ============================================================= Teinte

    private void SetTint(TileTint tint, bool animate)
    {
        if (tint == _tint) return;
        var previous = _tint;
        _tint = tint;

        if (tint != TileTint.Glass && !_tints.ContainsKey(tint))
        {
            var layer = new Border
            {
                CornerRadius = new CornerRadius(Radius),
                Background = Palette.Gradient(tint),
                BorderBrush = Palette.Frozen(Palette.A(Palette.Base(tint), Palette.Strength(tint).Border)),
                BorderThickness = new Thickness(1),
                Opacity = 0,
            };
            _tints[tint] = layer;
            _layers.Children.Add(layer);
        }
        foreach (var (key, layer) in _tints)
        {
            double target = key == tint ? 1 : 0;
            if (animate) layer.BeginAnimation(OpacityProperty, Palette.Anim(target, 380, Palette.Out));
            else { layer.BeginAnimation(OpacityProperty, null); layer.Opacity = target; }
        }

        Palette.ColorTo(_statusBrush, Palette.Text(tint), animate);
        var c = Palette.Dot(tint);
        _glowCore.Color = Palette.A(c, 0.6);
        _glowEdge.Color = Palette.A(c, 0);
        _art?.SetAccent(tint, animate);
        UpdateLoops();
        if (tint == TileTint.Glass)
        {
            _dotGlow.BeginAnimation(OpacityProperty, animate ? Palette.Anim(0, 250) : null);
            if (!animate) _dotGlow.Opacity = 0;
        }

        // Elle s'allume : petit gonflement et une onde depuis le centre.
        if (animate && previous == TileTint.Glass && tint != TileTint.Glass)
        {
            var bloom = new DoubleAnimationUsingKeyFrames();
            bloom.KeyFrames.Add(new EasingDoubleKeyFrame(1.035, TimeSpan.FromMilliseconds(140), Palette.Out));
            bloom.KeyFrames.Add(new EasingDoubleKeyFrame(1, TimeSpan.FromMilliseconds(520),
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }));
            _bloom.BeginAnimation(ScaleTransform.ScaleXProperty, bloom);
            _bloom.BeginAnimation(ScaleTransform.ScaleYProperty, bloom);
            if (!_pressed) Ripple(new Point(_body.ActualWidth / 2, _body.ActualHeight / 2), Palette.A(Palette.Base(tint), 0.35));
        }
    }

    /// <summary>
    /// Boucles discrètes quand la tuile est active et visible : la lueur du
    /// point pulse, le halo de l'illustration respire. Coupées dès que le
    /// panneau est caché — pas de rendu en continu pour rien.
    /// </summary>
    private void UpdateLoops()
    {
        bool on = IsVisible && _tint != TileTint.Glass && Breathe;
        if (on == _loops)
        {
            if (!on && _tint != TileTint.Glass) _dotGlow.Opacity = 1;
            return;
        }
        _loops = on;
        if (on)
        {
            _dotGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(1200))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            _art?.StartLoops();
        }
        else
        {
            _dotGlow.BeginAnimation(OpacityProperty, null);
            _dotGlow.Opacity = _tint != TileTint.Glass ? 1 : 0;
            _art?.StopLoops();
        }
    }

    private void SetClose(bool show, bool animate)
    {
        if (show == _closeShown) return;
        _closeShown = show;
        _title.Margin = new Thickness(0, 0, show ? 30 : 0, 0);
        if (!animate)
        {
            _close.BeginAnimation(OpacityProperty, null);
            _closeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _closeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _close.Opacity = 1;
            _closeScale.ScaleX = _closeScale.ScaleY = 1;
            _close.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (show)
        {
            _close.Visibility = Visibility.Visible;
            _close.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(380))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.9 } };
            _closeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            _closeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        else
        {
            var fade = Palette.Anim(0, 160);
            fade.Completed += (_, _) => { if (!_closeShown) _close.Visibility = Visibility.Collapsed; };
            _close.BeginAnimation(OpacityProperty, fade);
            Palette.ScaleTo(_closeScale, 0.5, 160);
        }
    }

    // ======================================================= Interactions

    private bool InFooter(object source)
    {
        for (var d = source as DependencyObject; d != null && d != this;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d == _footerHost) return true;
        }
        return false;
    }

    /// <summary>Le point (repère de la tuile) est-il dans le bandeau d'un pied séparé ?</summary>
    private bool OverSeparateFooter(Point p)
    {
        if (!_footerSeparate || _footerHost.ActualHeight < 1) return false;
        double top = _footerHost.TranslatePoint(new Point(0, 0), this).Y;
        return p.Y >= top;
    }

    private bool _hovered;

    private void SetHover(bool on)
    {
        if (on == _hovered) return;
        _hovered = on;
        Palette.Fade(_hover, on ? 1 : 0, on ? 140 : 280);
        if (!_pressed) Palette.ScaleTo(_press, on ? 1.012 : 1, on ? 220 : 280, Palette.Out);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetHover(Clickable && !OverSeparateFooter(e.GetPosition(this)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_footerSeparate) SetHover(Clickable && !OverSeparateFooter(e.GetPosition(this)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(false);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || !Clickable || !IsEnabled || InFooter(e.OriginalSource)
            || OverSeparateFooter(e.GetPosition(this))) return;
        _pressed = true;
        CaptureMouse();
        Palette.ScaleTo(_press, 0.965, 110, Palette.Out);
        Ripple(e.GetPosition(_body), Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        _pressed = false;
        var p = e.GetPosition(this);
        bool inside = p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        ReleaseMouseCapture();
        Release();
        e.Handled = true;
        if (inside) Click?.Invoke();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_pressed) return;
        _pressed = false;
        Release();
    }

    private void Release() =>
        Palette.ScaleTo(_press, _hovered ? 1.012 : 1, 420,
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });

    private static double Dist(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>Onde qui s'étend depuis <paramref name="at"/> et s'évanouit.</summary>
    private void Ripple(Point at, Color color)
    {
        double w = _body.ActualWidth, h = _body.ActualHeight;
        if (w < 1 || h < 1) return;
        double r = Math.Max(Math.Max(Dist(at.X, at.Y), Dist(w - at.X, at.Y)),
                            Math.Max(Dist(at.X, h - at.Y), Dist(w - at.X, h - at.Y)));
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(Palette.A(color, color.A / 255.0 * 0.5), 0.7),
                new GradientStop(Palette.A(color, 0), 1),
            },
        };
        brush.Freeze();
        var scale = new ScaleTransform(0.08, 0.08);
        var ring = new Ellipse
        {
            Width = 2 * r, Height = 2 * r, Fill = brush,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
        };
        Canvas.SetLeft(ring, at.X - r);
        Canvas.SetTop(ring, at.Y - r);
        _ripples.Children.Add(ring);

        var grow = new DoubleAnimation(0.08, 1, TimeSpan.FromMilliseconds(620))
        { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, TimeSpan.Zero));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(160)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(720), Palette.Out));
        fade.Completed += (_, _) => _ripples.Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, fade);
    }
}

/// <summary>
/// Trois mini-barres G / D / B (boîtier) : libellé et valeur au-dessus, barre
/// de 3 px dessous. Les barres se remplissent en glissant.
/// </summary>
internal sealed class BatteryBars : Grid
{
    private readonly TextBlock[] _labels = new TextBlock[3];
    private readonly TextBlock[] _values = new TextBlock[3];
    private readonly ScaleTransform[] _fills = new ScaleTransform[3];
    private readonly SolidColorBrush _track = new(Color.FromArgb(0x59, 0x78, 0x78, 0x80));
    private readonly SolidColorBrush _fill = new(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _label = new(Palette.Secondary);

    public BatteryBars(double fontSize = 10.5, double gap = 9)
    {
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var head = new Grid();
            _labels[i] = new TextBlock { FontFamily = Palette.Body, FontSize = fontSize, Foreground = _label };
            _values[i] = new TextBlock
            {
                FontFamily = Palette.Body, FontSize = fontSize, Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right, Text = "—",
            };
            head.Children.Add(_labels[i]);
            head.Children.Add(_values[i]);

            _fills[i] = new ScaleTransform(0, 1);
            var bar = new Grid { Height = 3, Margin = new Thickness(0, 3, 0, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = _track });
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(1.5), Background = _fill,
                RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _fills[i],
            });

            var cell = new StackPanel();
            cell.Children.Add(head);
            cell.Children.Add(bar);
            SetColumn(cell, i * 2);
            Children.Add(cell);
        }
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        _labels[0].Text = Loc.T("L", "G");
        _labels[1].Text = Loc.T("R", "D");
        _labels[2].Text = Loc.T("C", "B");
    }

    /// <summary>Couleurs selon le fond : sur une teinte, piste sombre et remplissage blanc plein.</summary>
    public void SetOnTint(bool tinted, bool animate)
    {
        Palette.ColorTo(_track, tinted ? Color.FromArgb(0x38, 0, 0, 0) : Color.FromArgb(0x59, 0x78, 0x78, 0x80), animate);
        Palette.ColorTo(_fill, tinted ? Colors.White : Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF), animate);
        Palette.ColorTo(_label, tinted ? Color.FromArgb(0xB3, 0xEB, 0xEB, 0xF5) : Palette.Secondary, animate);
    }

    /// <summary>Couleurs libres (fenêtre AirPods : piste claire sur verre).</summary>
    public void SetColors(Color track, Color fill, Color label, bool animate)
    {
        Palette.ColorTo(_track, track, animate);
        Palette.ColorTo(_fill, fill, animate);
        Palette.ColorTo(_label, label, animate);
    }

    public void SetLevels(AirPodsBattery? battery, bool animate)
    {
        ApplyLanguage();
        Set(0, battery?.Left, battery?.LeftCharging == true, animate);
        Set(1, battery?.Right, battery?.RightCharging == true, animate);
        Set(2, battery?.Case, battery?.CaseCharging == true, animate);
    }

    private void Set(int i, int? level, bool charging, bool animate)
    {
        _values[i].Inlines.Clear();
        if (level is int value && charging)
            _values[i].Inlines.Add(new System.Windows.Documents.Run(" ")
            { FontFamily = Palette.Icons, FontSize = 8, Foreground = Palette.Frozen(Palette.Green) });
        _values[i].Inlines.Add(new System.Windows.Documents.Run(level is int v ? v.ToString() : "—"));

        double target = (level ?? 0) / 100.0;
        if (!animate)
        {
            _fills[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _fills[i].ScaleX = target;
            return;
        }
        _fills[i].BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(700 + 90 * i)) { EasingFunction = Palette.Out });
    }
}

/// <summary>
/// Barre de volume épaisse de la tuile « Son reçu » : on la saisit
/// n'importe où et on glisse (comme sur iOS), molette et flèches du clavier
/// comprises. Le remplissage suit la valeur en glissant (lissé), sans
/// jamais retarder la valeur elle-même — c'est elle qui pilote l'audio.
/// Gabarit : VolumeBarStyle (Styles.xaml).
/// </summary>
internal sealed class VolumeBar : RangeBase
{
    private const double Corner = 14;
    private ScaleTransform? _fill;
    private bool _dragging;

    public VolumeBar()
    {
        Minimum = 0;
        Maximum = 1;
        SmallChange = 0.02;
        LargeChange = 0.1;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        SizeChanged += (_, _) => Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), Corner, Corner);
    }

    /// <summary>Largeur remplie (px) visée, pour choisir la couleur du pictogramme posé dessus.</summary>
    public double FilledWidth => Ratio * ActualWidth;

    private double Ratio => Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // La transformation du gabarit est figée (partagée) : on pose la nôtre.
        _fill = null;
        if (GetTemplateChild("Fill") is FrameworkElement fill)
        {
            _fill = new ScaleTransform(Ratio, 1);
            fill.RenderTransform = _fill;
        }
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (_fill == null) return;
        if (!IsLoaded || !IsVisible)
        {
            _fill.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _fill.ScaleX = Ratio;
            return;
        }
        _fill.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(Ratio, TimeSpan.FromMilliseconds(_dragging ? 80 : 280)) { EasingFunction = Palette.Out });
    }

    private void SetFromPoint(Point p)
    {
        if (ActualWidth <= 0) return;
        Value = Minimum + Math.Clamp(p.X / ActualWidth, 0, 1) * (Maximum - Minimum);
    }

    // Tactile : géré ici directement. Laissé à la conversion tactile → souris
    // de WPF, le geste était avalé par le défilement tactile de la page.
    private bool _touchDragging;

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        if (!IsEnabled || _touchDragging) return;
        _dragging = _touchDragging = true;
        CaptureTouch(e.TouchDevice);
        SetFromPoint(e.GetTouchPoint(this).Position);
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        if (!_touchDragging || e.TouchDevice.Captured != this) return;
        SetFromPoint(e.GetTouchPoint(this).Position);
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        if (!_touchDragging) return;
        ReleaseTouchCapture(e.TouchDevice);
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        base.OnLostTouchCapture(e);
        _dragging = _touchDragging = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // Souris « promue » d'un toucher déjà traité ci-dessus : l'ignorer.
        if (_touchDragging && e.StylusDevice != null) { e.Handled = true; return; }
        if (!IsEnabled) return;
        Focus();
        _dragging = true;
        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && !_touchDragging && IsMouseCaptured) SetFromPoint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_touchDragging) { if (e.StylusDevice != null) e.Handled = true; return; }
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsEnabled) return;
        Value = Math.Clamp(Value + Math.Sign(e.Delta) * 0.05, Minimum, Maximum);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = e.Key switch
        {
            Key.Left or Key.Down => -SmallChange,
            Key.Right or Key.Up => SmallChange,
            Key.PageDown => -LargeChange,
            Key.PageUp => LargeChange,
            Key.Home => -Maximum,
            Key.End => Maximum,
            _ => 0,
        };
        if (step == 0) return;
        Value = Math.Clamp(Value + step, Minimum, Maximum);
        e.Handled = true;
    }
}
