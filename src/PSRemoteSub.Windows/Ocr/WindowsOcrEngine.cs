using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using CoreOcrLine = PSRemoteSub.Core.Imaging.OcrLine;
using OcrWord = PSRemoteSub.Core.Imaging.OcrWord;

namespace PSRemoteSub.Windows.Ocr;

/// <summary>Windows.Media.Ocr implementation of <see cref="IOcrEngine"/>. Engines are cached per language tag.</summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly ConcurrentDictionary<string, OcrEngine?> _engines = new(StringComparer.OrdinalIgnoreCase);

    public int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    public bool IsAvailable(string ocrTag) => GetEngine(ocrTag) is not null;

    /// <summary>
    /// Forgets the cached engine (or cached "not installed") for <paramref name="ocrTag"/>, so the next call looks
    /// again. Call after installing an OCR language at runtime. Thread-safe.
    /// </summary>
    public void Invalidate(string ocrTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(ocrTag);
        _engines.TryRemove(ocrTag, out _);
    }

    public async Task<IReadOnlyList<CoreOcrLine>> RecognizeAsync(Frame image, string ocrTag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        OcrEngine engine = GetEngine(ocrTag)
            ?? throw new InvalidOperationException($"OCR language '{ocrTag}' is not available.");

        ct.ThrowIfCancellationRequested();
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            image.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
        OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);

        var lines = new List<CoreOcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            var words = new List<OcrWord>(line.Words.Count);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                left = Math.Min(left, r.Left);
                top = Math.Min(top, r.Top);
                right = Math.Max(right, r.Right);
                bottom = Math.Max(bottom, r.Bottom);
                words.Add(new OcrWord(word.Text, new PixelRect((int)Math.Floor(r.Left), (int)Math.Floor(r.Top),
                    (int)Math.Ceiling(r.Right - Math.Floor(r.Left)), (int)Math.Ceiling(r.Bottom - Math.Floor(r.Top)))));
            }
            var bounds = line.Words.Count == 0
                ? default
                : new PixelRect((int)Math.Floor(left), (int)Math.Floor(top),
                    (int)Math.Ceiling(right - Math.Floor(left)), (int)Math.Ceiling(bottom - Math.Floor(top)));
            lines.Add(new CoreOcrLine(line.Text, bounds, words));
        }
        return lines;
    }

    private OcrEngine? GetEngine(string ocrTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(ocrTag);
        return _engines.GetOrAdd(ocrTag, static tag =>
        {
            try { return OcrEngine.TryCreateFromLanguage(new Language(tag)); }
            catch (ArgumentException) { return null; }
        });
    }
}
