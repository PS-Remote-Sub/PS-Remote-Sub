using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Ocr;

/// <summary>Vision (VNRecognizeTextRequest) implementation of <see cref="IOcrEngine"/>.</summary>
public sealed class VisionOcrEngine : IOcrEngine
{
    private sealed record Box(string Text, int X, int Y, int W, int H);

    private sealed record Line(string Text, int X, int Y, int W, int H, List<Box>? Words);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<string, bool> _available = new(StringComparer.OrdinalIgnoreCase);

    public int MaxImageDimension => 8192;

    public bool IsAvailable(string ocrTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(ocrTag);
        return _available.GetOrAdd(ocrTag, static tag => Native.psrs_ocr_supported(VisionLanguages.ToVision(tag)) == 1);
    }

    public void Invalidate(string ocrTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(ocrTag);
        _available.TryRemove(ocrTag, out _);
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, string ocrTag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsAvailable(ocrTag))
            throw new InvalidOperationException($"OCR language '{ocrTag}' is not available.");

        ct.ThrowIfCancellationRequested();
        string json = await Task.Run(() => RunNative(image, VisionLanguages.ToVision(ocrTag)), CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var parsed = JsonSerializer.Deserialize<List<Line>>(json, Json) ?? [];
        return parsed.Select(l => new OcrLine(
            l.Text,
            new PixelRect(l.X, l.Y, l.W, l.H),
            l.Words?.Select(w => new OcrWord(w.Text, new PixelRect(w.X, w.Y, w.W, w.H))).ToList())).ToList();
    }

    private static unsafe string RunNative(Frame image, string lang)
    {
        int rc;
        nint json;
        fixed (byte* p = image.Bgra)
            rc = Native.psrs_ocr_recognize(p, image.Width, image.Height, lang, out json);
        if (rc != 0)
            throw new InvalidOperationException($"Vision OCR failed (code {rc}).");
        try { return Marshal.PtrToStringUTF8(json) ?? "[]"; }
        finally { Native.psrs_free(json); }
    }
}
