using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Ocr;

/// <summary>Vision ships its languages with macOS, so there is nothing to install.</summary>
public sealed class BuiltInOcrLanguages : IOcrLanguageInstaller
{
    public Task<bool> InstallAsync(SourceLanguage language, CancellationToken ct) => Task.FromResult(true);
}
