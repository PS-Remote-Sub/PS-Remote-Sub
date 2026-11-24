using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.Windows.Capture;

/// <summary>Maps the HRESULT of a failed capture-card open or read to the state shown to the user.</summary>
/// <remarks>Values checked against <c>mferror.h</c> / <c>winerror.h</c> (Windows SDK 10.0.26100.0).</remarks>
public static class CaptureErrors
{
    private const int EAccessDenied = unchecked((int)0x80070005);                       // privacy setting blocks the camera
    private const int MfEHwMftFailedStartStreaming = unchecked((int)0xC00D3704);       // device in use by another app
    private const int MfEVideoRecordingDevicePreempted = unchecked((int)0xC00D3EA3);   // taken by another app
    private const int MfEVideoRecordingDeviceInvalidated = unchecked((int)0xC00D3EA2); // no longer present
    private const int HResultErrorDeviceNotConnected = unchecked((int)0x8007048F);     // HRESULT_FROM_WIN32(1167)

    public static CaptureState Classify(int hresult) => hresult switch
    {
        EAccessDenied => CaptureState.Blocked,
        MfEHwMftFailedStartStreaming or MfEVideoRecordingDevicePreempted => CaptureState.InUse,
        MfEVideoRecordingDeviceInvalidated or HResultErrorDeviceNotConnected => CaptureState.Unplugged,
        _ => CaptureState.Failed,
    };
}
