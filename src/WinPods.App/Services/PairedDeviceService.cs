using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using WinPods.Core;

namespace WinPods.App.Services;

public sealed record PairedAudioDevice(string Name, string Address, bool IsConnected)
{
    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>Finds paired Bluetooth Classic devices and tracks the connection state of the selected AirPods.</summary>
public sealed class PairedDeviceService : IDisposable
{
    private BluetoothDevice? _device;

    /// <summary>Raised on a background thread when the selected device connects/disconnects.</summary>
    public event Action<bool>? ConnectionChanged;

    public static async Task<IReadOnlyList<PairedAudioDevice>> GetPairedDevicesAsync()
    {
        var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        var result = new List<PairedAudioDevice>();
        foreach (var info in infos)
        {
            using var device = await BluetoothDevice.FromIdAsync(info.Id);
            if (device is null) continue;
            result.Add(new PairedAudioDevice(device.Name, BluetoothAddress.Format(device.BluetoothAddress),
                device.ConnectionStatus == BluetoothConnectionStatus.Connected));
        }
        // AirPods first.
        return result.OrderByDescending(d => d.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)).ThenBy(d => d.Name).ToList();
    }

    public static async Task<bool> IsConnectedAsync(string address)
    {
        if (!BluetoothAddress.TryParse(address, out var raw)) return false;
        using var device = await BluetoothDevice.FromBluetoothAddressAsync(raw);
        return device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    }

    public async Task<string?> SelectAsync(string? address)
    {
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
        if (!BluetoothAddress.TryParse(address, out var raw)) return null;

        _device = await BluetoothDevice.FromBluetoothAddressAsync(raw);
        if (_device is null) return null;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        ConnectionChanged?.Invoke(_device.ConnectionStatus == BluetoothConnectionStatus.Connected);
        return _device.Name;
    }

    private void OnConnectionStatusChanged(BluetoothDevice sender, object args) =>
        ConnectionChanged?.Invoke(sender.ConnectionStatus == BluetoothConnectionStatus.Connected);

    public void Dispose()
    {
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
        }
    }
}
