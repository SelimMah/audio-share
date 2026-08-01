using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;

namespace AudioShare;

/// <summary>
/// Émission : capture tout le son de ce PC (boucle WASAPI sur la sortie par
/// défaut), le convertit en PCM 16 bits et l'envoie à un autre Audio Share
/// découvert sur le réseau local. Se reconnecte tout seul en cas de coupure ;
/// ignore sa propre machine pour ne jamais se diffuser à soi-même.
/// </summary>
internal sealed class NetworkSender : IDisposable
{
    private CancellationTokenSource? _cts;
    private volatile string _state = "";

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public string State => _state;

    /// <summary>Levé sur un thread quelconque à chaque changement d'état.</summary>
    public event Action? Changed;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetState("");
    }

    private void SetState(string state)
    {
        _state = state;
        Changed?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState("🔍 Recherche d'un récepteur Audio Share sur le réseau…");
                var (address, name) = await DiscoverAsync(ct);
                SetState($"Récepteur trouvé : « {name} ». Connexion…");
                await StreamAsync(address, name, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                SetState($"Diffusion interrompue ({ex.Message}) — nouvel essai…");
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
            await udp.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, NetworkReceiver.DiscoveryPort), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(2000);
            try
            {
                while (true)
                {
                    var reply = await udp.ReceiveAsync(timeout.Token);
                    var text = Encoding.UTF8.GetString(reply.Buffer);
                    if (!text.StartsWith("ASHARE!")) continue;

                    var name = text[7..];
                    // Notre propre récepteur répond aussi à la sonde : l'ignorer.
                    if (string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return (reply.RemoteEndPoint.Address, name);
                }
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
        await client.ConnectAsync(address, NetworkReceiver.StreamPort, ct);
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
        SetState($"🎵 Diffusion du son de ce PC vers « {receiverName} »");

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

    public void Dispose() => Stop();
}
