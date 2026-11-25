using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PSRemoteSub.Core.Tv;

/// <summary>Where <see cref="TvDiagnostics.CollectAsync"/> is, for the progress UI.</summary>
public enum TvDiagStep { Finding, Connecting, WaitingForApproval, Collecting, Writing, Done }

/// <summary>What the PC knows about itself. Every field is already privacy-safe (no PcId, no machine name).</summary>
/// <param name="SettingsText">TvSettings (Enabled, Mode, AdbAddress), rendered by the caller.</param>
/// <param name="PcIdHash"><see cref="TvDiagText.PcIdHash"/> of this PC's id.</param>
/// <param name="TvClients">IPs of the TVs connected to <c>/tv/events</c> right now.</param>
/// <param name="PcLogFiles">Log files to copy, newest first (<c>pc-log.txt</c>, <c>pc-log.1.txt</c>, ...); missing ones are skipped.</param>
public sealed record TvDiagPcInfo(
    string AppVersion, string Os, int? NewestApkVersion, string SettingsText, string PcIdHash,
    IReadOnlyList<string> TvClients, IReadOnlyList<string> PcLogFiles);

/// <summary><see cref="Ok"/> is true whenever a zip was written, even when the TV was unreachable.</summary>
/// <param name="ErrorKey"><c>TvDiagWriteFailed</c> only when the zip itself could not be written.</param>
public sealed record TvDiagResult(bool Ok, string? ZipPath, string? ErrorKey);

/// <summary>
/// Collects the TV diagnostics dump over network ADB (all text filtered for privacy on the PC) and writes it, with the
/// PC's own information, into one zip. Never throws except for cancellation; every section is independent.
/// </summary>
public sealed class TvDiagnostics(AdbClient adb, TvDiagPcInfo pc, TimeProvider? time = null)
{
    private const string AccessibilityKey = "enabled_accessibility_services";
    private const string StateHeader = "psrs-diag-state v1";
    private const string LogHeader = "psrs-diag-log v1";
    private const string CrashHeader = "psrs-diag-crash v1";
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The zip file name for a local time, <c>psrs-tv-diagnostics-yyyyMMdd-HHmmss.zip</c>.</summary>
    public static string ZipName(DateTime localNow) =>
        $"psrs-tv-diagnostics-{localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip";

    /// <summary>
    /// Finds the TV (unless <paramref name="endpoint"/> is given), connects, waits for the user's approval, reads every
    /// section and writes the zip into <paramref name="outputDir"/>. A TV that cannot be reached still gives a zip with
    /// the PC files. The adb server is stopped afterwards, whatever the outcome.
    /// </summary>
    public async Task<TvDiagResult> CollectAsync(string? endpoint, string outputDir, IProgress<TvDiagStep> progress, CancellationToken ct)
    {
        try
        {
            var now = _time.GetLocalNow();
            var data = new Collected();
            await CollectTvAsync(endpoint, progress, data, ct).ConfigureAwait(false);

            progress.Report(TvDiagStep.Writing);
            var summary = TvDiagText.Summary(data.ToFacts(now, pc));
            data.Entries.Insert(0, ("summary.txt", summary));
            var result = ZipWriter.Write(outputDir, ZipName(now.DateTime), data, pc);
            if (result.Ok)
                progress.Report(TvDiagStep.Done);
            return result;
        }
        finally
        {
            await adb.KillServerAsync().ConfigureAwait(false);
        }
    }

    private async Task CollectTvAsync(string? endpoint, IProgress<TvDiagStep> progress, Collected d, CancellationToken ct)
    {
        string serial;
        try
        {
            if (endpoint is null)
            {
                progress.Report(TvDiagStep.Finding);
                var tvs = await adb.FindTvsAsync(ct).ConfigureAwait(false);
                if (tvs.Count == 0)
                {
                    d.Unreached("no TV found on the network");
                    return;
                }
                endpoint = tvs[0].Endpoint;
            }

            progress.Report(TvDiagStep.Connecting);
            if (await adb.ConnectAsync(endpoint, ct).ConfigureAwait(false) == AdbConnect.Failed)
            {
                d.Unreached(adb.LocalNetworkBlocked ? "adb connect failed (local network blocked?)" : "adb connect failed");
                return;
            }

            if (await adb.GetStateAsync(endpoint, ct).ConfigureAwait(false) != "device")
            {
                progress.Report(TvDiagStep.WaitingForApproval);
                if (!await WaitForApprovalAsync(endpoint, ct).ConfigureAwait(false))
                {
                    d.Unreached("TV did not approve the connection in time");
                    return;
                }
            }
            serial = endpoint;
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            d.Unreached("adb failed: " + Reason(ex));
            return;
        }

        d.Reached = true;
        progress.Report(TvDiagStep.Collecting);
        await CollectDeviceAsync(serial, d, ct).ConfigureAwait(false);
        await CollectPackageAsync(serial, d, ct).ConfigureAwait(false);
        await CollectAccessibilityAsync(serial, d, ct).ConfigureAwait(false);
        var pids = await Section(d, "pidof", () => adb.PidsAsync(serial, ct), []).ConfigureAwait(false);
        await CollectTvAppAsync(serial, d, ct).ConfigureAwait(false);
        await CollectLogcatAsync(serial, pids, d, ct).ConfigureAwait(false);
    }

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

    private async Task CollectDeviceAsync(string serial, Collected d, CancellationToken ct)
    {
        var text = await Section(d, "device.txt", () => adb.GetPropsAsync(serial, ct), null).ConfigureAwait(false);
        if (text is null)
        {
            d.FailedIfEmpty("device.txt", "getprop failed");
            return;
        }
        d.Props = TvDiagText.WhitelistedProps(text);
        d.Entries.Add(("device.txt", string.Join('\n', d.Props.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")) + "\n"));
    }

    private async Task CollectPackageAsync(string serial, Collected d, CancellationToken ct)
    {
        var text = await Section(d, "packages.txt", () => adb.DumpsysPackageAsync(serial, ct), null).ConfigureAwait(false);
        if (text is null)
        {
            d.FailedIfEmpty("packages.txt", "dumpsys package failed");
            return;
        }
        d.Package = TvDiagText.ParsePackage(text);
        d.NotInstalled = d.Package is null && text.Contains("Unable to find package", StringComparison.Ordinal);
        var p = d.Package;
        d.Entries.Add(("packages.txt", p is null
            ? "TV app th.psrs.tvoverlay is not installed / ยังไม่ได้ติดตั้งแอปบนทีวี\n"
            : $"package=th.psrs.tvoverlay\nversionCode={p.VersionCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}\nversionName={p.VersionName ?? "?"}\n" +
              $"firstInstallTime={p.FirstInstall ?? "?"}\nlastUpdateTime={p.LastUpdate ?? "?"}\nenabled={(p.Enabled is { } e ? (e ? "true" : "false") : "?")}\n"));
    }

    private async Task CollectAccessibilityAsync(string serial, Collected d, CancellationToken ct)
    {
        var enabled = await Section(d, "accessibility.txt", () => adb.GetSecureAsync(serial, AccessibilityKey, ct), null).ConfigureAwait(false);
        var master = await Section(d, "accessibility.txt", () => adb.GetSecureAsync(serial, "accessibility_enabled", ct), null).ConfigureAwait(false);
        var dump = await Section(d, "accessibility.txt", () => adb.DumpsysAccessibilityAsync(serial, ct), null).ConfigureAwait(false);
        if (enabled is null && master is null && dump is null)
        {
            d.FailedIfEmpty("accessibility.txt", "no accessibility data readable");
            return;
        }
        var a = TvDiagText.ParseAccessibility(enabled, master, dump);
        d.A11y = a;
        string B(bool? v) => v is { } b ? (b ? "true" : "false") : "unknown";
        d.Entries.Add(("accessibility.txt",
            $"Listed={B(a.Listed)}\nBound={B(a.Bound)}\nOtherEnabledCount={a.OtherEnabledCount}\naccessibility_enabled={master?.Trim() ?? "unknown"}\n"));
    }

    private async Task CollectTvAppAsync(string serial, Collected d, CancellationToken ct)
    {
        var state = await ReadChannelAsync(serial, TvDiagChannel.State, StateHeader, d, ct).ConfigureAwait(false);
        var log = await ReadChannelAsync(serial, TvDiagChannel.Log, LogHeader, d, ct).ConfigureAwait(false);
        var crash = await ReadChannelAsync(serial, TvDiagChannel.Crash, CrashHeader, d, ct).ConfigureAwait(false);
        var fromContent = state is not null || log is not null;
        var bothFromContent = state is not null && log is not null;

        var fromDumpsys = false;
        string? dumpsysText = null;
        if (!bothFromContent)
        {
            dumpsysText = await Section(d, "tv-app-state.txt", () => adb.DumpsysServiceAsync(serial, ct), null).ConfigureAwait(false);
            var (s, l) = TvDiagText.SplitDumpsysService(dumpsysText);
            if (state is null && s is not null) { state = s; fromDumpsys = true; }
            if (log is null && l is not null && TvDiagText.HasHeader(l, LogHeader)) { log = l; fromDumpsys = true; }
        }
        d.Channel = bothFromContent ? "content" : fromDumpsys ? "dumpsys" : fromContent ? "content" : "logcat-only";
        var notRunning = dumpsysText?.Contains("No services match", StringComparison.Ordinal) == true;

        d.Entries.Add(("tv-app-log.txt", log ?? Unavailable(d, notRunning)));
        d.Entries.Add(("tv-app-state.txt", state ?? Unavailable(d, notRunning)));
        d.State = TvDiagText.ParseState(state);

        if (crash is null)
        {
            d.Entries.Add(("tv-app-crash.txt", Unavailable(d, notRunning)));
            return;
        }
        d.CrashRead = true;
        var body = crash.Replace("\r\n", "\n").Split('\n').SkipWhile(l => l.Trim().Length == 0).Skip(1).ToList();
        var record = string.Join('\n', body).Trim();
        if (record.Equals("none", StringComparison.OrdinalIgnoreCase) || record.Length == 0)
            return; // no crash: the file is absent
        d.LastCrashLine = body.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("time=", StringComparison.Ordinal));
        d.Entries.Add(("tv-app-crash.txt", crash.TrimEnd() + "\n"));
    }

    /// <summary>One <c>content read</c>; the text only when it starts with <paramref name="header"/> (else the channel is unavailable).</summary>
    private async Task<string?> ReadChannelAsync(string serial, TvDiagChannel channel, string header, Collected d, CancellationToken ct)
    {
        var text = await Section(d, $"tv-app-{channel.ToString().ToLowerInvariant()}.txt", () => adb.ContentReadAsync(serial, channel, ct), null).ConfigureAwait(false);
        return TvDiagText.HasHeader(text, header) ? text : null;
    }

    private static string Unavailable(Collected d, bool notRunning)
    {
        if (d.Package?.VersionCode is <= 2)
            return "TV app ≤ v2 has no built-in log / แอปบนทีวีรุ่น v2 หรือเก่ากว่าไม่มีบันทึกในตัว\n";
        if (d.NotInstalled)
            return "TV app is not installed / ยังไม่ได้ติดตั้งแอปบนทีวี\n";
        if (notRunning)
            return "TV app service is not running / บริการของแอปบนทีวีไม่ได้ทำงาน\n";
        return "Not available from the TV app / อ่านข้อมูลจากแอปบนทีวีไม่ได้\n";
    }

    private async Task CollectLogcatAsync(string serial, IReadOnlyList<int> pids, Collected d, CancellationToken ct)
    {
        var text = await Section(d, "logcat.txt", () => adb.LogcatAsync(serial, ct), null).ConfigureAwait(false);
        if (text is null)
        {
            d.FailedIfEmpty("logcat.txt", "logcat failed");
            return;
        }
        d.Entries.Add(("logcat.txt", TvDiagText.FilterLogcat(text, pids)));
    }

    /// <summary>Runs one adb read; a timeout, missing adb or I/O failure is noted in <c>errors.txt</c> and gives <paramref name="fallback"/>.</summary>
    private static async Task<T> Section<T>(Collected d, string file, Func<Task<T>> read, T fallback)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProcessFailure(ex))
        {
            d.Fail(file, Reason(ex));
            return fallback;
        }
    }

    private static bool IsProcessFailure(Exception ex) => ex is TimeoutException or Win32Exception or IOException;

    /// <summary>Only the kind of failure: adb messages can carry addresses.</summary>
    private static string Reason(Exception ex) => ex is TimeoutException ? "timed out" : ex.GetType().Name;

    /// <summary>Everything gathered so far, before it goes into the zip.</summary>
    internal sealed class Collected
    {
        public List<(string Name, string Text)> Entries { get; } = [];
        public List<string> Errors { get; } = [];
        public bool Reached { get; set; }
        public string? AdbFailure { get; private set; }
        public string Channel { get; set; } = "none";
        public IReadOnlyDictionary<string, string>? Props { get; set; }
        public TvPackageInfo? Package { get; set; }
        public bool NotInstalled { get; set; }
        public TvA11yInfo? A11y { get; set; }
        public TvAppState? State { get; set; }
        public string? LastCrashLine { get; set; }
        public bool CrashRead { get; set; }

        public void Unreached(string reason) => AdbFailure = reason;
        public void Fail(string file, string reason) => Errors.Add($"{file}: {reason}");
        /// <summary>Adds a line only when the section has not already reported its own exception.</summary>
        public void FailedIfEmpty(string file, string reason)
        {
            if (!Errors.Any(e => e.StartsWith(file + ":", StringComparison.Ordinal)))
                Fail(file, reason);
        }

        public TvDiagFacts ToFacts(DateTimeOffset now, TvDiagPcInfo pc) => new(
            now, pc.AppVersion, pc.Os, Reached, AdbFailure, Props, Package, pc.NewestApkVersion, A11y, State,
            pc.PcIdHash, pc.TvClients, LastCrashLine, CrashRead);
    }

    private static class ZipWriter
    {
        public static TvDiagResult Write(string outputDir, string name, Collected d, TvDiagPcInfo pc)
        {
            string? tmp = null;
            try
            {
                Directory.CreateDirectory(outputDir);
                tmp = Path.Combine(outputDir, $".psrs-diag-{Guid.NewGuid():N}.tmp");
                var errors = new List<string>(d.Errors);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var (entryName, text) in d.Entries)
                        AddText(zip, entryName, text);
                    AddText(zip, "pc-info.txt", PcInfoText(pc));
                    CopyLogs(zip, pc.PcLogFiles, errors);
                    AddText(zip, "errors.txt", $"channel={d.Channel}\n" + string.Concat(
                        (d.AdbFailure is { } f ? [$"tv: {f}"] : Enumerable.Empty<string>()).Concat(errors).Select(e => e + "\n")));
                }
                return new TvDiagResult(true, MoveToFinalName(tmp, outputDir, name), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                if (tmp is not null)
                {
                    try { File.Delete(tmp); } catch (Exception) { /* best effort */ }
                }
                return new TvDiagResult(false, null, "TvDiagWriteFailed");
            }
        }

        private static string MoveToFinalName(string tmp, string dir, string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            for (var n = 1; ; n++)
            {
                var path = Path.Combine(dir, n == 1 ? name : $"{stem}-{n}.zip");
                if (File.Exists(path))
                    continue;
                try
                {
                    File.Move(tmp, path, overwrite: false);
                    return path;
                }
                catch (IOException) when (File.Exists(path) && n < 1000)
                {
                    // lost a race for this name: try the next one
                }
            }
        }

        private static void AddText(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(Utf8.GetBytes(text));
        }

        private static void CopyLogs(ZipArchive zip, IReadOnlyList<string> files, List<string> errors)
        {
            for (var i = 0; i < files.Count; i++)
            {
                var name = i == 0 ? "pc-log.txt" : $"pc-log.{i}.txt";
                try
                {
                    using var src = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    src.CopyTo(dst);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // no such log yet (never rolled over): skipped silently
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{name}: {Reason(ex)}");
                }
            }
        }

        private static string PcInfoText(TvDiagPcInfo pc) =>
            $"PC app: {pc.AppVersion}\nPC OS: {pc.Os}\nBundled TV APK version: {pc.NewestApkVersion?.ToString(CultureInfo.InvariantCulture) ?? "?"}\n" +
            $"PC id hash: {pc.PcIdHash}\nTV clients connected to /tv/events: {(pc.TvClients.Count == 0 ? "none" : string.Join(", ", pc.TvClients))}\n" +
            $"\nTvSettings:\n{pc.SettingsText}\n";
    }
}
