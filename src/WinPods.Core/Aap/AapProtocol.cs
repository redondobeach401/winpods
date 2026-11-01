namespace WinPods.Core.Aap;

/// <summary>
/// Apple Accessory Protocol (AAP) as spoken by AirPods over Bluetooth Classic L2CAP PSM 0x1001.
/// This is the channel iOS/macOS use to change noise control, conversational awareness, etc.
/// Packet formats come from public reverse-engineering work (LibrePods / OpenPods / MagicPods).
/// </summary>
public static class AapProtocol
{
    public const ushort L2capPsm = 0x1001;

    private static readonly byte[] Header = [0x04, 0x00, 0x04, 0x00];

    public static class Opcodes
    {
        public const ushort Battery = 0x0004;
        public const ushort EarDetection = 0x0006;
        public const ushort ControlCommand = 0x0009;
        public const ushort RequestNotifications = 0x000F;
        public const ushort ConversationLevel = 0x004B;
        public const ushort SetFeatureFlags = 0x004D;
    }

    /// <summary>First packet after the L2CAP channel opens.</summary>
    public static byte[] Handshake() =>
        [0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Unlocks features such as conversational awareness and adaptive audio on H2 AirPods.</summary>
    public static byte[] SetFeatureFlags() =>
        [.. Header, 0x4D, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Ask the AirPods to push battery, ear detection and setting notifications.</summary>
    public static byte[] RequestNotifications() =>
        [.. Header, 0x0F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];

    public static byte[] ControlCommand(AapSetting setting, byte value) =>
        [.. Header, 0x09, 0x00, (byte)setting, value, 0x00, 0x00, 0x00];

    public static byte[] SetNoiseControl(NoiseControlMode mode) => ControlCommand(AapSetting.ListeningMode, (byte)mode);

    public static byte[] SetConversationalAwareness(bool enabled) =>
        ControlCommand(AapSetting.ConversationalAwareness, Bool(enabled));

    public static byte[] SetAdaptiveVolume(bool enabled) => ControlCommand(AapSetting.AdaptiveVolume, Bool(enabled));

    public static byte[] SetOneBudAnc(bool enabled) => ControlCommand(AapSetting.OneBudAncMode, Bool(enabled));

    /// <summary>Adaptive audio: 0 = let in more noise … 100 = reduce more noise.</summary>
    public static byte[] SetAdaptiveAudioNoise(int level) =>
        ControlCommand(AapSetting.AdaptiveAudioNoise, (byte)Math.Clamp(level, 0, 100));

    public static byte[] SetListeningModeCycle(ListeningModeCycle modes) =>
        ControlCommand(AapSetting.ListeningModeConfigs, (byte)modes);

    /// <summary>AAP encodes booleans as 0x01 = on, 0x02 = off.</summary>
    public static byte Bool(bool value) => value ? (byte)0x01 : (byte)0x02;

    public static bool? ToBool(byte value) => value switch { 0x01 => true, 0x02 => false, _ => null };

    /// <summary>Parses one packet received from the AirPods. Returns null for non-data packets.</summary>
    public static AapEvent? Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6 || !packet[..4].SequenceEqual(Header))
            return null;

        ushort opcode = (ushort)(packet[4] | (packet[5] << 8));
        var body = packet[6..];

        switch (opcode)
        {
            case Opcodes.Battery when body.Length >= 1:
            {
                int count = body[0];
                var readings = new List<BatteryReading>(count);
                for (int i = 0; i < count; i++)
                {
                    int off = 1 + i * 5;
                    if (off + 5 > body.Length) break;
                    readings.Add(new BatteryReading(
                        (BatteryComponent)body[off], body[off + 2], (BatteryChargeState)body[off + 3]));
                }
                return new BatteryEvent(readings);
            }
            case Opcodes.EarDetection when body.Length >= 2:
                return new EarDetectionEvent((EarState)body[0], (EarState)body[1]);
            case Opcodes.ControlCommand when body.Length >= 2:
                return new SettingEvent((AapSetting)body[0], body[1]);
            case Opcodes.ConversationLevel when body.Length >= 4:
                return new ConversationLevelEvent(body[3]);
            default:
                return new UnknownEvent(opcode, packet.ToArray());
        }
    }
}
