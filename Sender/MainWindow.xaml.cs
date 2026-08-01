using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioShareSender;

/// <summary>
/// Émetteur : capture tout le son de ce PC (boucle WASAPI sur la sortie par
/// défaut), le convertit en PCM 16 bits et l'envoie au récepteur Audio Share
/// découvert sur le réseau local. Se reconnecte tout seul en cas de coupure.
/// </summary>
public partial class MainWindow : Window
{
    private const int DiscoveryPort = 42501;
    private const int StreamPort = 42502;

    private readonly CancellationTokenSource _cts = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = RunAsync(_cts.Token);
        Closed += (_, _) => _cts.Cancel();
    }

    private void Status(string text) => Dispatcher.Invoke(() => StatusText.Text = text);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Status("🔍 Recherche d'Audio Share sur le réseau…");
                var (address, name) = await DiscoverAsync(ct);
                Status($"Récepteur trouvé : « {name} ». Connexion…");
                await StreamAsync(address, name, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Status($"Diffusion interrompue ({ex.Message}). Nouvel essai…");
                try { await Task.Delay(2000, ct); } catch { return; }
            }
        }
    }

    private static async Task<(IPAddress Address, string Name)> DiscoverAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var probe = Encoding.UTF8.GetBytes("ASHARE?");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await udp.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(2000);
            try
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                var text = Encoding.UTF8.GetString(reply.Buffer);
                if (text.StartsWith("ASHARE!"))
                    return (reply.RemoteEndPoint.Address, text[7..]);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // pas de réponse dans les temps : on rediffuse la sonde
            }
        }
    }

    private async Task StreamAsync(IPAddress address, string receiverName, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(address, StreamPort, ct);
        client.NoDelay = true;
        var net = client.GetStream();

        using var capture = new WasapiLoopbackCapture();
        var format = capture.WaveFormat;
        bool isFloat = format.BitsPerSample == 32; // le mix WASAPI est en float 32

        // En-tête : magie, format (converti en 16 bits), nom de cette machine
        var name = Encoding.UTF8.GetBytes(Environment.MachineName);
        var header = new byte[16 + 2 + name.Length];
        Encoding.ASCII.GetBytes("ASHARE01").CopyTo(header, 0);
        BitConverter.GetBytes(format.SampleRate).CopyTo(header, 8);
        BitConverter.GetBytes((short)format.Channels).CopyTo(header, 12);
        BitConverter.GetBytes((short)16).CopyTo(header, 14);
        BitConverter.GetBytes((short)name.Length).CopyTo(header, 16);
        name.CopyTo(header, 18);
        await net.WriteAsync(header, ct);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        capture.DataAvailable += (_, e) =>
        {
            try
            {
                byte[] payload;
                if (isFloat)
                {
                    // float 32 -> PCM 16 bits : moitié moins de bande passante
                    int samples = e.BytesRecorded / 4;
                    payload = new byte[samples * 2];
                    for (int i = 0; i < samples; i++)
                    {
                        float v = BitConverter.ToSingle(e.Buffer, i * 4);
                        short s = (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
                        payload[i * 2] = (byte)s;
                        payload[i * 2 + 1] = (byte)(s >> 8);
                    }
                }
                else
                {
                    payload = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, payload, e.BytesRecorded);
                }
                net.Write(payload, 0, payload.Length);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        };
        capture.RecordingStopped += (_, _) => done.TrySetResult();

        capture.StartRecording();
        Status($"🎵 Diffusion du son de ce PC vers « {receiverName} »");

        await using (ct.Register(() =>
        {
            try { capture.StopRecording(); } catch { }
            done.TrySetResult();
        }))
        {
            await done.Task;
        }
        throw new IOException("le flux s'est arrêté");
    }
}
