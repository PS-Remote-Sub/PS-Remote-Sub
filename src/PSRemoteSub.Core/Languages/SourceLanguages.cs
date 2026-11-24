namespace PSRemoteSub.Core.Languages;

public static class SourceLanguages
{
    public static IReadOnlyList<SourceLanguage> All { get; } =
    [
        new("en", "en-US", "อังกฤษ", "Language.OCR~~~en-US~0.0.1.0"),
        new("ja", "ja-JP", "ญี่ปุ่น", "Language.OCR~~~ja-JP~0.0.1.0"),
        new("zh", "zh-Hans-CN", "จีน", "Language.OCR~~~zh-CN~0.0.1.0"),
        new("ko", "ko-KR", "เกาหลี", "Language.OCR~~~ko-KR~0.0.1.0"),
    ];

    public static SourceLanguage Get(string code) =>
        All.FirstOrDefault(l => l.Code == code)
        ?? throw new ArgumentException($"Unknown source language '{code}'.", nameof(code));

    /// <summary>Google Translate's code for an app language code: only Chinese differs (<c>zh-CN</c>).</summary>
    public static string GoogleCode(string code) => code == "zh" ? "zh-CN" : code;
}
