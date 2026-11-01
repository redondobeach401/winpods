using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinPods.Core;
using WinPods.Core.Aap;

namespace WinPods.App.Services;

/// <summary>
/// AAP transport backed by the WinPodsL2cap kernel driver (driver/WinPodsL2cap), which opens the
/// L2CAP channel (PSM 0x1001) that Windows does not expose to user mode.
/// Constants must match driver/WinPodsL2cap/public.h.
/// </summary>
public sealed class DriverAapTransport : IAapTransport
{
    public static readonly Guid InterfaceGuid = new("c8a7f1a2-3d2b-4f0e-9a55-5b0e4a6d1f77");

    private const uint IoctlConnect = 0x00222000;
    private const uint IoctlDisconnect = 0x00222004;
    private const uint IoctlRead = 0x00222008;
    private const uint IoctlWrite = 0x0022200C;

    // Synchronous handles serialise I/O, so reads (which pend) get their own handle.
    private SafeFileHandle? _control;
    private SafeFileHandle? _read;

    public bool IsSupported => FindDevicePath() is not null;

    public string? UnavailableReason => IsSupported ? null :
        "No se encontró el driver WinPodsL2cap. Instálalo (driver/README.md) y vuelve a conectar los AirPods; " +
        "mientras tanto Windows no permite controlar la cancelación de ruido.";

    public async Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        string path = FindDevicePath() ?? throw new NotSupportedException(UnavailableReason);
        _control = Open(path);
        _read = Open(path);

        var request = new byte[16];
        BitConverter.GetBytes(bluetoothAddress).CopyTo(request, 0);
        BitConverter.GetBytes((ushort)0).CopyTo(request, 8); // 0 = default AAP PSM

        await Task.Run(() => Ioctl(_control, IoctlConnect, request, null), cancellationToken).ConfigureAwait(false);
    }

    public Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        var control = _control ?? throw new InvalidOperationException("Not connected.");
        byte[] data = packet.ToArray();
        return Task.Run(() => Ioctl(control, IoctlWrite, data, null), cancellationToken);
    }

    public Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = _read ?? throw new InvalidOperationException("Not connected.");
        return Task.Run(() =>
        {
            using var registration = cancellationToken.Register(() => CancelIoEx(read, IntPtr.Zero));
            var temp = new byte[buffer.Length];
            if (!DeviceIoControl(read, IoctlRead, null, 0, temp, (uint)temp.Length, out uint returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                // Cancelled / device removed / channel closed: report end of stream.
                if (error is ErrorOperationAborted or ErrorDeviceNotConnected or ErrorInvalidHandle or ErrorGenCancelled) return 0;
                throw new System.ComponentModel.Win32Exception(error);
            }
            temp.AsSpan(0, (int)returned).CopyTo(buffer.Span);
            return (int)returned;
        }, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        var read = Interlocked.Exchange(ref _read, null);
        var control = Interlocked.Exchange(ref _control, null);
        if (read is { IsInvalid: false }) CancelIoEx(read, IntPtr.Zero);
        if (control is { IsInvalid: false })
        {
            try { DeviceIoControl(control, IoctlDisconnect, null, 0, null, 0, out _, IntPtr.Zero); } catch { }
            control.Dispose();
        }
        read?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void Ioctl(SafeFileHandle handle, uint code, byte[]? input, byte[]? output)
    {
        if (!DeviceIoControl(handle, code, input, (uint)(input?.Length ?? 0), output, (uint)(output?.Length ?? 0), out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    private static string? FindDevicePath()
    {
        var guid = InterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out uint length, ref guid, null, 0) != 0 || length <= 1) return null;
        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, length, 0) != 0) return null;
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    #region Native

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const int ErrorInvalidHandle = 6, ErrorOperationAborted = 995, ErrorGenCancelled = 1223, ErrorDeviceNotConnected = 1167;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid guid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputSize,
        byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    #endregion
}
