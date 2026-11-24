using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.Core.Tests.Capture;

public class CaptureDeviceRulesTests
{
    private const string CardPort1 = @"\\?\USB#VID_345F&PID_2130&MI_00#7&3b793843&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";
    private const string CardPort2 = @"\\?\USB#VID_345F&PID_2130&MI_00#7&1f00aa11&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";
    private const string Webcam = @"\\?\USB#VID_04F2&PID_B6DD&MI_00#6&2c1e3b0&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";

    private static readonly Guid CardContainer = new("74e6f082-2d9d-51e8-80ea-6d379b47600e");

    // ---- PairAudio ----

    [Fact]
    public void PairAudio_SameContainer_PicksThatInput() =>
        Assert.Equal("card-mic", CaptureDeviceRules.PairAudio(CardContainer,
            [("other", Guid.NewGuid()), ("card-mic", CardContainer)]));

    [Fact]
    public void PairAudio_SystemContainer_NeverPairs() =>
        // Every built-in device shares this container: the laptop camera must not pair with the laptop microphone.
        Assert.Null(CaptureDeviceRules.PairAudio(CaptureDeviceRules.SystemContainer,
            [("built-in-mic", CaptureDeviceRules.SystemContainer)]));

    [Fact]
    public void PairAudio_EmptyContainer_NeverPairs() =>
        Assert.Null(CaptureDeviceRules.PairAudio(Guid.Empty, [("mic", Guid.Empty)]));

    [Fact]
    public void PairAudio_NoContainer_NeverPairs() =>
        Assert.Null(CaptureDeviceRules.PairAudio(null, [("mic", null), ("mic2", CardContainer)]));

    [Fact]
    public void PairAudio_NoMatch_Null() =>
        Assert.Null(CaptureDeviceRules.PairAudio(CardContainer, [("mic", Guid.NewGuid())]));

    // ---- ResolveDevice ----

    [Fact]
    public void ResolveDevice_SavedPresent_PicksIt()
    {
        var a = new CaptureDevice("a", "A", "a-mic");
        var b = new CaptureDevice("b", "B", null);
        Assert.Same(b, CaptureDeviceRules.ResolveDevice([a, b], "b"));
    }

    [Fact]
    public void ResolveDevice_SavedPresent_IgnoresCase()
    {
        var card = new CaptureDevice(CardPort1, "USB3 Video", "mic");
        Assert.Same(card, CaptureDeviceRules.ResolveDevice([card], CardPort1.ToUpperInvariant()));
    }

    [Fact]
    public void ResolveDevice_SavedMissing_DoesNotSubstituteAnotherDevice()
    {
        var webcam = new CaptureDevice(Webcam, "Webcam", "webcam-mic");
        var other = new CaptureDevice("b", "B", "b-mic");
        Assert.Null(CaptureDeviceRules.ResolveDevice([webcam, other], CardPort1));
    }

    [Fact]
    public void ResolveDevice_SavedMovedToAnotherUsbPort_PicksSameModel()
    {
        // The instance part of a USB id changes with the port; the vendor, product and interface do not.
        var webcam = new CaptureDevice(Webcam, "Webcam", "webcam-mic");
        var card = new CaptureDevice(CardPort2, "USB3 Video", "mic");
        Assert.Same(card, CaptureDeviceRules.ResolveDevice([webcam, card], CardPort1));
    }

    [Fact]
    public void ResolveDevice_SavedInterfaceMovedToAnotherUsbPort_PicksSameInterface()
    {
        // A card with two video interfaces on the new port: the saved MI_02 must not fall back to MI_00 (the first
        // VID&PID match) while MI_02 itself is there.
        const string savedMi02 = @"\\?\USB#VID_345F&PID_2130&MI_02#7&3b793843&0&0002#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";
        var mi00 = new CaptureDevice(CardPort2, "USB3 Video", "mic");
        var mi02 = new CaptureDevice(
            @"\\?\USB#VID_345F&PID_2130&MI_02#7&1f00aa11&0&0002#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL", "USB3 Video 2", "mic");
        Assert.Same(mi02, CaptureDeviceRules.ResolveDevice([mi00, mi02], savedMi02));
        Assert.Same(mi00, CaptureDeviceRules.ResolveDevice([mi02, mi00], CardPort1));
    }

    [Fact]
    public void ResolveDevice_NoSaved_PrefersExternalWithAudio()
    {
        var builtIn = new CaptureDevice("cam", "Integrated Camera", null, IsBuiltIn: true);
        var silent = new CaptureDevice("silent", "Silent", null);
        var card = new CaptureDevice(CardPort1, "USB3 Video", "mic");
        Assert.Same(card, CaptureDeviceRules.ResolveDevice([builtIn, silent, card], null));
        Assert.Same(card, CaptureDeviceRules.ResolveDevice([builtIn, silent, card], ""));
    }

    [Fact]
    public void ResolveDevice_NoSaved_BuiltInWithAudio_NotPreferred()
    {
        var builtIn = new CaptureDevice("cam", "Integrated Camera", "mic", IsBuiltIn: true);
        var silent = new CaptureDevice("silent", "Silent", null);
        Assert.Same(silent, CaptureDeviceRules.ResolveDevice([builtIn, silent], null));
    }

    [Fact]
    public void ResolveDevice_NoSaved_OnlyBuiltIn_Null()
    {
        var builtIn = new CaptureDevice("cam", "Integrated Camera", "mic", IsBuiltIn: true);
        Assert.Null(CaptureDeviceRules.ResolveDevice([builtIn], null));
    }

    [Fact]
    public void ResolveDevice_None_Null()
    {
        Assert.Null(CaptureDeviceRules.ResolveDevice([], "a"));
        Assert.Null(CaptureDeviceRules.ResolveDevice([], null));
    }

    // ---- ModelKey ----

    [Fact]
    public void ModelKey_UsbId_VendorProductInterface() =>
        Assert.Equal("VID_345F&PID_2130&MI_00", CaptureDeviceRules.ModelKey(CardPort1.ToLowerInvariant()));

    [Fact]
    public void ModelKey_NotUsb_Null() =>
        Assert.Null(CaptureDeviceRules.ModelKey(@"\\?\SWD#VCAMDEVAPI#abc"));

    [Fact]
    public void ModelKey_MacUniqueId_SameCardOnAnotherPort_SameKey()
    {
        Assert.Equal("VID_345F&PID_2130", CaptureDeviceRules.ModelKey("0x1100000345f2130"));
        Assert.Equal(CaptureDeviceRules.ModelKey("0x1100000345f2130"), CaptureDeviceRules.ModelKey("0x14200000345F2130"));
    }

    [Fact]
    public void ResolveDevice_MacCardOnAnotherPort_IsRecovered()
    {
        var moved = new CaptureDevice("0x14200000345f2130", "USB3 Video", null);
        var other = new CaptureDevice("0x1100000046d0825", "Webcam", null);
        Assert.Same(moved, CaptureDeviceRules.ResolveDevice([other, moved], "0x1100000345f2130"));
    }

    [Fact]
    public void ModelKey_MacUuid_Null() =>
        Assert.Null(CaptureDeviceRules.ModelKey("1FD4B3A2-236E-492B-8CE5-255DD288CE50"));

    // ---- A card saved on another OS (settings.json copied between Windows and Mac) ----

    [Fact]
    public void ResolveDevice_WindowsIdSaved_SameCardOnMac_IsFound()
    {
        var webcam = new CaptureDevice("0x1100000046d0825", "Webcam", null);
        var card = new CaptureDevice("0x1100000345f2130", "USB3 Video", "mic");
        Assert.Same(card, CaptureDeviceRules.ResolveDevice([webcam, card], CardPort1));
    }

    [Fact]
    public void ResolveDevice_MacIdSaved_SameCardOnWindows_IsFound()
    {
        var webcam = new CaptureDevice(Webcam, "Webcam", "webcam-mic");
        var card = new CaptureDevice(CardPort2, "USB3 Video", "mic");
        Assert.Same(card, CaptureDeviceRules.ResolveDevice([webcam, card], "0x1100000345f2130"));
    }

    [Fact]
    public void ResolveDevice_WindowsIdSaved_OtherVidPidOnMac_Null()
    {
        var webcam = new CaptureDevice("0x1100000046d0825", "Webcam", null);
        var otherCard = new CaptureDevice("0x1100000345f2131", "Other Video", "mic");
        Assert.Null(CaptureDeviceRules.ResolveDevice([webcam, otherCard], CardPort1));
    }
}
