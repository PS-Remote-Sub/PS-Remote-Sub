using System.Reflection;
using Avalonia.Threading;
using PSRemoteSub.Core.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace PSRemoteSub.App.Updates;

/// <summary>
/// Checks GitHub Releases through Velopack. Does nothing when no repository URL is configured or when the app
/// is not installed by Velopack (portable zip, dev build). Never throws.
/// </summary>
/// <param name="notify">Shows a toast. Called on the UI thread.</param>
/// <param name="exitApp">The app's normal exit path (disposes AppHost, then shuts the lifetime down). Called on the UI thread.</param>
internal sealed class UpdateChecker(Action<string> notify, Func<Task> exitApp, string? repositoryUrl = null)
{
    private readonly string _repositoryUrl = repositoryUrl ?? ReadRepositoryUrl();

    /// <summary>The <c>RepositoryUrl</c> assembly metadata written from the csproj property; empty when unset.</summary>
    internal static string ReadRepositoryUrl() =>
        typeof(UpdateChecker).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value?.Trim() ?? "";

    public async Task CheckAsync()
    {
        if (string.IsNullOrEmpty(_repositoryUrl))
            return;
        try
        {
            var manager = new UpdateManager(new GithubSource(_repositoryUrl, null, false));
            if (!manager.IsInstalled)
                return;
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return;

            string version = update.TargetFullRelease.Version.ToString();
            Log.Info($"Update available: {version}");
            string question = Loc.Format("UpdateAvailable", version);
            Dispatcher.UIThread.Post(() => notify(question));

            bool yes = await Dispatcher.UIThread.InvokeAsync(() => new UpdateConfirmWindow(question).AskAsync());
            if (!yes)
                return;

            Dispatcher.UIThread.Post(() => notify(Loc.Format("UpdateDownloading", version)));
            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);

            // Velopack's updater waits for this process to exit, applies the update, then restarts the app.
            // Exit through the app's normal path so AppHost (web server, hotkeys, keep-awake, capture) is disposed first.
            manager.WaitExitThenApplyUpdates(update, silent: false, restart: true);
            await Dispatcher.UIThread.InvokeAsync(exitApp);
        }
        catch (Exception ex)
        {
            // An update check must never disturb the app (offline, rate-limited, no release yet).
            Log.Warn("Update check failed", ex);
        }
    }
}
