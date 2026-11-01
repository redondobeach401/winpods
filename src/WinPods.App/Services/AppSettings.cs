using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPods.Core.Handoff;

namespace WinPods.App.Services;

/// <summary>User settings, stored in %AppData%\WinPods\settings.json. The pairing secret is DPAPI-encrypted.</summary>
public sealed class AppSettings
{
    /// <summary>Classic Bluetooth address of the AirPods ("AA:BB:CC:DD:EE:FF").</summary>
    public string? DeviceAddress { get; set; }

    /// <summary>Host name or IP of the Mac running WinPodsAgent (e.g. "MacBook-de-Ana.local").</summary>
    public string? PeerHost { get; set; }

    public int PeerPort { get; set; } = TcpHandoffClient.DefaultPort;
    public int ListenPort { get; set; } = TcpHandoffClient.DefaultPort;
    public bool HandoffEnabled { get; set; }
    public string ComputerName { get; set; } = Environment.MachineName;

    /// <summary>Minimum RSSI (dBm) for BLE battery adverts; filters out other people's AirPods.</summary>
    public int MinimumRssi { get; set; } = -65;

    [JsonPropertyName("sharedSecretProtected")]
    public string? SharedSecretProtected { get; set; }

    [JsonIgnore]
    public string? SharedSecret
    {
        get
        {
            if (string.IsNullOrEmpty(SharedSecretProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(SharedSecretProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
        }
        set => SharedSecretProtected = string.IsNullOrEmpty(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinPods");
    private static string FilePath => Path.Combine(Directory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>A random, human-typeable pairing secret (e.g. "k7qm-2xwd-9fha-c3tp").</summary>
    public static string GenerateSecret()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var chars = Enumerable.Range(0, 16).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray();
        return string.Join('-', Enumerable.Range(0, 4).Select(i => new string(chars, i * 4, 4)));
    }
}
