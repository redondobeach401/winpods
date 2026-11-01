using System.Net;
using WinPods.Core.Handoff;

namespace WinPods.Core.Tests;

public class HandoffTests
{
    private const string Addr = "A4:C6:F0:12:34:56";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void MatchesReferenceVector()
    {
        // Shared with mac/WinPodsAgent (and docs/handoff-protocol.md) to keep both implementations in sync.
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var signer = new HandoffSigner("correct horse battery staple", clock);
        var msg = new HandoffMessage { Type = "release", From = "PC", Timestamp = 1_700_000_000, Nonce = "00112233445566778899aabb", Address = Addr };
        Assert.Equal("v1\nrelease\nPC\n1700000000\n00112233445566778899aabb\nA4:C6:F0:12:34:56\n\n", msg.CanonicalString());
        Assert.Equal("f7a722d01d20327edaed7381a343a57b31ffecc1cec7e31147e5f106668e1575", signer.ComputeSignature(msg));
    }

    [Fact]
    public void VerifiesSignedMessageAndRejectsTamperingReplayAndStaleness()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var signer = new HandoffSigner("secret", clock);
        var verifier = new HandoffSigner("secret", clock);
        var msg = signer.Sign(HandoffMessage.Create(HandoffMessageTypes.Release, "PC", Addr, clock));

        Assert.True(verifier.Verify(msg, out _));
        Assert.False(verifier.Verify(msg, out var replay));
        Assert.Contains("replay", replay);

        var tampered = signer.Sign(HandoffMessage.Create(HandoffMessageTypes.Release, "PC", Addr, clock)) with { Type = "acquire" };
        Assert.False(verifier.Verify(tampered, out var bad));
        Assert.Contains("signature", bad);

        var wrongKey = new HandoffSigner("other", clock).Sign(HandoffMessage.Create("ping", "PC", Addr, clock));
        Assert.False(verifier.Verify(wrongKey, out _));

        var old = signer.Sign(HandoffMessage.Create("ping", "PC", Addr, clock));
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.False(verifier.Verify(old, out var stale));
        Assert.Contains("stale", stale);
    }

    [Fact]
    public void JsonRoundTrips()
    {
        var msg = new HandoffSigner("s").Sign(HandoffMessage.Create("ping", "PC", Addr));
        Assert.Equal(msg, HandoffMessage.FromJsonLine(msg.ToJsonLine()));
        Assert.DoesNotContain("reply_to", msg.ToJsonLine());
        Assert.Null(HandoffMessage.FromJsonLine("{not json"));
    }

    [Fact]
    public async Task PullHereAsksPeerToReleaseThenConnectsLocally()
    {
        var local = new FakeRoute();
        var peer = new FakePeer(ok: true);
        var c = new HandoffCoordinator(local, peer, () => Addr) { Delay = (_, _) => Task.CompletedTask };

        var r = await c.PullHereAsync();

        Assert.True(r.Success);
        Assert.Equal([HandoffMessageTypes.Release], peer.Requests);
        Assert.Equal(["connect"], local.Calls);
    }

    [Fact]
    public async Task PullHereStillConnectsWhenPeerIsOffline()
    {
        var local = new FakeRoute();
        var c = new HandoffCoordinator(local, new FakePeer(throws: true), () => Addr) { Delay = (_, _) => Task.CompletedTask };
        var r = await c.PullHereAsync();
        Assert.True(r.Success);
        Assert.Contains("no se pudo contactar", r.Message);
    }

    [Fact]
    public async Task PushRollsBackWhenPeerFails()
    {
        var local = new FakeRoute { Connected = true };
        var c = new HandoffCoordinator(local, new FakePeer(ok: false), () => Addr) { Delay = (_, _) => Task.CompletedTask };
        var r = await c.PushToPeerAsync();
        Assert.False(r.Success);
        Assert.Equal(["disconnect", "connect"], local.Calls);
    }

    [Fact]
    public async Task HandlesReleaseRequestFromPeer()
    {
        var local = new FakeRoute { Connected = true };
        var c = new HandoffCoordinator(local, new FakePeer(ok: true), () => Addr);
        var (ok, _) = await c.HandleRequestAsync(HandoffMessage.Create(HandoffMessageTypes.Release, "Mac", Addr), default);
        Assert.True(ok);
        Assert.False(local.Connected);
    }

    [Fact]
    public async Task TcpClientAndServerTalk()
    {
        var local = new FakeRoute { Connected = true };
        var coordinator = new HandoffCoordinator(local, new FakePeer(ok: true), () => Addr);
        await using var server = new TcpHandoffServer(new HandoffSigner("pairing"), "Mac", 0, coordinator.HandleRequestAsync, IPAddress.Loopback);
        server.Start();

        var client = new TcpHandoffClient(new HandoffSigner("pairing"), "PC", "127.0.0.1", server.Port, () => Addr);
        var reply = await client.RequestAsync(HandoffMessageTypes.Release, default);

        Assert.True(reply.Ok);
        Assert.Equal("Mac", reply.From);
        Assert.False(local.Connected);

        var badClient = new TcpHandoffClient(new HandoffSigner("wrong"), "PC", "127.0.0.1", server.Port, () => Addr);
        await Assert.ThrowsAsync<IOException>(() => badClient.RequestAsync(HandoffMessageTypes.Ping, default));
    }

    private sealed class FakeRoute : IAudioRouteController
    {
        public bool Connected { get; set; }
        public List<string> Calls { get; } = [];
        public Task<bool> ConnectAsync(string a, CancellationToken ct) { Calls.Add("connect"); Connected = true; return Task.FromResult(true); }
        public Task<bool> DisconnectAsync(string a, CancellationToken ct) { Calls.Add("disconnect"); Connected = false; return Task.FromResult(true); }
        public Task<bool> IsConnectedAsync(string a, CancellationToken ct) => Task.FromResult(Connected);
    }

    private sealed class FakePeer(bool ok = true, bool throws = false) : IHandoffClient
    {
        public List<string> Requests { get; } = [];
        public Task<HandoffMessage> RequestAsync(string type, CancellationToken ct)
        {
            Requests.Add(type);
            if (throws) throw new IOException("connection refused");
            return Task.FromResult(HandoffMessage.Create(type, "PC", Addr).CreateReply("Mac", ok, ok ? null : "busy"));
        }
    }
}
