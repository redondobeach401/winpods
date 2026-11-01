namespace WinPods.Core.Aap;

/// <summary>
/// A message-oriented channel to the AirPods' AAP endpoint (L2CAP PSM 0x1001).
/// Each <see cref="ReceiveAsync"/> call returns exactly one L2CAP SDU.
/// </summary>
public interface IAapTransport : IAsyncDisposable
{
    /// <summary>False when the platform cannot open L2CAP channels (see <see cref="UnavailableReason"/>).</summary>
    bool IsSupported { get; }

    string? UnavailableReason { get; }

    Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);

    /// <summary>Returns the number of bytes written to <paramref name="buffer"/>; 0 means the channel closed.</summary>
    Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
