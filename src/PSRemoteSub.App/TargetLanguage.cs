using System.Text.RegularExpressions;

namespace PSRemoteSub.App;

/// <summary>What <see cref="TargetLanguage.TryApply"/> did.</summary>
internal enum TargetLanguageResult
{
    /// <summary>Not a language code: nothing was written.</summary>
    Invalid,

    /// <summary>Valid and equal to the saved one: nothing was written.</summary>
    Unchanged,

    /// <summary>Written to <c>Settings.TargetLang</c> and applied; the caller saves.</summary>
    Applied,
}

/// <summary>
/// The "แปลเป็นภาษา" code shared by the Translation page and the setup wizard: one validation, one write path.
/// UI thread only.
/// </summary>
internal static class TargetLanguage
{
    private static readonly Regex LanguageCode = new("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,4})?$", RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="code"/> (already trimmed) looks like a language code (th, en, zh-CN …).</summary>
    public static bool IsValid(string code) => LanguageCode.IsMatch(code);

    /// <summary>
    /// Trims <paramref name="text"/>; when it is a valid code different from <c>Settings.TargetLang</c>, writes it and
    /// calls <see cref="AppHost.ApplySettings"/>. Does not save (the caller saves, debounced or not).
    /// </summary>
    public static TargetLanguageResult TryApply(AppHost host, string? text)
    {
        string code = (text ?? "").Trim();
        if (!IsValid(code))
            return TargetLanguageResult.Invalid;
        if (code == host.Settings.TargetLang)
            return TargetLanguageResult.Unchanged;
        host.Settings.TargetLang = code;
        host.ApplySettings();
        return TargetLanguageResult.Applied;
    }
}
