using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

public class AvDevicePairingTests
{
    private static readonly AvDevicePairing.Audio CardAudio =
        new("AppleUSBAudioEngine:MACROSILICON:USB3 Digital Audio:2130:1", "USB3 Digital Audio", "USB3 Digital Audio:345F:2130");

    private static readonly AvDevicePairing.Audio Mic = new("BuiltInMicrophoneDevice", "MacBook Pro Microphone", "");

    private static readonly AvDevicePairing.Audio OtherUsb =
        new("AppleUSBAudioEngine:Logitech:Headset:1", "Headset", "Headset:046D:0A87");

    [Fact]
    public void PairAudio_UvcModelId_MatchesVendorProduct() =>
        Assert.Equal(CardAudio.Id, AvDevicePairing.PairAudio(
            new("0x1100000345f2130", "USB3 Video", "UVC Camera VendorID_13407 ProductID_8496"), [Mic, OtherUsb, CardAudio]));

    [Fact]
    public void PairAudio_VideoUniqueId_MatchesAudioUid() =>
        Assert.Equal("x:345f:2130", AvDevicePairing.PairAudio(
            new("0x14200000345F2130", "USB3 Video", ""), [Mic, OtherUsb, new("x:345f:2130", "n", "")]));

    [Fact]
    public void PairAudio_CaseInsensitiveHex() =>
        Assert.Equal("a:345f:2130:1", AvDevicePairing.PairAudio(
            new("0x1100000345F2130", "USB3 Video", ""), [new("a:345f:2130:1", "n", "")]));

    [Fact]
    public void PairAudio_DifferentDevice_Null() =>
        Assert.Null(AvDevicePairing.PairAudio(
            new("0x1100000345f2130", "USB3 Video", "UVC Camera VendorID_13407 ProductID_8496"), [Mic, OtherUsb]));

    [Fact]
    public void PairAudio_EqualModelId_FallsBack() =>
        Assert.Equal("a", AvDevicePairing.PairAudio(new("v", "Cam", "ModelX"), [new("a", "Other", "ModelX")]));

    [Fact]
    public void PairAudio_EqualName_FallsBack() =>
        Assert.Equal("a", AvDevicePairing.PairAudio(new("v", "Cam", ""), [new("a", "Cam", "")]));

    [Fact]
    public void PairAudio_EmptyNames_DoNotPair() =>
        Assert.Null(AvDevicePairing.PairAudio(new("v", "", ""), [new("a", "", "")]));

    [Fact]
    public void PairAudio_NoAudio_Null() =>
        Assert.Null(AvDevicePairing.PairAudio(new("v", "Cam", "M"), []));
}
