namespace PSRemoteSub.MacOS.Tests;

/// <summary>A <see cref="FactAttribute"/> that skips itself when not running on macOS, so the suite passes on Windows.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS only";
    }
}
