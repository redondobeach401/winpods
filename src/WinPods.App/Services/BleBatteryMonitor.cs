using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;
using WinPods.Core.Ble;

namespace WinPods.App.Services;

/// <summary>
/// Listens for AirPods proximity-pairing BLE adverts. Works without any special driver and gives
/// battery (10 % steps), charging, in-ear and model info even before the AAP channel is open.
/// </summary>
public sealed class BleBatteryMonitor : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Passive };
    private readonly Dictionary<ulong, (ProximityPairingMessage Message, short Rssi, DateTime Seen)> _recent = [];
    private readonly object _lock = new();

    public BleBatteryMonitor()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) => Stopped?.Invoke(e.Error.ToString());
    }

    public int MinimumRssi { get; set; } = -65;

    /// <summary>Best (strongest, most recent) advert. Raised on a background thread.</summary>
    public event Action<ProximityPairingMessage>? Updated;

    public event Action<string>? Stopped;

    public void Start()
    {
        if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Start();
    }

    public void Stop() => _watcher.Stop();

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.RawSignalStrengthInDBm < MinimumRssi) return;

        foreach (var data in args.Advertisement.GetManufacturerDataByCompanyId(ProximityPairingMessage.AppleCompanyId))
        {
            if (!ProximityPairingMessage.TryParse(data.Data.ToArray(), out var message) || message is null) continue;

            ProximityPairingMessage best;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                // AirPods rotate their BLE address, so key by address and expire quickly.
                _recent[args.BluetoothAddress] = (message, args.RawSignalStrengthInDBm, now);
                foreach (var stale in _recent.Where(kv => now - kv.Value.Seen > TimeSpan.FromSeconds(15)).Select(kv => kv.Key).ToList())
                    _recent.Remove(stale);
                best = _recent.Values.OrderByDescending(v => v.Rssi).First().Message;
            }
            Updated?.Invoke(best);
        }
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
    }
}
