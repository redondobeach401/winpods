using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinPods.Core;
using WinPods.Core.Handoff;

namespace WinPods.App.Services;

/// <summary>
/// Connects / disconnects the AirPods' audio on Windows the same way the "Connect" button in
/// Settings does: by sending KSPROPERTY_ONESHOT_RECONNECT / _DISCONNECT (KSPROPSETID_BtAudio) to
/// the Bluetooth audio kernel-streaming filters (A2DP "BTHENUM" and hands-free "BTHHFENUM").
/// No driver or admin rights required.
/// </summary>
public sealed class KsAudioRouteController : IAudioRouteController
{
    private static readonly Guid KsCategoryAudio = new("6994AD04-93EF-11D0-A3CC-00A0C9223196");
    private static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint KsPropertyOneshotReconnect = 0;
    private const uint KsPropertyOneshotDisconnect = 1;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public Task<bool> ConnectAsync(string address, CancellationToken ct) =>
        SendAndWaitAsync(address, KsPropertyOneshotReconnect, expectConnected: true, ct);

    public Task<bool> DisconnectAsync(string address, CancellationToken ct) =>
        SendAndWaitAsync(address, KsPropertyOneshotDisconnect, expectConnected: false, ct);

    public Task<bool> IsConnectedAsync(string address, CancellationToken ct) => PairedDeviceService.IsConnectedAsync(address);

    private async Task<bool> SendAndWaitAsync(string address, uint property, bool expectConnected, CancellationToken ct)
    {
        if (!BluetoothAddress.TryParse(address, out var raw))
            throw new ArgumentException($"Invalid Bluetooth address '{address}'.", nameof(address));
        string needle = raw.ToString("X12");

        var paths = FindAudioFilters(needle);
        if (paths.Count == 0)
            throw new InvalidOperationException("No se encontró el dispositivo de audio Bluetooth de los AirPods. ¿Están emparejados con este PC?");

        bool anySent = false;
        await Task.Run(() =>
        {
            foreach (var path in paths)
                anySent |= TrySendKsProperty(path, property);
        }, ct);
        if (!anySent) return false;

        var deadline = DateTime.UtcNow + ConnectTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await PairedDeviceService.IsConnectedAsync(address) == expectConnected) return true;
            await Task.Delay(300, ct);
        }
        return false;
    }

    private static List<string> FindAudioFilters(string addressHex)
    {
        var guid = KsCategoryAudio;
        foreach (uint flags in new[] { CmGetDeviceInterfaceListPresent, CmGetDeviceInterfaceListAllDevices })
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint length, ref guid, null, flags) != CrSuccess || length <= 1) continue;
            var buffer = new char[length];
            if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, length, flags) != CrSuccess) continue;

            var matches = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Contains(addressHex, StringComparison.OrdinalIgnoreCase)
                            && (p.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) || p.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matches.Count > 0) return matches;
        }
        return [];
    }

    private static unsafe bool TrySendKsProperty(string devicePath, uint propertyId)
    {
        using SafeFileHandle handle = CreateFileW(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var property = new KsProperty { Set = KsPropSetIdBtAudio, Id = propertyId, Flags = KsPropertyTypeGet };
        using var completed = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = completed.SafeWaitHandle.DangerousGetHandle() };
        uint returned = 0;

        bool ok = DeviceIoControl(handle, IoctlKsProperty, &property, (uint)sizeof(KsProperty), null, 0, &returned, &overlapped);
        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorIoPending) return false;
            ok = GetOverlappedResult(handle, &overlapped, &returned, true);
        }
        return ok;
    }

    #region Native

    private const uint CrSuccess = 0;
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint CmGetDeviceInterfaceListAllDevices = 1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;
    private const uint KsPropertyTypeGet = 1;
    // CTL_CODE(FILE_DEVICE_KS = 0x2F, 0x000, METHOD_NEITHER, FILE_ANY_ACCESS)
    private const uint IoctlKsProperty = 0x002F0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize,
        void* outBuffer, uint outBufferSize, uint* bytesReturned, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, uint* bytesTransferred, bool wait);

    #endregion
}
