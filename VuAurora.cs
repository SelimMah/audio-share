using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;

namespace AudioShare;

/// <summary>
/// Vumètre « aurore » de la tuile Son reçu : quelques lueurs douces derrière
/// la tuile qui dansent avec le son. La couleur dit d'où vient le son
/// (violet = émis, bleu = réseau, vert = téléphone, une lueur rose quand les
/// AirPods sont connectés).
///
/// Ce qui la rend vivante :
/// - gain automatique : le niveau est jugé par rapport au volume récent du
///   morceau, donc l'aurore réagit autant à 20 % qu'à 100 % de volume ;
/// - enveloppe rapide (attaque ~25 ms) contre enveloppe lente (~0,8 s) :
///   quand la rapide dépasse nettement la lente, c'est une attaque (coup de
///   grosse caisse, accent) → impulsion globale + une lueur à tour de rôle
///   s'allume, grossit et change de nuance (la vague circule d'une lueur à
///   l'autre) ;
/// - l'énergie du passage règle la vitesse de dérive : calme = lent et
///   sombre, fort = vif et lumineux ;
/// - chaque lueur suit le niveau avec son propre retard : ondulation, pas un
///   bloc qui clignote d'un coup.
///
/// Tous les temps sont en secondes (pas réel entre deux images), pour un
/// rendu identique quelle que soit la fréquence d'affichage.
/// Ellipses fixes à dégradé gaussien : seules des transformations et des
/// opacités bougent. Pas de BlurEffect : il casse l'acrylique DWM.
/// </summary>
internal sealed class VuAurora
{
    // Familles de teintes voisines (couleurs système iOS, mode sombre) : des
    // teintes sans rapport mélangées donnent du gris sale.
    private static readonly Color[][] Families =
    [
        [Rgb(0xBF, 0x5A, 0xF2), Rgb(0x5E, 0x5C, 0xE6), Rgb(0xDA, 0x8F, 0xFF)], // émis : violet, indigo, lilas
        [Rgb(0x0A, 0x84, 0xFF), Rgb(0x64, 0xD2, 0xFF), Rgb(0x5E, 0x5C, 0xE6)], // réseau : bleu, cyan, indigo
        [Rgb(0x30, 0xD1, 0x58), Rgb(0x66, 0xD4, 0xCF), Rgb(0x40, 0xC8, 0xE0)], // téléphone : vert, menthe, sarcelle
    ];
    private static readonly Color AirPodsAccent = Rgb(0xFF, 0x64, 0x82);

    private sealed class Glow
    {
        public required Ellipse Shape;
        public required ScaleTransform Scale;
        public required TranslateTransform Pos;
        public required GradientStop[] Stops;
        public double RX, RY, SpeedX, SpeedY, PhaseX, PhaseY, BreathPhase;
        public double Lag, Sens;   // retard de suivi (s) et sensibilité propres
        public double Env, Kick;   // niveau suivi, éclat d'attaque (décroît)
        public int Shade;          // nuance dans la famille, avance sur les attaques
        public double CR, CG, CB;  // couleur courante, glisse vers sa cible
        public bool Colored;
    }

    // Profil gaussien exp(-3 r²) : cœur lumineux, bord déjà invisible avant
    // le contour de l'ellipse.
    private static readonly (double Offset, double Alpha)[] Profile =
        [(0, 1), (0.2, 0.89), (0.4, 0.62), (0.6, 0.34), (0.8, 0.15), (1, 0)];

    private const double CoreAlpha = 0.72;

    private readonly Canvas _host;
    private readonly List<Glow> _glows = new();
    private readonly float[] _src = new float[3];
    private double _agc = 0.05, _fast, _slow, _pulse, _cooldown, _presence, _phase;
    private int _beats;
    private bool _armed = true;

    public VuAurora(Canvas host, Random? rng = null)
    {
        _host = host;
        _host.Opacity = 0;
        rng ??= new Random();
        for (int i = 0; i < 5; i++)
        {
            var stops = Profile.Select(p => new GradientStop(Colors.Transparent, p.Offset)).ToArray();
            var brush = new RadialGradientBrush();
            foreach (var s in stops) brush.GradientStops.Add(s);
            var g = new Glow
            {
                Shape = null!, Scale = new ScaleTransform(), Pos = new TranslateTransform(), Stops = stops,
                RX = 90 + rng.NextDouble() * 55,
                RY = 58 + rng.NextDouble() * 30,
                SpeedX = 0.35 + rng.NextDouble() * 0.45,
                SpeedY = 0.30 + rng.NextDouble() * 0.40,
                PhaseX = rng.NextDouble() * Math.Tau,
                PhaseY = rng.NextDouble() * Math.Tau,
                BreathPhase = rng.NextDouble() * Math.Tau,
                Lag = 0.04 + i * 0.05,
                Sens = 0.75 + rng.NextDouble() * 0.5,
                Shade = i,
            };
            var group = new TransformGroup();
            group.Children.Add(g.Scale);
            group.Children.Add(g.Pos);
            g.Shape = new Ellipse
            {
                Width = 2 * g.RX, Height = 2 * g.RY, Fill = brush,
                RenderTransform = group,
                // L'échelle respire autour du centre de l'ellipse.
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            };
            _host.Children.Add(g.Shape);
            _glows.Add(g);
        }
    }

    /// <summary>Remet tout à zéro (flyout caché) : la prochaine ouverture repart éteinte.</summary>
    public void Reset()
    {
        Array.Clear(_src);
        _fast = _slow = _pulse = _presence = 0;
        _agc = 0.05;
        foreach (var g in _glows) g.Env = g.Kick = 0;
        _host.Opacity = 0;
    }

    /// <summary>Facteur de lissage exponentiel pour une constante de temps tau (s).</summary>
    private static double K(double dt, double tau) => 1 - Math.Exp(-dt / tau);

    /// <summary>
    /// Un pas d'animation. dt = secondes écoulées depuis le précédent ;
    /// niveaux de crête 0..1 de chaque source, 0 si elle ne joue pas.
    /// </summary>
    public void Update(double dt, double width, double height,
        float sent, float network, float phone, bool airPods)
    {
        dt = Math.Clamp(dt, 0.001, 0.1);
        float[] raw = [sent, network, phone];
        double level = 0;
        var active = new List<Color[]>();
        double hold = Math.Exp(-dt / 0.6);
        for (int i = 0; i < 3; i++)
        {
            // Mémoire courte par source : un silence d'une fraction de
            // seconde ne fait pas sauter la couleur.
            _src[i] = (float)Math.Max(Math.Min(1f, raw[i]), _src[i] * hold);
            level = Math.Max(level, Math.Min(1f, raw[i]));
            if (_src[i] > 0.01f) active.Add(Families[i]);
        }
        bool accent = airPods && active.Count > 0;

        // Gain automatique : référence = crête récente (monte tout de suite,
        // redescend en ~4 s). x = où on en est dans la dynamique du morceau.
        _agc = level > _agc ? level : Math.Max(0.03, _agc + (level - _agc) * K(dt, 4));
        double x = Math.Clamp((level / _agc - 0.3) / 0.7, 0, 1);

        _fast += (x - _fast) * K(dt, x > _fast ? 0.025 : 0.14);
        _slow += (x - _slow) * K(dt, 0.8);

        // Attaque : la rapide décolle de la lente. Une seule par coup : il
        // faut que l'écart retombe pour réarmer (sinon un même coup compte
        // deux fois), plus un délai mini sur les passages très percussifs.
        _cooldown -= dt;
        double rise = _fast - _slow;
        if (rise < 0.06) _armed = true;
        if (_armed && rise > 0.16 && _fast > 0.35 && _cooldown <= 0)
        {
            _armed = false;
            _pulse = 1;
            _cooldown = 0.16;
            var hit = _glows[_beats % _glows.Count];
            hit.Kick = 1;
            hit.Shade++;
            _beats++;
        }
        _pulse *= Math.Exp(-dt / 0.25);

        _presence += ((active.Count > 0 ? 1 : 0) - _presence) * K(dt, 0.35);
        if (_presence < 0.005 && active.Count == 0) { _host.Opacity = 0; return; }
        _host.Opacity = _presence;

        // Calme = dérive lente ; fort = vif ; chaque attaque donne un élan.
        _phase += dt * (0.18 + 0.75 * _slow + 1.6 * _pulse);

        for (int i = 0; i < _glows.Count; i++)
        {
            var g = _glows[i];
            g.Env += (_fast - g.Env) * K(dt, g.Lag);
            g.Kick *= Math.Exp(-dt / 0.35);
            double e = Math.Min(1, g.Env * g.Sens);

            // Dérive, centre gardé un peu bas : le titre reste lisible.
            g.Pos.X = width * (0.5 + 0.42 * Math.Sin(_phase * g.SpeedX + g.PhaseX)) - g.RX;
            g.Pos.Y = height * (0.62 + 0.30 * Math.Sin(_phase * g.SpeedY + g.PhaseY)) - g.RY;

            double breath = 0.78 + 0.05 * Math.Sin(_phase * 1.7 + g.BreathPhase)
                + 0.20 * e + 0.12 * _pulse + 0.30 * g.Kick;
            g.Scale.ScaleX = breath;
            g.Scale.ScaleY = breath * (0.95 + 0.08 * Math.Sin(_phase * 1.3 + g.PhaseX));

            // Lueur basse au calme, pleine sur les passages forts et les attaques.
            g.Shape.Opacity = Math.Min(1, 0.18 + 0.50 * e + 0.20 * _pulse + 0.55 * g.Kick);

            if (active.Count == 0) continue; // fondu de sortie : on garde les couleurs

            // Chaque source prend des lueurs à tour de rôle ; la nuance dans
            // sa famille avance quand la lueur reçoit une attaque.
            Color c;
            if (accent && i == _glows.Count - 1) c = AirPodsAccent;
            else
            {
                var family = active[i % active.Count];
                c = family[(g.Shade + i / active.Count) % family.Length];
            }
            if (!g.Colored) { g.CR = c.R; g.CG = c.G; g.CB = c.B; g.Colored = true; }
            double kc = K(dt, 0.3);
            g.CR += (c.R - g.CR) * kc;
            g.CG += (c.G - g.CG) * kc;
            g.CB += (c.B - g.CB) * kc;
            byte r = (byte)g.CR, gr = (byte)g.CG, b = (byte)g.CB;
            for (int s = 0; s < g.Stops.Length; s++)
                g.Stops[s].Color = Color.FromArgb((byte)(255 * CoreAlpha * Profile[s].Alpha), r, gr, b);
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
