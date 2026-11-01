using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinPods.Core.Handoff;

/// <summary>Message types of the WinPods handoff protocol (see docs/handoff-protocol.md).</summary>
public static class HandoffMessageTypes
{
    /// <summary>"Are you there / do you have the AirPods?"</summary>
    public const string Ping = "ping";
    /// <summary>"Please disconnect the AirPods so I can take them."</summary>
    public const string Release = "release";
    /// <summary>"I released the AirPods, please connect them."</summary>
    public const string Acquire = "acquire";
    /// <summary>Reply to any request; <see cref="HandoffMessage.Ok"/> tells the outcome.</summary>
    public const string Reply = "reply";
}

/// <summary>A single JSON line exchanged between WinPods peers (Windows app / macOS agent).</summary>
public sealed record HandoffMessage
{
    public const int ProtocolVersion = 1;

    [JsonPropertyName("v")] public int Version { get; init; } = ProtocolVersion;
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("ts")] public long Timestamp { get; init; }
    [JsonPropertyName("nonce")] public required string Nonce { get; init; }
    [JsonPropertyName("addr")] public string? Address { get; init; }
    [JsonPropertyName("reply_to")] public string? ReplyTo { get; init; }
    [JsonPropertyName("ok")] public bool? Ok { get; init; }
    /// <summary>Free-form, unsigned, informational text.</summary>
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("sig")] public string? Signature { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HandoffMessage Create(string type, string from, string? address, TimeProvider? clock = null) => new()
    {
        Type = type,
        From = from,
        Timestamp = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds(),
        Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
        Address = address,
    };

    public HandoffMessage CreateReply(string from, bool ok, string? detail = null, TimeProvider? clock = null) => new()
    {
        Type = HandoffMessageTypes.Reply,
        From = from,
        Timestamp = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds(),
        Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
        Address = Address,
        ReplyTo = Nonce,
        Ok = ok,
        Detail = detail,
    };

    /// <summary>The exact string that is HMAC'd. Must stay byte-identical to the Swift agent.</summary>
    public string CanonicalString() => string.Join('\n',
        "v" + Version, Type, From, Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Nonce, Address ?? "", ReplyTo ?? "", Ok switch { true => "1", false => "0", null => "" });

    public string ToJsonLine() => JsonSerializer.Serialize(this, JsonOptions);

    public static HandoffMessage? FromJsonLine(string line)
    {
        try { return JsonSerializer.Deserialize<HandoffMessage>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
