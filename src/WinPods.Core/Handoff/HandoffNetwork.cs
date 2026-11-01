using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinPods.Core.Handoff;

/// <summary>Sends one request to a peer and waits for its reply.</summary>
public interface IHandoffClient
{
    Task<HandoffMessage> RequestAsync(string type, CancellationToken cancellationToken);
}

/// <summary>TCP transport: one newline-terminated JSON request, one JSON reply, connection closed.</summary>
public sealed class TcpHandoffClient : IHandoffClient
{
    public const int DefaultPort = 47821;

    private readonly HandoffSigner _signer;
    private readonly string _localName;
    private readonly Func<string?> _address;

    public TcpHandoffClient(HandoffSigner signer, string localName, string host, int port, Func<string?> address)
    {
        _signer = signer;
        _localName = localName;
        Host = host;
        Port = port;
        _address = address;
    }

    public string Host { get; }
    public int Port { get; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<HandoffMessage> RequestAsync(string type, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        var request = _signer.Sign(HandoffMessage.Create(type, _localName, _address()));
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(Host, Port, cts.Token).ConfigureAwait(false);
        await using var stream = tcp.GetStream();
        await HandoffWire.WriteLineAsync(stream, request.ToJsonLine(), cts.Token).ConfigureAwait(false);

        string? line = await HandoffWire.ReadLineAsync(stream, cts.Token).ConfigureAwait(false)
            ?? throw new IOException("Peer closed the connection without replying.");
        var reply = HandoffMessage.FromJsonLine(line) ?? throw new InvalidDataException("Malformed reply from peer.");
        if (!_signer.Verify(reply, out var error))
            throw new InvalidDataException($"Rejected reply from peer: {error}.");
        if (reply.Type != HandoffMessageTypes.Reply || reply.ReplyTo != request.Nonce)
            throw new InvalidDataException("Peer reply does not match the request.");
        return reply;
    }
}

/// <summary>Accepts requests from peers and answers them through a handler.</summary>
public sealed class TcpHandoffServer : IAsyncDisposable
{
    private readonly HandoffSigner _signer;
    private readonly string _localName;
    private readonly Func<HandoffMessage, CancellationToken, Task<(bool Ok, string? Detail)>> _handler;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public TcpHandoffServer(HandoffSigner signer, string localName, int port,
        Func<HandoffMessage, CancellationToken, Task<(bool Ok, string? Detail)>> handler, IPAddress? bindAddress = null)
    {
        _signer = signer;
        _localName = localName;
        _handler = handler;
        _listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Raised for rejected/failed requests (diagnostics).</summary>
    public event Action<string>? Log;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { Log?.Invoke($"accept failed: {ex.Message}"); continue; }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var stream = client.GetStream();
            string? line = await HandoffWire.ReadLineAsync(stream, cts.Token).ConfigureAwait(false);
            if (line is null) return;
            var request = HandoffMessage.FromJsonLine(line);
            if (request is null) { Log?.Invoke("malformed request"); return; }
            if (!_signer.Verify(request, out var error)) { Log?.Invoke($"rejected request from {client.Client.RemoteEndPoint}: {error}"); return; }

            (bool ok, string? detail) = await _handler(request, cts.Token).ConfigureAwait(false);
            var reply = _signer.Sign(request.CreateReply(_localName, ok, detail));
            await HandoffWire.WriteLineAsync(stream, reply.ToJsonLine(), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
            Log?.Invoke($"request failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
        _cts.Dispose();
    }
}

internal static class HandoffWire
{
    private const int MaxLineBytes = 8 * 1024;

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(256);
        var one = new byte[1];
        while (buffer.Count < MaxLineBytes)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            buffer.Add(one[0]);
        }
        throw new InvalidDataException("Line too long.");
    }
}
