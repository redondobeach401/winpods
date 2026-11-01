using WinPods.Core.Aap;

namespace WinPods.Core.Tests;

public class AapProtocolTests
{
    [Fact]
    public void BuildsNoiseControlCommand() =>
        Assert.Equal(new byte[] { 0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x0D, 0x02, 0x00, 0x00, 0x00 },
            AapProtocol.SetNoiseControl(NoiseControlMode.NoiseCancellation));

    [Fact]
    public void BuildsConversationalAwarenessCommand()
    {
        Assert.Equal(0x28, AapProtocol.SetConversationalAwareness(true)[6]);
        Assert.Equal(0x01, AapProtocol.SetConversationalAwareness(true)[7]);
        Assert.Equal(0x02, AapProtocol.SetConversationalAwareness(false)[7]);
    }

    [Fact]
    public void ClampsAdaptiveLevel() => Assert.Equal(100, AapProtocol.SetAdaptiveAudioNoise(250)[7]);

    [Fact]
    public void ParsesBatteryNotification()
    {
        byte[] packet =
        [
            0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x03,
            0x02, 0x01, 0x64, 0x02, 0x01, // right 100 %, discharging
            0x04, 0x01, 0x63, 0x01, 0x01, // left 99 %, charging
            0x08, 0x01, 0x11, 0x04, 0x01, // case, disconnected
        ];
        var evt = Assert.IsType<BatteryEvent>(AapProtocol.Parse(packet));
        Assert.Equal(3, evt.Readings.Count);

        var state = new AapState();
        AapSession.Apply(state, evt);
        Assert.Equal(100, state.RightBattery);
        Assert.Equal(99, state.LeftBattery);
        Assert.True(state.LeftCharging);
        Assert.Null(state.CaseBattery);
    }

    [Fact]
    public void ParsesEarDetection()
    {
        var evt = Assert.IsType<EarDetectionEvent>(AapProtocol.Parse(new byte[] { 0x04, 0x00, 0x04, 0x00, 0x06, 0x00, 0x00, 0x01 }));
        Assert.Equal(EarState.InEar, evt.Primary);
        Assert.Equal(EarState.OutOfEar, evt.Secondary);
    }

    [Fact]
    public void ParsesSettingNotificationsIntoState()
    {
        var state = new AapState();
        AapSession.Apply(state, AapProtocol.Parse(AapProtocol.SetNoiseControl(NoiseControlMode.Adaptive))!);
        AapSession.Apply(state, AapProtocol.Parse(AapProtocol.SetConversationalAwareness(false))!);
        AapSession.Apply(state, AapProtocol.Parse(AapProtocol.SetAdaptiveAudioNoise(40))!);
        Assert.Equal(NoiseControlMode.Adaptive, state.NoiseControl);
        Assert.False(state.ConversationalAwareness);
        Assert.Equal(40, state.AdaptiveAudioNoise);
    }

    [Fact]
    public void IgnoresHandshakeAndKeepsUnknownOpcodes()
    {
        Assert.Null(AapProtocol.Parse(AapProtocol.Handshake()));
        var unknown = Assert.IsType<UnknownEvent>(AapProtocol.Parse(new byte[] { 0x04, 0x00, 0x04, 0x00, 0x1D, 0x00, 0xAA }));
        Assert.Equal(0x1D, unknown.Opcode);
    }

    [Fact]
    public async Task SessionPerformsHandshakeAndTracksState()
    {
        var transport = new FakeTransport();
        transport.Incoming.Add(AapProtocol.SetNoiseControl(NoiseControlMode.Transparency));
        await using var session = new AapSession(transport);
        var closed = new TaskCompletionSource();
        session.Closed += _ => closed.TrySetResult();

        await session.StartAsync(0xA4C6F0123456);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AapProtocol.Handshake(), transport.Sent[0]);
        Assert.Equal(AapProtocol.RequestNotifications(), transport.Sent[2]);
        Assert.Equal(NoiseControlMode.Transparency, session.State.NoiseControl);
    }

    private sealed class FakeTransport : IAapTransport
    {
        public List<byte[]> Sent { get; } = [];
        public List<byte[]> Incoming { get; } = [];
        public bool IsSupported => true;
        public string? UnavailableReason => null;
        public Task ConnectAsync(ulong a, CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> p, CancellationToken ct) { Sent.Add(p.ToArray()); return Task.CompletedTask; }
        public Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (Incoming.Count == 0) return Task.FromResult(0);
            var p = Incoming[0]; Incoming.RemoveAt(0);
            p.CopyTo(buffer);
            return Task.FromResult(p.Length);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
