using PSRemoteSub.Core.Capture;
using PSRemoteSub.Windows.Capture;

namespace PSRemoteSub.Windows.Tests;

[Collection(CaptureCardCollection.Name)]
public class CaptureDeviceCatalogTests
{
    private const string CardId = "VID_345F&PID_2130";

    // The selection rules themselves are tested in PSRemoteSub.Core.Tests (CaptureDeviceRulesTests).

    [CaptureCardFact]
    public async Task List_FindsTestCardWithAudio()
    {
        var devices = await CaptureDeviceCatalog.ListAsync();
        var card = Assert.Single(devices, d => d.Name.Contains("USB3 Video", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(card.AudioDeviceId);
        Assert.False(card.IsBuiltIn);
        // The real id carries the port-independent model key used when the card moves to another USB port.
        Assert.Equal("VID_345F&PID_2130&MI_00", CaptureDeviceRules.ModelKey(card.Id));
    }

    [CaptureCardFact]
    public async Task ResolveDevice_NoSavedId_PicksTheTestCard()
    {
        var devices = await CaptureDeviceCatalog.ListAsync();
        var picked = CaptureDeviceRules.ResolveDevice(devices, null);
        Assert.NotNull(picked);
        Assert.NotNull(picked.AudioDeviceId);
        Assert.False(picked.IsBuiltIn);
    }

    [CaptureCardFact]
    public async Task ListModes_TestCard_HasProbedModes()
    {
        var devices = await CaptureDeviceCatalog.ListAsync();
        var card = devices.First(d => d.Id.Contains(CardId, StringComparison.OrdinalIgnoreCase));
        var modes = CaptureDeviceCatalog.ListModes(card.Id);

        Assert.Contains(modes, m => Has(m, 1920, 1080, 60, CaptureSubtype.Nv12));
        Assert.Contains(modes, m => Has(m, 2560, 1440, 50, CaptureSubtype.Mjpg));
        Assert.Contains(modes, m => Has(m, 2560, 1440, 50, CaptureSubtype.Yuy2));
    }

    private static bool Has(CaptureMode m, int w, int h, double fps, CaptureSubtype s) =>
        m.Width == w && m.Height == h && Math.Abs(m.Fps - fps) <= 0.5 && m.Subtype == s;
}
