using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Concentus;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioShare;

/// <summary>
/// Gain et balance appliqués aux échantillons reçus du réseau — le flux nous
/// appartient de bout en bout, aucun détour par les volumes de Windows.
/// </summary>
internal sealed class VolumeBalanceProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    public VolumeBalanceProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public volatile float Left = 1f;
    public volatile float Right = 1f;
    public volatile float Gain = 1f;

    /// <summary>Crête du dernier bloc rendu (0..1), pour le vumètre.</summary>
    public volatile float Peak;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float g = Gain, l = Left * g, r = Right * g;

        if (WaveFormat.Channels == 2 && (l != 1f || r != 1f))
        {
            for (int i = 0; i + 1 < read; i += 2)
            {
                buffer[offset + i] *= l;
                buffer[offset + i + 1] *= r;
            }
        }
        else if (WaveFormat.Channels != 2 && g != 1f)
        {
            for (int i = 0; i < read; i++) buffer[offset + i] *= g;
        }

        float peak = 0f;
        for (int i = 0; i < read; i++)
        {
            float a = Math.Abs(buffer[offset + i]);
            if (a > peak) peak = a;
        }
        Peak = peak;
        return read;
    }
}

/// <summary>
/// Rattrapage de dérive d'horloge : les horloges audio des deux PC ne battent
/// jamais exactement à la même cadence, donc le tampon de réception se remplit
/// ou se vide lentement — latence qui grimpe puis purge brutale, ou coupures.
/// On rééchantillonne très légèrement (±0,5 % max, inaudible) par interpolation
/// linéaire pour maintenir le tampon autour de la latence cible, sans jamais
/// couper ni sauter.
/// </summary>
internal sealed class DriftCorrector : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float[] _cur, _next;   // deux trames encadrant la position
    private readonly float[] _block;        // lecture source par blocs
    private int _blockCount, _blockPos;
    private double _pos = 1.0;              // force le chargement initial

    /// <summary>Trames source consommées par trame produite (1 ± 0,005).</summary>
    public volatile float Ratio = 1f;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public DriftCorrector(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _cur = new float[_channels];
        _next = new float[_channels];
        _block = new float[4096 - 4096 % _channels];
    }

    private void LoadNextFrame()
    {
        Array.Copy(_next, _cur, _channels);
        if (_blockPos >= _blockCount)
        {
            // BufferedWaveProvider (ReadFully) comble toujours en silence :
            // jamais de lecture partielle, mais on se protège quand même.
            _blockCount = _source.Read(_block, 0, _block.Length);
            _blockPos = 0;
            if (_blockCount < _channels)
            {
                Array.Clear(_block, 0, _channels);
                _blockCount = _channels;
            }
        }
        Array.Copy(_block, _blockPos, _next, 0, _channels);
        _blockPos += _channels;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _channels;
        float ratio = Ratio;
        for (int f = 0; f < frames; f++)
        {
            while (_pos >= 1.0)
            {
                _pos -= 1.0;
                LoadNextFrame();
            }
            float t = (float)_pos;
            int o = offset + f * _channels;
            for (int c = 0; c < _channels; c++)
                buffer[o + c] = _cur[c] + (_next[c] - _cur[c]) * t;
            _pos += ratio;
        }
        return frames * _channels;
    }
}

/// <summary>
/// Réception du son d'un autre PC par le réseau local.
/// Découverte : l'émetteur diffuse « ASHARE? » en UDP (port 42501), on répond
/// avec le nom de cette machine. Flux : TCP (port 42502), un en-tête décrivant
/// le format puis du PCM 16 bits brut — à ~1,5 Mb/s, aucun codec nécessaire.
/// </summary>
internal sealed class NetworkReceiver : IDisposable
{
    public const int DiscoveryPort = 42501;
    public const int StreamPort = 42502;

    // Latence cible initiale du tampon de réception : prudente au départ,
    // puis ADAPTATIVE — toutes les 10 s, le plus bas niveau de tampon observé
    // dit de combien on peut descendre (gigue réelle du lien) ou s'il faut
    // remonter. Sur un lien propre, elle converge vers TargetFloorMs.
    private const int TargetStartMs = 120;
    private const int TargetFloorMs = 40;
    private const int TargetCeilMs = 300;

    // Au-delà : le réseau s'est figé puis a vidé sa rafale d'un coup — on
    // resynchronise franchement plutôt que de rattraper 0,5 % par 0,5 %.
    private const int ResyncLatencyMs = 500;

    private UdpClient? _udp;
    private TcpListener? _tcp;
    private CancellationTokenSource? _cts;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private DriftCorrector? _drift;
    private VolumeBalanceProvider? _vb;
    private volatile string? _senderName;
    private IPAddress? _senderAddress;

    // La page Réglages peut changer la sortie pendant qu'un flux joue.
    private readonly object _playLock = new();

    private float _left = 1f, _right = 1f;

    public string? SenderName => _senderName;
    public bool IsReceiving => _senderName != null;

    /// <summary>Latence actuelle du tampon (ms), pour affichage.</summary>
    public int? LatencyMs => IsReceiving && _buffer is { } b
        ? (int)b.BufferedDuration.TotalMilliseconds : null;

    /// <summary>Crête du son rendu (0..1), pour le vumètre.</summary>
    public float OutputLevel => _vb?.Peak ?? 0f;

    private long _bytesReceived;

    /// <summary>Octets reçus depuis le lancement, pour le débit (diagnostic).</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Levé sur un thread quelconque quand un émetteur arrive ou part.</summary>
    public event Action? Changed;

    /// <summary>Volume reçu de l'émetteur (contrôles partagés), pour refléter dans l'interface.</summary>
    public event Action<float>? RemoteGain;

    /// <summary>Balance reçue de l'émetteur (contrôles partagés).</summary>
    public event Action<float, float>? RemoteBalance;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => DiscoveryLoopAsync(_cts.Token));
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void SetBalance(float left, float right)
    {
        _left = left; _right = right;
        if (_vb is { } vb) { vb.Left = left; vb.Right = right; }
    }

    /// <summary>
    /// Contrôles partagés, sens récepteur → émetteur : le volume vit chez
    /// l'émetteur (appliqué aux échantillons envoyés), notre curseur ne fait
    /// que le télécommander ; l'émetteur renvoie la valeur en écho.
    /// </summary>
    public void SendVolumeToSender(float volume)
    {
        var target = _senderAddress;
        if (target == null || _udp == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(
                "ASHAREVOL " + volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _udp.Send(bytes, bytes.Length, new IPEndPoint(target, DiscoveryPort));
        }
        catch { /* datagramme perdu : le prochain corrigera */ }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        try
        {
            _udp = new UdpClient(DiscoveryPort);
            while (!ct.IsCancellationRequested)
            {
                var request = await _udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(request.Buffer);

                if (text == "ASHARE?")
                {
                    var reply = Encoding.UTF8.GetBytes("ASHARE!" + Environment.MachineName);
                    await _udp.SendAsync(reply, request.RemoteEndPoint, ct);
                }
                else if (text.StartsWith("ASHAREVOL "))
                {
                    // Contrôles partagés : valeur du volume émis, déjà
                    // appliquée aux échantillons par l'émetteur — on ne fait
                    // que la refléter (curseur), sans l'appliquer une 2e fois.
                    if (float.TryParse(text[10..], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var gain))
                    {
                        RemoteGain?.Invoke(Math.Clamp(gain, 0f, 1f));
                    }
                }
                else if (text.StartsWith("ASHAREBAL "))
                {
                    var parts = text[10..].Split(' ');
                    if (parts.Length == 2
                        && float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var left)
                        && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var right))
                    {
                        SetBalance(Math.Clamp(left, 0f, 1f), Math.Clamp(right, 0f, 1f));
                        RemoteBalance?.Invoke(left, right);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"Réseau (découverte) : {ex.Message}"); }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            _tcp = new TcpListener(IPAddress.Any, StreamPort);
            _tcp.Start();
            while (!ct.IsCancellationRequested)
            {
                var client = await _tcp.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"Réseau (écoute) : {ex.Message}"); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        StopPlayback(); // un seul émetteur à la fois : le dernier arrivé gagne
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var stream = client.GetStream();

            var header = new byte[64];
            await ReadExactAsync(stream, header, 16, ct);
            string magic = Encoding.ASCII.GetString(header, 0, 8);
            bool isOpus = magic == "ASHARE02";
            if (magic != "ASHARE01" && !isOpus) return;
            int rate = BitConverter.ToInt32(header, 8);
            int channels = BitConverter.ToInt16(header, 12);
            int bits = BitConverter.ToInt16(header, 14);

            await ReadExactAsync(stream, header, 2, ct);
            int nameLength = BitConverter.ToInt16(header, 0);
            var nameBuffer = new byte[nameLength];
            await ReadExactAsync(stream, nameBuffer, nameLength, ct);
            _senderName = Encoding.UTF8.GetString(nameBuffer);
            _senderAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            Log.Write($"Réseau : émetteur « {_senderName} » connecté "
                      + $"({rate} Hz, {channels} canaux, {bits} bits, {(isOpus ? "Opus" : "PCM")})");

            var format = new WaveFormat(rate, bits, channels);
            var buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            // Coussin initial de silence : la lecture démarre tout de suite
            // avec la latence cible déjà en réserve, prête à absorber la gigue.
            var cushion = new byte[format.AverageBytesPerSecond * TargetStartMs / 1000
                                   / format.BlockAlign * format.BlockAlign];
            buffer.AddSamples(cushion, 0, cushion.Length);

            var drift = new DriftCorrector(buffer.ToSampleProvider());
            _buffer = buffer;
            _drift = drift;
            _vb = new VolumeBalanceProvider(drift)
            {
                Left = _left, Right = _right,
            };
            lock (_playLock)
            {
                _output = CreateOutput();
                _output.Init(_vb);
                _output.Play();
            }
            Changed?.Invoke();

            // ----- Régulation commune aux deux formats -----
            double targetMs = TargetStartMs;
            double windowMin = double.MaxValue;
            long windowStart = Environment.TickCount64;

            void Regulate(int addedBytes)
            {
                double buffered = buffer.BufferedDuration.TotalMilliseconds;
                if (buffered > ResyncLatencyMs)
                {
                    // Rafale après un gel réseau : on repart du direct en
                    // reconstituant le coussin d'un coup.
                    buffer.ClearBuffer();
                    buffer.AddSamples(cushion, 0, cushion.Length);
                    buffered = TargetStartMs;
                }

                // Latence adaptative : le plus bas niveau de tampon observé
                // sur 10 s mesure la gigue réelle. Trop de marge → on descend
                // (en gardant ~30 ms de garde) ; près du vide → on remonte.
                windowMin = Math.Min(windowMin, buffered);
                if (Environment.TickCount64 - windowStart > 10_000)
                {
                    if (windowMin > 45)
                        targetMs = Math.Max(TargetFloorMs, targetMs - (windowMin - 30));
                    else if (windowMin < 12)
                        targetMs = Math.Min(TargetCeilMs, targetMs + 30);
                    windowStart = Environment.TickCount64;
                    windowMin = double.MaxValue;
                }

                // Asservissement de la dérive d'horloge : écart de latence
                // replié en vitesse de lecture, ±0,5 % max (inaudible).
                drift.Ratio = 1f + Math.Clamp(
                    (float)(buffered - targetMs) / 4000f, -0.005f, 0.005f);
                Interlocked.Add(ref _bytesReceived, addedBytes);
            }

            if (isOpus)
            {
                using var opus = OpusCodecFactory.CreateDecoder(rate, channels);
                var lenBuf = new byte[2];
                var payload = new byte[1500];
                var frame = new short[5760 * channels]; // 120 ms max par trame Opus
                var frameBytes = new byte[frame.Length * 2];
                while (!ct.IsCancellationRequested)
                {
                    await ReadExactAsync(stream, lenBuf, 2, ct);
                    int len = lenBuf[0] | (lenBuf[1] << 8);
                    if (len <= 0 || len > payload.Length) break;
                    await ReadExactAsync(stream, payload, len, ct);

                    int decoded = opus.Decode(payload.AsSpan(0, len), frame, frame.Length / channels);
                    int bytes = decoded * channels * 2;
                    Buffer.BlockCopy(frame, 0, frameBytes, 0, bytes);
                    Regulate(2 + len);
                    buffer.AddSamples(frameBytes, 0, bytes);
                }
            }
            else
            {
                var data = new byte[32 * 1024];
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(data, ct);
                    if (n <= 0) break;
                    Regulate(n);
                    buffer.AddSamples(data, 0, n);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"Réseau (flux) : {ex.Message}"); }
        finally
        {
            _senderName = null;
            _senderAddress = null;
            StopPlayback();
            Changed?.Invoke();
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n <= 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    /// <summary>
    /// Sortie choisie dans les Réglages (Prefs), sinon la sortie par défaut.
    /// Un périphérique disparu (débranché) retombe sur la sortie par défaut.
    /// </summary>
    private static WasapiOut CreateOutput()
    {
        var id = Prefs.OutputDeviceId;
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active)
                    return new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
            }
            catch (Exception ex)
            {
                Log.Write($"Réseau : sortie choisie indisponible, sortie par défaut ({ex.Message})");
            }
        }
        // 30 ms de tampon de sortie (au lieu de 60) : moitié de latence en
        // moins à ce maillon, toujours confortable en mode partagé.
        return new WasapiOut(AudioClientShareMode.Shared, 30);
    }

    /// <summary>
    /// Rebranche la lecture en cours sur la sortie des Réglages : seule la
    /// sortie WASAPI est recréée, le tampon et le flux réseau continuent.
    /// </summary>
    public void ApplyOutputDevice()
    {
        lock (_playLock)
        {
            if (_vb == null) return;
            try { _output?.Stop(); } catch { }
            _output?.Dispose();
            try
            {
                _output = CreateOutput();
                _output.Init(_vb);
                _output.Play();
            }
            catch (Exception ex)
            {
                Log.Write($"Réseau : changement de sortie impossible ({ex.Message})");
                _output = null;
            }
        }
    }

    private void StopPlayback()
    {
        lock (_playLock)
        {
            try { _output?.Stop(); } catch { }
            _output?.Dispose();
            _output = null;
            _buffer = null;
            _drift = null;
            _vb = null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        try { _tcp?.Stop(); } catch { }
        StopPlayback();
    }
}
