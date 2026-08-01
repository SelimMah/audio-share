using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float g = Gain, l = Left * g, r = Right * g;

        if (WaveFormat.Channels == 2)
        {
            if (l == 1f && r == 1f) return read;
            for (int i = 0; i + 1 < read; i += 2)
            {
                buffer[offset + i] *= l;
                buffer[offset + i + 1] *= r;
            }
        }
        else if (g != 1f)
        {
            for (int i = 0; i < read; i++) buffer[offset + i] *= g;
        }
        return read;
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

    private UdpClient? _udp;
    private TcpListener? _tcp;
    private CancellationTokenSource? _cts;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private VolumeBalanceProvider? _vb;
    private volatile string? _senderName;

    private float _left = 1f, _right = 1f, _gain = 1f;

    public string? SenderName => _senderName;
    public bool IsReceiving => _senderName != null;

    /// <summary>Levé sur un thread quelconque quand un émetteur arrive ou part.</summary>
    public event Action? Changed;

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

    public void SetGain(float gain)
    {
        _gain = gain;
        if (_vb is { } vb) vb.Gain = gain;
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        try
        {
            _udp = new UdpClient(DiscoveryPort);
            while (!ct.IsCancellationRequested)
            {
                var request = await _udp.ReceiveAsync(ct);
                if (Encoding.UTF8.GetString(request.Buffer) != "ASHARE?") continue;

                var reply = Encoding.UTF8.GetBytes("ASHARE!" + Environment.MachineName);
                await _udp.SendAsync(reply, request.RemoteEndPoint, ct);
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
            if (Encoding.ASCII.GetString(header, 0, 8) != "ASHARE01") return;
            int rate = BitConverter.ToInt32(header, 8);
            int channels = BitConverter.ToInt16(header, 12);
            int bits = BitConverter.ToInt16(header, 14);

            await ReadExactAsync(stream, header, 2, ct);
            int nameLength = BitConverter.ToInt16(header, 0);
            var nameBuffer = new byte[nameLength];
            await ReadExactAsync(stream, nameBuffer, nameLength, ct);
            _senderName = Encoding.UTF8.GetString(nameBuffer);
            Log.Write($"Réseau : émetteur « {_senderName} » connecté ({rate} Hz, {channels} canaux, {bits} bits)");

            _buffer = new BufferedWaveProvider(new WaveFormat(rate, bits, channels))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            _vb = new VolumeBalanceProvider(_buffer.ToSampleProvider())
            {
                Left = _left, Right = _right, Gain = _gain,
            };
            _output = new WasapiOut(AudioClientShareMode.Shared, 60);
            _output.Init(_vb);
            _output.Play();
            Changed?.Invoke();

            var data = new byte[32 * 1024];
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(data, ct);
                if (n <= 0) break;

                // Anti-dérive : si le retard s'accumule (réseau saturé, reprise
                // après pause), on repart du direct plutôt que d'empiler.
                if (_buffer.BufferedDuration > TimeSpan.FromMilliseconds(300))
                    _buffer.ClearBuffer();

                _buffer.AddSamples(data, 0, n);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"Réseau (flux) : {ex.Message}"); }
        finally
        {
            _senderName = null;
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

    private void StopPlayback()
    {
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _output = null;
        _buffer = null;
        _vb = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        try { _tcp?.Stop(); } catch { }
        StopPlayback();
    }
}
