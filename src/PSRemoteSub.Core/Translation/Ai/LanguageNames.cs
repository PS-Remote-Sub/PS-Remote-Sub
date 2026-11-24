using PSRemoteSub.Core.Languages;

namespace PSRemoteSub.Core.Translation.Ai;

/// <summary>English language names used in the AI prompt.</summary>
public static class LanguageNames
{
    public static string ForSource(SourceLanguage s) => s.Code switch
    {
        "en" => "English",
        "ja" => "Japanese",
        "zh" => "Simplified Chinese",
        "ko" => "Korean",
        _ => s.Code,
    };

    public static string ForTarget(string code) => code switch
    {
        "th" => "Thai",
        "en" => "English",
        "ja" => "Japanese",
        "zh-CN" => "Simplified Chinese",
        "ko" => "Korean",
        _ => code,
    };
}
