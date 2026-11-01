namespace WinPods.Core.Ble;

public sealed record PodStatus(int? BatteryPercent, bool Charging, bool InEar);

/// <summary>
/// Apple "proximity pairing" BLE advertisement (manufacturer 0x004C, type 0x07) broadcast by
/// AirPods. It carries coarse battery levels (10 % steps), charging and in-ear state and can be
/// read on any OS without a connection to the AirPods.
/// </summary>
/// <remarks>
/// Layout of the manufacturer data (company ID already stripped):
/// <code>
/// [0] 0x07 type        [1] length      [2] prefix (0x01 = paired)
/// [3..4] product ID LE [5] status      [6] battery: hi = other pod, lo = broadcasting pod
/// [7] hi = charging flags (b0 broadcasting pod, b1 other pod, b2 case), lo = case battery
/// [8] lid             [9] colour      [10] suffix  [11..] encrypted payload
/// </code>
/// Status byte: b1 broadcasting pod in ear, b2 both in case, b3 other pod in ear,
/// b5 broadcasting pod is the left one.
/// </remarks>
public sealed record ProximityPairingMessage(
    AirPodsModel Model,
    ushort ProductId,
    PodStatus Left,
    PodStatus Right,
    int? CaseBatteryPercent,
    bool CaseCharging,
    bool BothInCase,
    bool BroadcastFromLeft,
    byte Color)
{
    public const ushort AppleCompanyId = 0x004C;
    public const byte MessageType = 0x07;
    private const int MinimumLength = 11;

    public static bool TryParse(ReadOnlySpan<byte> data, out ProximityPairingMessage? message)
    {
        message = null;
        if (data.Length < MinimumLength || data[0] != MessageType || data[1] + 2 < MinimumLength)
            return false;

        ushort productId = (ushort)(data[3] | (data[4] << 8));
        byte status = data[5];
        bool fromLeft = (status & 0x20) != 0;

        int? thisBattery = DecodeBattery(data[6] & 0x0F);
        int? otherBattery = DecodeBattery(data[6] >> 4);
        int chargeFlags = data[7] >> 4;
        int? caseBattery = DecodeBattery(data[7] & 0x0F);

        var thisPod = new PodStatus(thisBattery, (chargeFlags & 0x1) != 0, (status & 0x02) != 0);
        var otherPod = new PodStatus(otherBattery, (chargeFlags & 0x2) != 0, (status & 0x08) != 0);

        message = new ProximityPairingMessage(
            AirPodsModelExtensions.FromProductId(productId),
            productId,
            Left: fromLeft ? thisPod : otherPod,
            Right: fromLeft ? otherPod : thisPod,
            CaseBatteryPercent: caseBattery,
            CaseCharging: (chargeFlags & 0x4) != 0,
            BothInCase: (status & 0x04) != 0,
            BroadcastFromLeft: fromLeft,
            Color: data[9]);
        return true;
    }

    /// <summary>Nibble 0..10 means 0..100 %, 15 means "not available".</summary>
    private static int? DecodeBattery(int nibble) => nibble <= 10 ? nibble * 10 : null;
}
