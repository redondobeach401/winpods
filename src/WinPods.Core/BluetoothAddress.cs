using System.Globalization;

namespace WinPods.Core;

/// <summary>Helpers for 48-bit Bluetooth device addresses.</summary>
public static class BluetoothAddress
{
    /// <summary>Formats as "AA:BB:CC:DD:EE:FF".</summary>
    public static string Format(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    /// <summary>Parses "AA:BB:CC:DD:EE:FF", "aa-bb-cc-dd-ee-ff" or "AABBCCDDEEFF".</summary>
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = new string(text.Where(c => c is not (':' or '-' or ' ')).ToArray());
        if (hex.Length != 12) return false;
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    /// <summary>Normalises any accepted spelling to "AA:BB:CC:DD:EE:FF"; returns null when invalid.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var a) ? Format(a) : null;
}
