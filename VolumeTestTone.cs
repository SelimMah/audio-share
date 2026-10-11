using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioShare;

/// <summary>
/// Repère sonore du volume : une note brève et douce, synthétisée, jouée au
/// niveau choisi. Elle passe par WASAPI et non par le Beep() système — une
/// onde carrée qui ignore tout volume : ici l'amplitude est exactement le gain
/// demandé, donc fidèle à ce que donnera le son reçu.
/// </summary>
internal static class VolumeTestTone
{
    // Mi5 et son octave : médium, lisible au casque comme sur de petits
    // haut-parleurs. L'octave s'éteint plus vite que la fondamentale — attaque
    // claire, queue ronde, comme une lame frappée.
    private const double Pitch = 659.26;
    private const double BodyDecay = 0.055, SheenDecay = 0.020;
    private const double Attack = 0.005, Release = 0.030, Length = 0.25;

    // Crête à plein volume (−12 dBFS) : du même ordre qu'une musique.
    private const float Level = 0.25f;

    private static int _playing;

    /// <summary>
    /// Joue la note avec un gain par canal (volume × balance), sur la sortie
    /// fournie par <paramref name="createOutput"/> ou, à défaut, la sortie par
    /// défaut. Non bloquant.
    /// </summary>
    public static void Play(float left, float right, Func<WasapiOut>? createOutput = null)
    {
        // Une note à la fois : des clics en rafale ne s'empilent pas.
        if (Interlocked.CompareExchange(ref _playing, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var output = createOutput?.Invoke()
                    ?? new WasapiOut(AudioClientShareMode.Shared, 30);
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                output.PlaybackStopped += (_, _) => done.TrySetResult();
                // Synthèse à la cadence du périphérique : aucun rééchantillonnage.
                output.Init(new Note(output.OutputWaveFormat.SampleRate, left, right));
                output.Play();
                // Garde-fou : ne jamais retenir la sortie si la fin se perd.
                await Task.WhenAny(done.Task, Task.Delay(2000));
            }
            catch (Exception ex) { Log.Write($"Repère sonore : {ex.Message}"); }
            finally { Interlocked.Exchange(ref _playing, 0); }
        });
    }

    private static float[] Render(int rate)
    {
        int length = (int)(rate * Length);
        // 50 ms de silence derrière : l'arrêt de la sortie ne rogne pas la queue.
        var note = new float[length + rate / 20];
        float peak = 0f;
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / rate;
            double w = 2 * Math.PI * Pitch * t;
            double s = Math.Sin(w) * Math.Exp(-t / BodyDecay)
                       + 0.3 * Math.Sin(2 * w) * Math.Exp(-t / SheenDecay);
            // Montée et extinction en demi-cosinus : aucun clic aux extrémités.
            if (t < Attack) s *= 0.5 - 0.5 * Math.Cos(Math.PI * t / Attack);
            double remaining = Length - t;
            if (remaining < Release) s *= 0.5 - 0.5 * Math.Cos(Math.PI * remaining / Release);
            note[i] = (float)s;
            peak = Math.Max(peak, Math.Abs(note[i]));
        }
        for (int i = 0; i < length; i++) note[i] *= Level / peak;
        return note;
    }

    /// <summary>La note, lue une fois : mono dupliqué avec un gain par canal.</summary>
    private sealed class Note : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly float _left, _right;
        private int _position;

        public WaveFormat WaveFormat { get; }

        public Note(int rate, float left, float right)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            _samples = Render(rate);
            _left = left;
            _right = right;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = Math.Min(count / 2, _samples.Length - _position);
            for (int i = 0; i < frames; i++)
            {
                float s = _samples[_position++];
                buffer[offset++] = s * _left;
                buffer[offset++] = s * _right;
            }
            return frames * 2;
        }
    }
}
