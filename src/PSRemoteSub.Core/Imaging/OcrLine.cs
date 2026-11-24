namespace PSRemoteSub.Core.Imaging;

/// <summary>One recognized word and where it is, in OCR-image pixels.</summary>
public sealed record OcrWord(string Text, PixelRect Bounds);

/// <param name="Words">The line's words, when the engine reports them (used to find a speaker label's box).</param>
public sealed record OcrLine(string Text, PixelRect Bounds, IReadOnlyList<OcrWord>? Words = null);
