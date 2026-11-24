using PSRemoteSub.Windows.Capture;

namespace PSRemoteSub.Windows.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips at runtime unless the test capture card (USB VID_345F, PID_2130)
/// is plugged in. The device list is read once, synchronously, in a static field.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CaptureCardFactAttribute : FactAttribute
{
    private static readonly bool CardPresent = DetectCard();

    public CaptureCardFactAttribute()
    {
        if (!CardPresent)
            Skip = "Test capture card (VID_345F&PID_2130) is not connected.";
    }

    private static bool DetectCard()
    {
        try
        {
            var devices = CaptureDeviceCatalog.ListAsync().GetAwaiter().GetResult();
            return devices.Any(d => d.Id.Contains("VID_345F&PID_2130", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
