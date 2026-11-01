namespace WinPods.Core.Handoff;

/// <summary>Connects / disconnects the AirPods' audio profiles (A2DP + HFP) on this computer.</summary>
public interface IAudioRouteController
{
    Task<bool> ConnectAsync(string address, CancellationToken cancellationToken);
    Task<bool> DisconnectAsync(string address, CancellationToken cancellationToken);
    Task<bool> IsConnectedAsync(string address, CancellationToken cancellationToken);
}

public sealed record HandoffResult(bool Success, string Message);

/// <summary>
/// Moves the AirPods between this computer and a peer (e.g. Windows ⇄ Mac), mimicking Apple's
/// automatic switching: the side that wants the audio asks the other to release the AirPods first.
/// </summary>
public sealed class HandoffCoordinator
{
    private readonly IAudioRouteController _local;
    private readonly IHandoffClient _peer;
    private readonly Func<string?> _address;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HandoffCoordinator(IAudioRouteController local, IHandoffClient peer, Func<string?> address)
    {
        _local = local;
        _peer = peer;
        _address = address;
    }

    /// <summary>Time given to the AirPods to drop the previous host before we connect.</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(1500);

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>"Bring the audio here": peer releases, then this computer connects.</summary>
    public async Task<HandoffResult> PullHereAsync(CancellationToken ct = default)
    {
        var address = _address() ?? throw new InvalidOperationException("No AirPods selected.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string peerNote;
            try
            {
                var reply = await _peer.RequestAsync(HandoffMessageTypes.Release, ct).ConfigureAwait(false);
                peerNote = reply.Ok == true ? $"{reply.From} liberó los AirPods" : $"{reply.From}: {reply.Detail ?? "no pudo liberar"}";
                if (reply.Ok == true) await Delay(SettleDelay, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The peer may be asleep/offline: still try, the AirPods may be free.
                peerNote = $"no se pudo contactar al otro equipo ({ex.Message})";
            }

            bool connected = await _local.ConnectAsync(address, ct).ConfigureAwait(false);
            return connected
                ? new HandoffResult(true, $"Audio en este equipo; {peerNote}.")
                : new HandoffResult(false, $"No se pudo conectar los AirPods aquí; {peerNote}.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>"Send the audio to the other computer": disconnect here, then ask the peer to connect.</summary>
    public async Task<HandoffResult> PushToPeerAsync(CancellationToken ct = default)
    {
        var address = _address() ?? throw new InvalidOperationException("No AirPods selected.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _local.DisconnectAsync(address, ct).ConfigureAwait(false);
            await Delay(SettleDelay, ct).ConfigureAwait(false);
            try
            {
                var reply = await _peer.RequestAsync(HandoffMessageTypes.Acquire, ct).ConfigureAwait(false);
                if (reply.Ok == true) return new HandoffResult(true, $"Audio enviado a {reply.From}.");
                await _local.ConnectAsync(address, ct).ConfigureAwait(false);
                return new HandoffResult(false, $"{reply.From} no pudo conectar ({reply.Detail}); el audio vuelve aquí.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await _local.ConnectAsync(address, ct).ConfigureAwait(false);
                return new HandoffResult(false, $"No se pudo contactar al otro equipo ({ex.Message}); el audio vuelve aquí.");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Handles a verified request coming from the peer.</summary>
    public async Task<(bool Ok, string? Detail)> HandleRequestAsync(HandoffMessage request, CancellationToken ct)
    {
        var address = _address();
        if (address is null) return (false, "no AirPods configured");

        switch (request.Type)
        {
            case HandoffMessageTypes.Ping:
                return (true, await _local.IsConnectedAsync(address, ct).ConfigureAwait(false) ? "connected" : "idle");

            case HandoffMessageTypes.Release:
                if (!await _local.IsConnectedAsync(address, ct).ConfigureAwait(false)) return (true, "not connected");
                return await _local.DisconnectAsync(address, ct).ConfigureAwait(false)
                    ? (true, "released") : (false, "disconnect failed");

            case HandoffMessageTypes.Acquire:
                return await _local.ConnectAsync(address, ct).ConfigureAwait(false)
                    ? (true, "connected") : (false, "connect failed");

            default:
                return (false, $"unknown request '{request.Type}'");
        }
    }
}
