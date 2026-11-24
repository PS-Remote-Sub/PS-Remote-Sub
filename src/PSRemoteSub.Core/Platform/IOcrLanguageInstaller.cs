using PSRemoteSub.Core.Languages;

namespace PSRemoteSub.Core.Platform;

public interface IOcrLanguageInstaller
{
    Task<bool> InstallAsync(SourceLanguage language, CancellationToken ct);
}
