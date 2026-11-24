using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Platform;

public interface IOcrEngine
{
    int MaxImageDimension { get; }
    bool IsAvailable(string ocrTag);
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, string ocrTag, CancellationToken ct);

    /// <summary>Forgets a cached availability answer for <paramref name="ocrTag"/> (after a language install).</summary>
    void Invalidate(string ocrTag) { }
}
