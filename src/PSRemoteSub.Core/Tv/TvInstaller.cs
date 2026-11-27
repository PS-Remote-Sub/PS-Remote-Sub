using System.ComponentModel;

namespace PSRemoteSub.Core.Tv;

/// <summary>Where <see cref="TvInstaller.InstallAsync"/> is, for the progress UI.</summary>
public enum TvInstallStep { Downloading, Finding, Connecting, WaitingForApproval, Installing, Enabling, Done }

/// <summary>Outcome of an install/update; <see cref="ErrorKey"/> is a UI resource key (Tv*) when <see cref="Ok"/> is false.</summary>
/// <param name="LocalNetworkBlocked">adb could not reach the TV with "No route to host": on macOS, Local Network access is probably denied.</param>
public sealed record TvInstallResult(bool Ok, string? Endpoint, string? ErrorKey, bool LocalNetworkBlocked = false);

/// <summary>Installs / updates the TV app over network ADB and switches its accessibility service on.</summary>
/// <param name="apkVersion">versionCode of <paramref name="apkPath"/> (downloaded from GitHub or bundled).</param>
/// <param name="delay">Waits (enable read-back); injectable so tests run instantly.</param>
public sealed class TvInstaller(AdbClient adb, string apkPath, int apkVersion, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const string AccessibilityKey = "enabled_accessibility_services";
    private const string MasterKey = "accessibility_enabled";
    private const string RestrictedSettingsOp = "ACCESS_RESTRICTED_SETTINGS";
    private const string AutoStartOp = "APP_AUTO_START";
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EnableSettle = TimeSpan.FromSeconds(3);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((t, ct) => Task.Delay(t, ct));

    /// <summary>
    /// Finds the TV (unless <paramref name="endpoint"/> is given), connects, waits for the user to accept the TV's
    /// prompt, installs the APK and enables the accessibility service. The first TV found wins when several answer.
    /// The adb server is stopped afterwards, whatever the outcome.
    /// </summary>
    public async Task<TvInstallResult> InstallAsync(string? endpoint, IProgress<TvInstallStep> progress, CancellationToken ct)
    {
        try
        {
            return await InstallCoreAsync(endpoint, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            await adb.KillServerAsync().ConfigureAwait(false);
        }
    }

    private async Task<TvInstallResult> InstallCoreAsync(string? endpoint, IProgress<TvInstallStep> progress, CancellationToken ct)
    {
        try
        {
            if (endpoint is null)
            {
                progress.Report(TvInstallStep.Finding);
                var tvs = await adb.FindTvsAsync(ct).ConfigureAwait(false);
                if (tvs.Count == 0)
                    return new TvInstallResult(false, null, "TvNotFound");
                endpoint = tvs[0].Endpoint;
            }

            progress.Report(TvInstallStep.Connecting);
            if (await adb.ConnectAsync(endpoint, ct).ConfigureAwait(false) == AdbConnect.Failed)
                return Fail(endpoint, "TvConnectFailed", adb.LocalNetworkBlocked);

            // "connected" does not mean authorized: a second connect says "already connected" while the prompt is still open.
            if (await adb.GetStateAsync(endpoint, ct).ConfigureAwait(false) != "device")
            {
                progress.Report(TvInstallStep.WaitingForApproval);
                if (!await WaitForApprovalAsync(endpoint, ct).ConfigureAwait(false))
                    return Fail(endpoint, "TvApprovalTimeout");
            }

            progress.Report(TvInstallStep.Installing);
            await AllowAutoStartAsync(endpoint, ct).ConfigureAwait(false); // an app already there: before the system rebinds it
            var install = await adb.InstallAsync(endpoint, apkPath, ct).ConfigureAwait(false);
            if (install != AdbInstallOutcome.Success)
                return Fail(endpoint, install == AdbInstallOutcome.SignatureMismatch ? "TvSignatureMismatch" : "TvInstallFailed");

            await AllowAutoStartAsync(endpoint, ct).ConfigureAwait(false); // a fresh install: before enabling binds it
            progress.Report(TvInstallStep.Enabling);
            if (!await EnableAsync(endpoint, ct).ConfigureAwait(false))
                return Fail(endpoint, "TvEnableFailed");

            progress.Report(TvInstallStep.Done);
            return new TvInstallResult(true, endpoint, null);
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            return Fail(endpoint, "TvInstallFailed");
        }
    }

    /// <summary>
    /// Installs the APK over an older installed one. Never waits for approval (an unauthorized TV gives
    /// <c>TvNeedsApproval</c>) and never installs where the app is absent: that is the user's explicit setup action.
    /// Succeeds without doing anything when the installed version is the same or newer. The adb server is stopped
    /// afterwards, whatever the outcome.
    /// </summary>
    public async Task<TvInstallResult> UpdateIfOlderAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            return await UpdateIfOlderCoreAsync(endpoint, ct).ConfigureAwait(false);
        }
        finally
        {
            await adb.KillServerAsync().ConfigureAwait(false);
        }
    }

    private async Task<TvInstallResult> UpdateIfOlderCoreAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            if (await adb.ConnectAsync(endpoint, ct).ConfigureAwait(false) == AdbConnect.Failed)
                return Fail(endpoint, "TvConnectFailed", adb.LocalNetworkBlocked);
            if (await adb.GetStateAsync(endpoint, ct).ConfigureAwait(false) != "device")
                return Fail(endpoint, "TvNeedsApproval");

            var installed = await adb.GetVersionCodeAsync(endpoint, ct).ConfigureAwait(false);
            if (installed is not { } version)
                return new TvInstallResult(true, endpoint, null);
            // Also for an up-to-date app, so TVs set up before this fix get it on the next check.
            await AllowAutoStartAsync(endpoint, ct).ConfigureAwait(false);
            if (version >= apkVersion)
                return new TvInstallResult(true, endpoint, null);

            var install = await adb.InstallAsync(endpoint, apkPath, ct).ConfigureAwait(false);
            if (install != AdbInstallOutcome.Success)
                return Fail(endpoint, install == AdbInstallOutcome.SignatureMismatch ? "TvSignatureMismatch" : "TvInstallFailed");
            await AllowAutoStartAsync(endpoint, ct).ConfigureAwait(false);

            // Normally the setting survives an update; this is a no-op then.
            return await EnableAsync(endpoint, ct).ConfigureAwait(false)
                ? new TvInstallResult(true, endpoint, null)
                : Fail(endpoint, "TvEnableFailed");
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            return Fail(endpoint, "TvInstallFailed");
        }
    }

    /// <summary>A runner timeout while waiting counts as the approval not arriving.</summary>
    private async Task<bool> WaitForApprovalAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            return await adb.WaitAuthorizedAsync(endpoint, ApprovalTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lifts "restricted settings" for the app, adds our service to the enabled list (keeping others), waits, and reads
    /// back; the system sometimes drops the entry right after an install, so it retries once from the list it read back.
    /// An unreadable list (failed read) is never written over: that would drop other apps' services, so it fails
    /// instead. Last, turns accessibility_enabled on when it is not already.
    /// </summary>
    private async Task<bool> EnableAsync(string serial, CancellationToken ct)
    {
        // Android 13+ puts an app installed outside a store under "restricted settings"; Google TV (TCL, Sony) then
        // switches its accessibility service straight back off. Older Android has no such op, so a failure is ignored.
        await adb.AllowAppOpAsync(serial, RestrictedSettingsOp, ct).ConfigureAwait(false);
        if (!await AddServiceAsync(serial, ct).ConfigureAwait(false))
            return false;
        if (await adb.GetSecureAsync(serial, MasterKey, ct).ConfigureAwait(false) == "1")
            return true;
        return await adb.PutSecureAsync(serial, MasterKey, "1", ct).ConfigureAwait(false);
    }

    private async Task<bool> AddServiceAsync(string serial, CancellationToken ct)
    {
        var current = await adb.GetSecureAsync(serial, AccessibilityKey, ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (current is null)
                return false;
            if (AccessibilityList.Contains(current))
                return true;
            if (!await adb.PutSecureAsync(serial, AccessibilityKey, AccessibilityList.Add(current), ct).ConfigureAwait(false))
                return false;
            await _delay(EnableSettle, ct).ConfigureAwait(false);
            current = await adb.GetSecureAsync(serial, AccessibilityKey, ct).ConfigureAwait(false);
        }
        return AccessibilityList.Contains(current);
    }

    /// <summary>
    /// TCL TVs (TclAppBootManager) refuse to bind the accessibility service of an app without APP_AUTO_START
    /// ("callee_does't_have_OP_AUTO_START_permission"), so the TV app never runs. Other TVs do not know the op and the
    /// command just fails. Best effort: a process failure is logged, never fails the install; cancellation propagates.
    /// </summary>
    private async Task AllowAutoStartAsync(string serial, CancellationToken ct)
    {
        try
        {
            if (await adb.AllowAppOpAsync(serial, AutoStartOp, ct).ConfigureAwait(false))
                Diagnostics.Log.Info("TV app: auto-start allowed (APP_AUTO_START)");
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            Diagnostics.Log.Warn("adb appops APP_AUTO_START failed", ex);
        }
    }

    private static TvInstallResult Fail(string? endpoint, string key, bool blocked = false) => new(false, endpoint, key, blocked);

    /// <summary>Timeouts and a missing adb.exe become a failed result; cancellation still propagates.</summary>
    private static bool IsProcessFailure(Exception ex) => ex is TimeoutException or Win32Exception or IOException;
}
