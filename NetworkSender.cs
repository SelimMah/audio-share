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
///  - les haut-parleurs locaux sont coupés : muets ET à 0 %, et les touches
///    volume sont interceptées avant Windows (VolumeKeyHook) — aucune fuite
///    possible ;
///  - les réglages audio (muet, volume) sont sauvegardés à l'activation et
///    rétablis à l'arrêt de l'émission ou à la fermeture de l'app ;
///  - le volume ÉMIS est purement logiciel, appliqué aux échantillons :
///    le curseur de l'app le fixe directement, les touches interceptées le
///    déplacent par pas de 2 % avec un aperçu façon Windows (VolumeOsd) ;
///  - la balance de l'app est relayée au récepteur (contrôles partagés)
///    par datagrammes UDP.
/// </summary>
internal sealed class NetworkSender : IDisposable
{
    // Volume réel auquel le périphérique est épinglé pendant l'émission :
    // 0 %, car les touches volume sont interceptées par VolumeKeyHook avant
    // Windows — rien ne peut plus démuter ni monter le vrai volume, et même
    // un démutage par un autre biais resterait silencieux.
    private const float PinnedVolume = 0f;

    // Pas d'un appui de touche volume, comme l'OSD de Windows (2/100).
    private const float KeyStep = 0.02f;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile string _state = "";
    private IPAddress? _controlTarget;
    private readonly UdpClient _control = new();
    private float _sendVolume = 1f;   // volume émis, appliqué aux échantillons
    private bool _volumeSeeded;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public string State => _state;

    /// <summary>Levé sur un thread quelconque à chaque changement d'état.</summary>
    public event Action? Changed;

    /// <summary>
    /// Volume émis (touches physiques ou curseur de l'app) — pour garder le
    /// curseur synchronisé. Levé sur un thread quelconque.
    /// </summary>
    public event Action<float>? EmissionVolumeChanged;

    public void Start()
    {
        Stop();
        _volumeSeeded = false;
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
    /// Fixe le volume émis, appliqué par l'app aux échantillons envoyés :
    /// aucun périphérique dans la boucle, effet immédiat et garanti.
    /// </summary>
    public void SetEmissionVolume(float volume)
    {
        _sendVolume = Math.Clamp(volume, 0f, 1f);
        EmissionVolumeChanged?.Invoke(_sendVolume);
    }

    /// <summary>Appui volume +/− intercepté : ajuste le volume émis d'un pas.</summary>
    public float NudgeEmissionVolume(int steps)
    {
        SetEmissionVolume(_sendVolume + steps * KeyStep);
        return _sendVolume;
    }

    /// <summary>Touche muet interceptée : bascule le volume émis 0 ↔ précédent.</summary>
    public float ToggleEmissionMute()
    {
        if (_sendVolume > 0f)
        {
            _lastAudibleVolume = _sendVolume;
            SetEmissionVolume(0f);
        }
        else
        {
            SetEmissionVolume(_lastAudibleVolume > 0f ? _lastAudibleVolume : 0.5f);
        }
        return _sendVolume;
    }

    private float _lastAudibleVolume;

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

        // --- Coupure des haut-parleurs locaux, volume émis logiciel ---
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var endpointVolume = endpoint.AudioEndpointVolume;

        // Réglages audio sauvegardés à l'activation : muet ET volume sont
        // rétablis tels quels à l'arrêt de l'émission ou à la fermeture.
        bool restoreMute = endpointVolume.Mute;
        float restoreVolume = endpointVolume.MasterVolumeLevelScalar;

        // Le volume ÉMIS est purement logiciel (_sendVolume, appliqué aux
        // échantillons) : le curseur volume réel du périphérique ne module pas
        // le prélèvement de la boucle de façon fiable (l'heuristique
        // HardwareSupport s'est révélée fausse selon les machines). Le
        // périphérique, lui, est muet ET à 0 % : les touches volume étant
        // interceptées en amont (VolumeKeyHook), rien ne le rallume, et un
        // démutage par un autre biais resterait silencieux.
        if (!_volumeSeeded)
        {
            // On démarre au volume que l'utilisateur avait ; les reconnexions
            // suivantes gardent le volume ajusté pendant l'émission.
            _sendVolume = Math.Clamp(restoreVolume, 0f, 1f);
            _volumeSeeded = true;
        }
        endpointVolume.Mute = true;
        endpointVolume.MasterVolumeLevelScalar = PinnedVolume;
        Log.Write($"Émission : haut-parleurs coupés, volume émis {_sendVolume:0.00}");

        // Filet de sécurité : si le volume réel bouge quand même (curseur des
        // réglages Windows, source non interceptée), le déplacement par
        // rapport au plancher est replié dans le volume émis puis le
        // périphérique est ré-épinglé. Nos propres remises au plancher
        // donnent un delta nul et sont donc ignorées.
        void OnVolumeNotification(NAudio.CoreAudioApi.AudioVolumeNotificationData data)
        {
            try
            {
                if (!data.Muted) endpointVolume.Mute = true;
                float delta = data.MasterVolume - PinnedVolume;
                if (Math.Abs(delta) > 0.001f)
                {
                    SetEmissionVolume(_sendVolume + delta);
                    endpointVolume.MasterVolumeLevelScalar = PinnedVolume;
                }
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
            }
            catch { }
        };
        muteGuard.Start();

        EmissionVolumeChanged?.Invoke(_sendVolume);

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
                    float scale = _sendVolume;
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
