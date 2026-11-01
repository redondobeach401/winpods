using WinPods.Core;
using WinPods.Core.Ble;

namespace WinPods.Core.Tests;

public class ProximityPairingTests
{
    private static byte[] Advert(byte status, byte battery, byte charge, ushort product = 0x2014) =>
    [
        0x07, 0x19, 0x01, (byte)(product & 0xFF), (byte)(product >> 8), status, battery, charge, 0x31, 0x00, 0x05,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    [Fact]
    public void ParsesAdvertBroadcastFromLeftPod()
    {
        // status: b5 left broadcasting, b1 this pod in ear, b3 other pod in ear
        Assert.True(ProximityPairingMessage.TryParse(Advert(0x2A, 0x98, 0x5A), out var m));
        Assert.Equal(AirPodsModel.AirPodsPro2, m!.Model);
        Assert.True(m.BroadcastFromLeft);
        Assert.Equal(new PodStatus(80, true, true), m.Left);
        Assert.Equal(new PodStatus(90, false, true), m.Right);
        Assert.Equal(100, m.CaseBatteryPercent);
        Assert.True(m.CaseCharging);
    }

    [Fact]
    public void FlipsSidesWhenBroadcastFromRightPod()
    {
        Assert.True(ProximityPairingMessage.TryParse(Advert(0x02, 0x98, 0x1F, 0x2024), out var m));
        Assert.Equal(AirPodsModel.AirPodsPro2UsbC, m!.Model);
        Assert.False(m.BroadcastFromLeft);
        Assert.Equal(new PodStatus(80, true, true), m.Right);
        Assert.Equal(new PodStatus(90, false, false), m.Left);
        Assert.Null(m.CaseBatteryPercent); // 0xF = unknown (case closed / not reported)
        Assert.False(m.CaseCharging);
    }

    [Theory]
    [InlineData(new byte[] { 0x10, 0x05, 0x01 })]           // other Apple message type
    [InlineData(new byte[] { 0x07, 0x19, 0x01, 0x14, 0x20 })] // truncated
    public void RejectsOtherPayloads(byte[] data) =>
        Assert.False(ProximityPairingMessage.TryParse(data, out _));

    [Fact]
    public void UnknownModelStillParses()
    {
        Assert.True(ProximityPairingMessage.TryParse(Advert(0x20, 0x55, 0x05, 0x2999), out var m));
        Assert.Equal(AirPodsModel.Unknown, m!.Model);
        Assert.Equal((ushort)0x2999, m.ProductId);
        Assert.Equal(50, m.Left.BatteryPercent);
    }

    [Fact]
    public void BluetoothAddressRoundTrips()
    {
        Assert.True(BluetoothAddress.TryParse("a4-c6-f0-12-34-56", out var a));
        Assert.Equal(0xA4C6F0123456UL, a);
        Assert.Equal("A4:C6:F0:12:34:56", BluetoothAddress.Format(a));
        Assert.Null(BluetoothAddress.Normalize("nope"));
    }
}
