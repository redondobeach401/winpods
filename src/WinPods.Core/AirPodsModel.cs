namespace WinPods.Core;

/// <summary>Apple audio product IDs as they appear (little-endian) in BLE proximity-pairing adverts.</summary>
public enum AirPodsModel : ushort
{
    Unknown = 0,
    AirPods1 = 0x2002,
    AirPods2 = 0x200F,
    AirPods3 = 0x2013,
    AirPods4 = 0x2019,
    AirPods4Anc = 0x201B,
    AirPodsPro = 0x200E,
    AirPodsPro2 = 0x2014,
    AirPodsPro2UsbC = 0x2024,
    AirPodsMax = 0x200A,
}

public static class AirPodsModelExtensions
{
    public static AirPodsModel FromProductId(ushort id) =>
        Enum.IsDefined(typeof(AirPodsModel), id) ? (AirPodsModel)id : AirPodsModel.Unknown;

    public static string DisplayName(this AirPodsModel model) => model switch
    {
        AirPodsModel.AirPods1 => "AirPods (1.ª gen.)",
        AirPodsModel.AirPods2 => "AirPods (2.ª gen.)",
        AirPodsModel.AirPods3 => "AirPods (3.ª gen.)",
        AirPodsModel.AirPods4 => "AirPods 4",
        AirPodsModel.AirPods4Anc => "AirPods 4 (ANC)",
        AirPodsModel.AirPodsPro => "AirPods Pro",
        AirPodsModel.AirPodsPro2 => "AirPods Pro 2",
        AirPodsModel.AirPodsPro2UsbC => "AirPods Pro 2 (USB-C)",
        AirPodsModel.AirPodsMax => "AirPods Max",
        _ => "AirPods",
    };

    /// <summary>Whether the model supports active noise cancellation / transparency.</summary>
    public static bool SupportsNoiseControl(this AirPodsModel model) => model is
        AirPodsModel.AirPodsPro or AirPodsModel.AirPodsPro2 or AirPodsModel.AirPodsPro2UsbC
        or AirPodsModel.AirPodsMax or AirPodsModel.AirPods4Anc;

    /// <summary>Adaptive audio, conversational awareness, etc. (H2 chip).</summary>
    public static bool SupportsAdaptiveAudio(this AirPodsModel model) => model is
        AirPodsModel.AirPodsPro2 or AirPodsModel.AirPodsPro2UsbC or AirPodsModel.AirPods4Anc;
}
