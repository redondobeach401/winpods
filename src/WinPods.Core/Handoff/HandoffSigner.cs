using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace WinPods.Core.Handoff;

/// <summary>
/// Signs and verifies handoff messages with HMAC-SHA256 over a key derived from the shared
/// pairing secret, rejecting stale (clock skew) and replayed messages.
/// </summary>
public sealed class HandoffSigner
{
    private readonly byte[] _key;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, long> _seenNonces = new();

    public HandoffSigner(string sharedSecret, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(sharedSecret))
            throw new ArgumentException("A shared secret is required.", nameof(sharedSecret));
        _key = DeriveKey(sharedSecret);
        _clock = clock ?? TimeProvider.System;
    }

    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>key = SHA-256("winpods-v1:" + secret)</summary>
    public static byte[] DeriveKey(string sharedSecret) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("winpods-v1:" + sharedSecret.Trim()));

    public HandoffMessage Sign(HandoffMessage message) =>
        message with { Signature = ComputeSignature(message) };

    public string ComputeSignature(HandoffMessage message) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(message.CanonicalString()))).ToLowerInvariant();

    public bool Verify(HandoffMessage message, out string? error)
    {
        error = null;
        if (message.Version != HandoffMessage.ProtocolVersion) { error = "unsupported protocol version"; return false; }
        if (string.IsNullOrEmpty(message.Signature)) { error = "missing signature"; return false; }

        byte[] expected = Convert.FromHexString(ComputeSignature(message));
        byte[] actual;
        try { actual = Convert.FromHexString(message.Signature); }
        catch (FormatException) { error = "malformed signature"; return false; }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) { error = "bad signature (different pairing secret?)"; return false; }

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - message.Timestamp) > MaxClockSkew.TotalSeconds) { error = "stale message (check both clocks)"; return false; }

        PruneNonces(now);
        if (!_seenNonces.TryAdd(message.Nonce, message.Timestamp)) { error = "replayed message"; return false; }
        return true;
    }

    private void PruneNonces(long now)
    {
        long cutoff = now - (long)MaxClockSkew.TotalSeconds * 2;
        foreach (var (nonce, ts) in _seenNonces)
            if (ts < cutoff) _seenNonces.TryRemove(nonce, out _);
    }
}
