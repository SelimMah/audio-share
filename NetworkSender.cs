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
///    fait avant le muet), et re-coupés IMMÉDIATEMENT si les touches volume
///    les rallument (notification du périphérique, pas un simple minuteur) ;
///  - les réglages audio (muet, volume) sont sauvegardés à l'activation et
///    rétablis à l'arrêt de l'émission ou à la fermeture de l'app ;
///  - touches physiques ET curseur de l'app pilotent le même volume réel du
///    périphérique, qui fixe le niveau ENVOYÉ : sur les périphériques à
///    volume matériel le prélèvement ignore le volume, on l'applique donc
///    nous-mêmes aux échantillons ;
///  - la balance de l'app est relayée au récepteur (contrôles partagés)
///    par datagrammes UDP.
/// </summary>
internal sealed class NetworkSender : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile string _state = "";
    private IPAddress? _controlTarget;
    private readonly UdpClient _control = new();
    private NAudio.CoreAudioApi.AudioEndpointVolume? _liveVolume;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public string State => _state;

    /// <summary>Levé sur un thread quelconque à chaque changement d'état.</summary>
    public event Action? Changed;

    /// <summary>
    /// Volume réel du périphérique de sortie pendant l'émission (touches
    /// physiques ou curseur de l'app) — pour garder le curseur synchronisé.
    /// </summary>
    public event Action<float>? DeviceVolumeChanged;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetState("");
    }

    /// <summary>
    /// Pendant l'émission, le curseur de l'app pilote le vrai volume du
    /// périphérique — le même que celui des touches physiques, qui fixe le
    /// niveau envoyé au récepteur.
    /// </summary>
    public void SetDeviceVolume(float volume)
    {
        var v = _liveVolume;
        if (v == null) return;
        try { v.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f); } catch { }
    }

    // ---------- Contrôles partagés (balance vers le récepteur) ----------

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

        // Réglages audio sauvegardés à l'activation : muet ET volume sont
        // rétablis tels quels à l'arrêt de l'émission ou à la fermeture.
        bool restoreMute = endpointVolume.Mute;
        float restoreVolume = endpointVolume.MasterVolumeLevelScalar;
        endpointVolume.Mute = true;
        Log.Write($"Émission : haut-parleurs coupés (volume matériel : {scaleWithMaster})");

        // La touche volume + de Windows RALLUME le son : la notification du
        // périphérique permet de le recouper immédiatement (une garde
        // périodique resterait audible), et de suivre le volume voulu.
        void OnVolumeNotification(NAudio.CoreAudioApi.AudioVolumeNotificationData data)
        {
            try
            {
                if (!data.Muted) endpointVolume.Mute = true;
                sendScale = scaleWithMaster ? data.MasterVolume : 1f;
                DeviceVolumeChanged?.Invoke(data.MasterVolume);
            }
            catch { /* périphérique parti (déconnexion) : la capture s'arrêtera */ }
        }
        endpointVolume.OnVolumeNotification += OnVolumeNotification;

        // Filet de sécurité si une notification se perd.
        using var muteGuard = new System.Timers.Timer(150) { AutoReset = true };
        muteGuard.Elapsed += (_, _) =>
        {
            try
            {
                if (!endpointVolume.Mute) endpointVolume.Mute = true;
                sendScale = scaleWithMaster ? endpointVolume.MasterVolumeLevelScalar : 1f;
            }
            catch { }
        };
        muteGuard.Start();

        _liveVolume = endpointVolume;
        DeviceVolumeChanged?.Invoke(restoreVolume);

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
            _liveVolume = null;
            muteGuard.Stop();
            endpointVolume.OnVolumeNotification -= OnVolumeNotification;
            try
            {
                endpointVolume.MasterVolumeLevelScalar = restoreVolume;
                endpointVolume.Mute = restoreMute;
            }
            catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        // À la fermeture de l'app, laisser le temps à la boucle d'émission de
        // rétablir les réglages audio sauvegardés (muet, volume).
        try { _runTask?.Wait(1500); } catch { }
        _control.Dispose();
    }
}
