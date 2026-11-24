using PSRemoteSub.Windows.Capture;

namespace PSRemoteSub.Windows.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips at runtime when a <see cref="CaptureCardEngine"/> cannot be built here: no
/// Media Foundation, or no hardware Direct3D 11 device with video support (a hosted CI runner may have only the Basic
/// Render Driver). Checked once, in a static field. Where the engine builds, the test runs normally.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class VideoDeviceFactAttribute : FactAttribute
{
    private static readonly string? Unavailable = Probe();

    public VideoDeviceFactAttribute()
    {
        if (Unavailable is not null)
            Skip = $"No hardware video device for the capture engine: {Unavailable}";
    }

    private static string? Probe()
    {
        try
        {
            using var engine = new CaptureCardEngine();
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
