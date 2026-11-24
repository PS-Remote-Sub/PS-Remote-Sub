using PSRemoteSub.Windows.Ocr;

namespace PSRemoteSub.Windows.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips at runtime when the Windows en-US OCR language pack is missing
/// (hosted CI runners are not guaranteed to have it). Where the pack exists the test runs normally.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OcrFactAttribute : FactAttribute
{
    private static readonly bool EnUsAvailable = new WindowsOcrEngine().IsAvailable("en-US");

    public OcrFactAttribute()
    {
        if (!EnUsAvailable)
            Skip = "Windows OCR language pack en-US is not installed.";
    }
}
