using PSRemoteSub.Core.Capture;
using PSRemoteSub.Windows.Capture;

namespace PSRemoteSub.Windows.Tests;

public class CaptureErrorsTests
{
    [Fact]
    public void Classify_MapsTable()
    {
        Assert.Equal(CaptureState.Blocked, CaptureErrors.Classify(unchecked((int)0x80070005)));   // E_ACCESSDENIED
        Assert.Equal(CaptureState.InUse, CaptureErrors.Classify(unchecked((int)0xC00D3704)));     // MF_E_HW_MFT_FAILED_START_STREAMING
        Assert.Equal(CaptureState.InUse, CaptureErrors.Classify(unchecked((int)0xC00D3EA3)));     // MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
        Assert.Equal(CaptureState.Unplugged, CaptureErrors.Classify(unchecked((int)0xC00D3EA2))); // MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED
        Assert.Equal(CaptureState.Unplugged, CaptureErrors.Classify(unchecked((int)0x8007048F))); // ERROR_DEVICE_NOT_CONNECTED
        Assert.Equal(CaptureState.Failed, CaptureErrors.Classify(unchecked((int)0x80004005)));    // E_FAIL
    }
}
