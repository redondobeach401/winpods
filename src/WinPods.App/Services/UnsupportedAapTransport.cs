using WinPods.Core.Aap;

namespace WinPods.App.Services;

/// <summary>
/// Placeholder AAP transport. Windows exposes no user-mode API to open Bluetooth Classic L2CAP
/// channels (Winsock AF_BTH only supports RFCOMM), and AirPods' control protocol lives on L2CAP
/// PSM 0x1001. A kernel-mode Bluetooth profile driver is required; see docs/roadmap.md.
/// </summary>
public sealed class UnsupportedAapTransport : IAapTransport
{
    public bool IsSupported => false;

    public string? UnavailableReason =>
        "El control de cancelación de ruido necesita el driver L2CAP de WinPods (aún no incluido). " +
        "Windows no permite abrir canales L2CAP desde una aplicación normal.";

    public Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken) => throw new NotSupportedException(UnavailableReason);
    public Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => throw new NotSupportedException(UnavailableReason);
    public Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException(UnavailableReason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
