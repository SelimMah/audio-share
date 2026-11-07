using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace PodGate;

/// <summary>
/// Une annonce BLE « proximity pairing » d'Apple. Les AirPods en émettent dès que le boîtier
/// est ouvert ou qu'ils sont hors du boîtier, même connectés à un iPhone, sans aucun appairage.
/// </summary>
sealed record AirPodsAdvert(
    ushort Model, short Rssi,
    int? Left, int? Right, int? Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging)
{
    public string ModelName => AppleModels.Name(Model);

    public string BatteryText
    {
        get
        {
            var parts = new List<string>();
            if (Left is int l) parts.Add($"G {l} %");
            if (Right is int r) parts.Add($"D {r} %");
            if (Case is int c) parts.Add($"Boîtier {c} %");
            return parts.Count == 0 ? "" : "Batterie : " + string.Join(" · ", parts);
        }
    }
}

static class AppleModels
{
    // Identifiants de modèle présents dans l'annonce BLE (= Product ID Bluetooth d'Apple).
    static readonly Dictionary<ushort, string> Names = new()
    {
        [0x2002] = "AirPods (1re gén.)",
        [0x200F] = "AirPods (2e gén.)",
        [0x2013] = "AirPods (3e gén.)",
        [0x200E] = "AirPods Pro",
        [0x2014] = "AirPods Pro 2",
        [0x2024] = "AirPods Pro 2 (USB-C)",
        [0x200A] = "AirPods Max",
    };

    public static string Name(ushort model) =>
        Names.TryGetValue(model, out var name) ? name : $"Écouteurs Apple/Beats (0x{model:X4})";
}

sealed class AirPodsScanner : IDisposable
{
    const ushort AppleCompanyId = 0x004C;
    const byte ProximityPairingType = 0x07;

    BluetoothLEAdvertisementWatcher? _watcher;
    System.Threading.Timer? _restartTimer;
    volatile bool _disposed;
    readonly object _sync = new();

    public event Action<AirPodsAdvert>? AdvertReceived;

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                if (_watcher != null && _watcher.Status is BluetoothLEAdvertisementWatcherStatus.Aborted
                                                          or BluetoothLEAdvertisementWatcherStatus.Stopped)
                    DropWatcher();

                if (_watcher == null)
                {
                    _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                    _watcher.Received += OnReceived;
                    _watcher.Stopped += OnStopped;
                }
                if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    _watcher.Start();
                    Log.Info("Scan BLE démarré");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Scan BLE impossible ({ex.Message}). Nouvel essai dans 10 s.");
                ScheduleRestart();
            }
        }
    }

    void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (_disposed) return;
        Log.Warn($"Scan BLE arrêté ({args.Error}). Nouvel essai dans 10 s (Bluetooth désactivé ?).");
        ScheduleRestart();
    }

    void ScheduleRestart()
    {
        if (_disposed) return;
        _restartTimer?.Dispose();
        _restartTimer = new System.Threading.Timer(_ => Start(), null, 10_000, Timeout.Infinite);
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            foreach (var manufacturer in args.Advertisement.ManufacturerData)
            {
                if (manufacturer.CompanyId != AppleCompanyId || manufacturer.Data.Length == 0) continue;
                var data = new byte[manufacturer.Data.Length];
                using (var reader = DataReader.FromBuffer(manufacturer.Data))
                    reader.ReadBytes(data);

                var advert = Parse(data, args.RawSignalStrengthInDBm);
                if (advert != null) AdvertReceived?.Invoke(advert);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Annonce BLE illisible : {ex.Message}");
        }
    }

    /// <summary>
    /// Données constructeur Apple = suite de blocs [type][longueur][contenu].
    /// Bloc 0x07 (proximity pairing) : 01 | modèle (2 octets, LE) | état | batteries G/D | charge + boîtier | couvercle | couleur | …
    /// </summary>
    public static AirPodsAdvert? Parse(byte[] data, short rssi)
    {
        int i = 0;
        while (i + 2 <= data.Length)
        {
            byte type = data[i];
            int length = data[i + 1];
            int start = i + 2;
            if (start + length > data.Length) break;
            if (type == ProximityPairingType && length >= 8)
                return ParseProximity(data.AsSpan(start, length), rssi);
            i = start + length;
        }
        return null;
    }

    static AirPodsAdvert? ParseProximity(ReadOnlySpan<byte> p, short rssi)
    {
        ushort model = (ushort)(p[1] | (p[2] << 8));
        if ((model & 0xFF00) != 0x2000) return null; // pas un appareil audio Apple/Beats

        byte status = p[3];
        byte pods = p[4];
        byte charge = p[5];

        // Le bit 0x20 indique quel écouteur émet : il inverse l'ordre gauche/droite.
        bool flipped = (status & 0x20) == 0;
        int high = pods >> 4, low = pods & 0x0F;
        int left = flipped ? high : low;
        int right = flipped ? low : high;
        int caseLevel = charge & 0x0F;
        int chargeBits = charge >> 4;

        return new AirPodsAdvert(
            model, rssi,
            Percent(left), Percent(right), Percent(caseLevel),
            (chargeBits & (flipped ? 0b010 : 0b001)) != 0,
            (chargeBits & (flipped ? 0b001 : 0b010)) != 0,
            (chargeBits & 0b100) != 0);
    }

    /// <summary>Niveaux 0 à 10 (par tranches de 10 %), 15 = inconnu (écouteur absent, boîtier fermé…).</summary>
    static int? Percent(int level) => level <= 10 ? level * 10 : null;

    void DropWatcher()
    {
        if (_watcher == null) return;
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        try { _watcher.Stop(); } catch { }
        _watcher = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _restartTimer?.Dispose();
            DropWatcher();
        }
    }
}
