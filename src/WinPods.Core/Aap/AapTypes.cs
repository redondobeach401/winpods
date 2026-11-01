namespace WinPods.Core.Aap;

/// <summary>Listening (noise-control) mode, value of control setting <see cref="AapSetting.ListeningMode"/>.</summary>
public enum NoiseControlMode : byte
{
    Off = 0x01,
    NoiseCancellation = 0x02,
    Transparency = 0x03,
    Adaptive = 0x04,
}

/// <summary>Identifiers of "control command" (opcode 0x09) settings.</summary>
public enum AapSetting : byte
{
    ListeningMode = 0x0D,
    ListeningModeConfigs = 0x1A,
    OneBudAncMode = 0x1B,
    VolumeSwipeMode = 0x25,
    AdaptiveVolume = 0x26,
    ConversationalAwareness = 0x28,
    AdaptiveAudioNoise = 0x2E,
}

/// <summary>Bit mask used by <see cref="AapSetting.ListeningModeConfigs"/> (modes cycled by press-and-hold).</summary>
[Flags]
public enum ListeningModeCycle : byte
{
    None = 0,
    Off = 0x01,
    NoiseCancellation = 0x02,
    Transparency = 0x04,
    Adaptive = 0x08,
}

public enum BatteryComponent : byte
{
    Single = 0x01,
    Right = 0x02,
    Left = 0x04,
    Case = 0x08,
}

public enum BatteryChargeState : byte
{
    Unknown = 0x00,
    Charging = 0x01,
    Discharging = 0x02,
    Disconnected = 0x04,
}

public enum EarState : byte
{
    InEar = 0x00,
    OutOfEar = 0x01,
    InCase = 0x02,
}

public readonly record struct BatteryReading(BatteryComponent Component, int Percent, BatteryChargeState State);

public abstract record AapEvent;
public sealed record BatteryEvent(IReadOnlyList<BatteryReading> Readings) : AapEvent;
public sealed record EarDetectionEvent(EarState Primary, EarState Secondary) : AapEvent;
public sealed record SettingEvent(AapSetting Setting, byte Value) : AapEvent;
/// <summary>Conversational awareness volume level reported while the user speaks (1..9 = speaking, 8/9 = stop).</summary>
public sealed record ConversationLevelEvent(byte Level) : AapEvent;
public sealed record UnknownEvent(ushort Opcode, byte[] Packet) : AapEvent;
