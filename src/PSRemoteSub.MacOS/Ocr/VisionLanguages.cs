namespace PSRemoteSub.MacOS.Ocr;

internal static class VisionLanguages
{
    /// <summary>Maps a Core OCR tag (Windows-style BCP-47) to the identifier Vision uses.</summary>
    public static string ToVision(string ocrTag) => ocrTag switch
    {
        "zh-Hans-CN" => "zh-Hans",
        _ => ocrTag,
    };
}
