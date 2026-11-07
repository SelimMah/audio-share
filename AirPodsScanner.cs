using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace AudioShare;

/// <summary>
/// Une annonce BLE « proximity pairing » d'Apple. Les AirPods en émettent dès
/// que le boîtier est ouvert ou qu'ils en sont sortis, même connectés à un
/// iPhone, sans aucun appairage — mais AUSSI, moins souvent, rangés dans le
/// boîtier fermé : la seule réception d'annonces ne dit donc pas qu'ils sont
/// disponibles, il faut lire l'état du couvercle et des écouteurs.
/// </summary>
internal sealed record AirPodsAdvert(
    ushort Model, short Rssi,
    int? Left, int? Right, int? Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging,
    bool BothInCase, bool LidClosed, int LidOpenCount,
    byte RawStatus, byte RawLid)
{
    public string ModelName => AppleModels.Name(Model);

    /// <summary>Rangés : les deux écouteurs dans le boîtier, couvercle fermé.</summary>
    public bool Stowed => BothInCase && LidClosed;

    /// <summary>« Batterie : G 80 % · D 70 % · Boîtier 50 % », ou vide si inconnue.</summary>
    public string BatteryText
    {
        get
        {
            var parts = new List<string>();
            if (Left is int l) parts.Add(Loc.T($"L {l}%", $"G {l} %"));
            if (Right is int r) parts.Add(Loc.T($"R {r}%", $"D {r} %"));
            if (Case is int c) parts.Add(Loc.T($"Case {c}%", $"Boîtier {c} %"));
            return parts.Count == 0 ? "" : Loc.T("Battery: ", "Batterie : ") + string.Join(" · ", parts);
        }
    }
}

internal static class AppleModels
{
    // Identifiants de modèle présents dans l'annonce BLE (= Product ID Bluetooth d'Apple).
    private static readonly Dictionary<ushort, (string En, string Fr)> Names = new()
    {
        [0x2002] = ("AirPods (1st gen)", "AirPods (1re gén.)"),
        [0x200F] = ("AirPods (2nd gen)", "AirPods (2e gén.)"),
        [0x2013] = ("AirPods (3rd gen)", "AirPods (3e gén.)"),
        [0x200E] = ("AirPods Pro", "AirPods Pro"),
        [0x2014] = ("AirPods Pro 2", "AirPods Pro 2"),
        [0x2024] = ("AirPods Pro 2 (USB-C)", "AirPods Pro 2 (USB-C)"),
        [0x200A] = ("AirPods Max", "AirPods Max"),
    };

    public static string Name(ushort model) =>
        Names.TryGetValue(model, out var name)
            ? Loc.T(name.En, name.Fr)
            : Loc.T($"Apple/Beats earbuds (0x{model:X4})", $"Écouteurs Apple/Beats (0x{model:X4})");
}

/// <summary>
/// Écoute les annonces BLE et remonte celles des écouteurs Apple. Se relance
/// seul toutes les 10 s si le scan s'arrête (Bluetooth coupé…).
/// L'événement est levé sur un thread WinRT : à rapatrier sur l'UI.
/// </summary>
internal sealed class AirPodsScanner : IDisposable
{
    private const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairingType = 0x07;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private System.Threading.Timer? _restartTimer;
    private volatile bool _disposed;
    private readonly object _sync = new();

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
                    Log.Write("AirPods : scan BLE démarré");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"AirPods : scan BLE impossible ({ex.Message}) — nouvel essai dans 10 s");
                ScheduleRestart();
            }
        }
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (_disposed) return;
        Log.Write($"AirPods : scan BLE arrêté ({args.Error}) — nouvel essai dans 10 s (Bluetooth désactivé ?)");
        ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        if (_disposed) return;
        _restartTimer?.Dispose();
        _restartTimer = new System.Threading.Timer(_ => Start(), null, 10_000, Timeout.Infinite);
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
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
            Log.Write($"AirPods : annonce BLE illisible — {ex.Message}");
        }
    }

    /// <summary>
    /// Données constructeur Apple = suite de blocs [type][longueur][contenu].
    /// Bloc 0x07 (proximity pairing) : 01 | modèle (2 octets, LE) | état | batteries G/D | charge + boîtier | couvercle | couleur | …
    /// Octet d'état : 0x02 écouteur émetteur à l'oreille, 0x04 les deux dans
    /// le boîtier, 0x08 l'autre à l'oreille, 0x20 émis par l'écouteur gauche.
    /// Octet couvercle : bits 0-2 compteur d'ouvertures, 0x08 couvercle fermé.
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

    private static AirPodsAdvert? ParseProximity(ReadOnlySpan<byte> p, short rssi)
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
        byte lid = p[6];

        return new AirPodsAdvert(
            model, rssi,
            Percent(left), Percent(right), Percent(caseLevel),
            (chargeBits & (flipped ? 0b010 : 0b001)) != 0,
            (chargeBits & (flipped ? 0b001 : 0b010)) != 0,
            (chargeBits & 0b100) != 0,
            BothInCase: (status & 0x04) != 0,
            LidClosed: (lid & 0x08) != 0,
            LidOpenCount: lid & 0x07,
            RawStatus: status, RawLid: lid);
    }

    /// <summary>Niveaux 0 à 10 (par tranches de 10 %), 15 = inconnu (écouteur absent, boîtier fermé…).</summary>
    private static int? Percent(int level) => level <= 10 ? level * 10 : null;

    private void DropWatcher()
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
