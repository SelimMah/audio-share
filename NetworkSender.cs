using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioShare;

/// <summary>
/// Émission : capture tout le son de ce PC (boucle WASAPI sur la sortie par
/// défaut), le convertit en PCM 16 bits et l'envoie à un autre Audio Share
/// découvert sur le réseau local. Pendant l'émission :
///  - les haut-parleurs locaux sont coupés (le prélèvement de la boucle se
///    fait avant le muet), et re-coupés si les touches volume les rallument ;
///  - les boutons physiques de volume pilotent le niveau ENVOYÉ : sur les
///    périphériques à volume matériel le prélèvement ignore le volume, on
///    l'applique donc nous-mêmes aux échantillons ;
///  - volume et balance de l'app sont relayés au récepteur (contrôles
///    partagés) par datagrammes UDP.
/// </summary>
internal sealed class NetworkSender : IDisposable
{
    private CancellationTokenSource? _cts;
    private volatile string _state = "";
    private IPAddress? _controlTarget;
    private readonly UdpClient _control = new();

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

    // ---------- Contrôles partagés (volume / balance vers le récepteur) ----------

    public void SendVolume(float volume) =>
        SendControl($"ASHAREVOL {volume.ToString(CultureInfo.InvariantCulture)}");

    public void SendBalance(float left, float right) =>
        SendControl($"ASHAREBAL {left.ToString(CultureInfo.InvariantCulture)} {right.ToString(CultureInfo.InvariantCulture)}");

    private void SendControl(string message)
    {
        var target = _controlTarget;
        if (target == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            _control.Send(bytes, bytes.Length, new IPEndPoint(target, NetworkReceiver.DiscoveryPort));
        }
        catch { /* datagramme perdu : le prochain corrigera */ }
    }

    // ---------- Boucle principale ----------

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
        _controlTarget = address;

        using var capture = new WasapiLoopbackCapture();
        var format = capture.WaveFormat;
        bool isFloat = format.BitsPerSample == 32; // le mix WASAPI est en float 32

        // --- Coupure des haut-parleurs locaux, volume physique -> flux ---
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var endpointVolume = endpoint.AudioEndpointVolume;

        // Volume matériel : le prélèvement ignore le curseur, on applique donc
        // le volume nous-mêmes. Volume logiciel : il est déjà dans le signal.
        bool scaleWithMaster =
            (endpointVolume.HardwareSupport & EEndpointHardwareSupport.Volume) != 0;
        float sendScale = scaleWithMaster ? endpointVolume.MasterVolumeLevelScalar : 1f;

        bool restoreMute = endpointVolume.Mute;
        endpointVolume.Mute = true;
        Log.Write($"Émission : haut-parleurs coupés (volume matériel : {scaleWithMaster})");

        // Les touches volume de Windows rallument le son : on le recoupe
        // aussitôt, et on relit le curseur pour suivre le volume voulu.
        using var muteGuard = new System.Timers.Timer(150) { AutoReset = true };
        muteGuard.Elapsed += (_, _) =>
        {
            try
            {
                if (!endpointVolume.Mute) endpointVolume.Mute = true;
                sendScale = scaleWithMaster ? endpointVolume.MasterVolumeLevelScalar : 1f;
            }
            catch { /* périphérique parti (déconnexion) : la capture s'arrêtera */ }
        };
        muteGuard.Start();

        try
        {
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
                    float scale = sendScale;
                    if (isFloat)
                    {
                        // float 32 -> PCM 16 bits : moitié moins de bande passante
                        int samples = e.BytesRecorded / 4;
                        payload = new byte[samples * 2];
                        for (int i = 0; i < samples; i++)
                        {
                            float v = BitConverter.ToSingle(e.Buffer, i * 4) * scale;
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
            SetState($"🎵 Diffusion vers « {receiverName} » — haut-parleurs de ce PC coupés, contrôles partagés.");

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
        finally
        {
            _controlTarget = null;
            muteGuard.Stop();
            try { endpointVolume.Mute = restoreMute; } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        _control.Dispose();
    }
}
