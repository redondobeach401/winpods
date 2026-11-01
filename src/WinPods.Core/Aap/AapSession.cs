namespace WinPods.Core.Aap;

/// <summary>Live state of the AirPods as reported over AAP.</summary>
public sealed class AapState
{
    public int? LeftBattery { get; internal set; }
    public int? RightBattery { get; internal set; }
    public int? CaseBattery { get; internal set; }
    public bool LeftCharging { get; internal set; }
    public bool RightCharging { get; internal set; }
    public bool CaseCharging { get; internal set; }
    public EarState? PrimaryEar { get; internal set; }
    public EarState? SecondaryEar { get; internal set; }
    public NoiseControlMode? NoiseControl { get; internal set; }
    public bool? ConversationalAwareness { get; internal set; }
    public bool? AdaptiveVolume { get; internal set; }
    public bool? OneBudAnc { get; internal set; }
    public int? AdaptiveAudioNoise { get; internal set; }
    public ListeningModeCycle? ListeningModeCycle { get; internal set; }
}

/// <summary>Drives an AAP conversation over an <see cref="IAapTransport"/> and keeps <see cref="State"/> up to date.</summary>
public sealed class AapSession : IAsyncDisposable
{
    private readonly IAapTransport _transport;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    public AapSession(IAapTransport transport) => _transport = transport;

    public AapState State { get; } = new();

    /// <summary>Raised (on a background thread) for every parsed packet, after <see cref="State"/> was updated.</summary>
    public event Action<AapEvent>? EventReceived;

    /// <summary>Raised when the read loop ends (AirPods disconnected or error).</summary>
    public event Action<Exception?>? Closed;

    public bool IsConnected => _readLoop is { IsCompleted: false };

    public async Task StartAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        if (!_transport.IsSupported)
            throw new NotSupportedException(_transport.UnavailableReason ?? "AAP transport not supported.");

        await _transport.ConnectAsync(bluetoothAddress, cancellationToken).ConfigureAwait(false);
        await _transport.SendAsync(AapProtocol.Handshake(), cancellationToken).ConfigureAwait(false);
        await _transport.SendAsync(AapProtocol.SetFeatureFlags(), cancellationToken).ConfigureAwait(false);
        await _transport.SendAsync(AapProtocol.RequestNotifications(), cancellationToken).ConfigureAwait(false);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public Task SetNoiseControlAsync(NoiseControlMode mode, CancellationToken ct = default) =>
        SendAsync(AapProtocol.SetNoiseControl(mode), ct);

    public Task SetConversationalAwarenessAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync(AapProtocol.SetConversationalAwareness(enabled), ct);

    public Task SetAdaptiveVolumeAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync(AapProtocol.SetAdaptiveVolume(enabled), ct);

    public Task SetOneBudAncAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync(AapProtocol.SetOneBudAnc(enabled), ct);

    public Task SetAdaptiveAudioNoiseAsync(int level, CancellationToken ct = default) =>
        SendAsync(AapProtocol.SetAdaptiveAudioNoise(level), ct);

    public Task SetListeningModeCycleAsync(ListeningModeCycle modes, CancellationToken ct = default) =>
        SendAsync(AapProtocol.SetListeningModeCycle(modes), ct);

    private Task SendAsync(byte[] packet, CancellationToken ct) => _transport.SendAsync(packet, ct);

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024];
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _transport.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0) break;
                var evt = AapProtocol.Parse(buffer.AsSpan(0, n));
                if (evt is null) continue;
                Apply(State, evt);
                EventReceived?.Invoke(evt);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { error = ex; }
        Closed?.Invoke(error);
    }

    /// <summary>Folds an event into the state object. Public for testing.</summary>
    public static void Apply(AapState state, AapEvent evt)
    {
        switch (evt)
        {
            case BatteryEvent b:
                foreach (var r in b.Readings)
                {
                    bool present = r.State != BatteryChargeState.Disconnected;
                    bool charging = r.State == BatteryChargeState.Charging;
                    switch (r.Component)
                    {
                        case BatteryComponent.Left:
                            state.LeftBattery = present ? r.Percent : null; state.LeftCharging = charging; break;
                        case BatteryComponent.Right:
                            state.RightBattery = present ? r.Percent : null; state.RightCharging = charging; break;
                        case BatteryComponent.Case:
                            state.CaseBattery = present ? r.Percent : null; state.CaseCharging = charging; break;
                    }
                }
                break;
            case EarDetectionEvent e:
                state.PrimaryEar = e.Primary;
                state.SecondaryEar = e.Secondary;
                break;
            case SettingEvent s:
                switch (s.Setting)
                {
                    case AapSetting.ListeningMode when Enum.IsDefined(typeof(NoiseControlMode), s.Value):
                        state.NoiseControl = (NoiseControlMode)s.Value; break;
                    case AapSetting.ConversationalAwareness:
                        state.ConversationalAwareness = AapProtocol.ToBool(s.Value); break;
                    case AapSetting.AdaptiveVolume:
                        state.AdaptiveVolume = AapProtocol.ToBool(s.Value); break;
                    case AapSetting.OneBudAncMode:
                        state.OneBudAnc = AapProtocol.ToBool(s.Value); break;
                    case AapSetting.AdaptiveAudioNoise:
                        state.AdaptiveAudioNoise = s.Value; break;
                    case AapSetting.ListeningModeConfigs:
                        state.ListeningModeCycle = (ListeningModeCycle)s.Value; break;
                }
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* already reported via Closed */ }
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
