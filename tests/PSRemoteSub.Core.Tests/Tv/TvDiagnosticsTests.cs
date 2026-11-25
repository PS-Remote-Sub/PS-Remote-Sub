using System.IO.Compression;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class TvDiagnosticsTests : IDisposable
{
    private const string Endpoint = "192.168.0.33:5555";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-diag-test-" + Guid.NewGuid().ToString("N"));

    public TvDiagnosticsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Fx(string device, string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tv-diag", device, file));

    private static ProcessResult Ok(string stdout) => new(0, stdout, "");

    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 25, 7, 30, 5, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan t) => _ticks += t.Ticks;
    }

    private sealed class Steps : IProgress<TvDiagStep>
    {
        public List<TvDiagStep> Seen { get; } = [];
        public void Report(TvDiagStep value) => Seen.Add(value);
    }

    /// <summary>A healthy emulator-api34 TV with the app installed, every channel answering.</summary>
    private static Func<string[], ProcessResult> Device(string device = "emulator-api34", Func<string[], ProcessResult?>? over = null) => a =>
    {
        var special = over?.Invoke(a);
        if (special is not null) return special;
        if (a[0] == "mdns") return Ok($"List of discovered mdns services\nadb-X\t_adb._tcp\t{Endpoint}\n");
        if (a[0] == "connect") return Ok($"connected to {Endpoint}\r\n");
        if (a[0] == "kill-server") return Ok("");
        if (a.Contains("get-state")) return Ok("device\r\n");
        if (a.Contains("getprop")) return Ok(Fx(device, "getprop.txt"));
        if (a.Contains("pidof")) return Ok(Fx(device, "pidof.txt"));
        if (a.Contains("logcat")) return Ok(Fx("emulator-api34", "logcat.txt"));
        if (a.Contains("settings")) return Ok(a.Contains("accessibility_enabled") ? "1\n" : Fx(device, "enabled-services.txt"));
        if (a.Contains("accessibility")) return Ok(Fx(device, "dumpsys-accessibility.txt"));
        if (a.Contains("package")) return Ok(Fx(device, "dumpsys-package.txt"));
        if (a.Contains("service")) return Ok(Fx(device, "dumpsys-service.txt"));
        if (a.Contains("content"))
            return Ok(a.Any(x => x.EndsWith("/state")) ? Fx(device, "content-state.txt")
                : a.Any(x => x.EndsWith("/log")) ? Fx(device, "content-log.txt")
                : Fx("emulator-api34", "content-crash.txt"));
        return new ProcessResult(1, "", "unexpected " + string.Join(' ', a));
    };

    private (TvDiagnostics Diag, FakeAdbRunner Runner, string LogPath) Create(
        Func<string[], ProcessResult> handler, string? logContent = "pc line\n", string pcIdHash = "ab12cd34", string[]? clients = null,
        int? webPort = 5000)
    {
        var runner = new FakeAdbRunner { Handler = handler };
        var clock = new FakeClock();
        Task Delay(TimeSpan t, CancellationToken _) { clock.Advance(t); return Task.CompletedTask; }
        var adb = new AdbClient("adb.exe", runner, Delay, clock);
        var logPath = Path.Combine(_dir, "log.txt");
        if (logContent is not null) File.WriteAllText(logPath, logContent);
        var pc = new TvDiagPcInfo("1.2.3", "Windows 11", 3, webPort, "Enabled=true\nMode=Auto\nAdbAddress=" + Endpoint, pcIdHash,
            clients ?? [], [logPath, Path.Combine(_dir, "log.1.txt")]);
        return (new TvDiagnostics(adb, pc, clock), runner, logPath);
    }

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        });
    }

    private string OutDir => Path.Combine(_dir, "out");

    [Fact]
    public async Task FullSuccess_writesEverySection()
    {
        var (diag, runner, _) = Create(Device(), clients: ["192.168.0.33"]);
        var steps = new Steps();

        var result = await diag.CollectAsync(null, OutDir, steps, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(OutDir, "psrs-tv-diagnostics-20261125-073005.zip"), result.ZipPath);
        var zip = ReadZip(result.ZipPath!);
        Assert.Equal(
            new[] { "summary.txt", "device.txt", "packages.txt", "accessibility.txt", "tv-app-log.txt", "tv-app-state.txt",
                    "tv-app-crash.txt", "logcat.txt", "pc-info.txt", "pc-log.txt", "errors.txt" }.Order(),
            zip.Keys.Order());
        Assert.Equal("channel=content", zip["errors.txt"].Trim());
        Assert.Equal(1, runner.Count("kill-server"));
        Assert.Equal("pc line\n", zip["pc-log.txt"]);
        Assert.Contains("psrs-diag-state v1", zip["tv-app-state.txt"]);
        Assert.Contains("time=2026-11-25T04:47:42.160Z", zip["tv-app-crash.txt"]);
        Assert.Contains("ro.product.model", zip["device.txt"]);
        Assert.Contains("Listed", zip["accessibility.txt"]);
        Assert.Contains("versionCode", zip["packages.txt"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.2.3", zip["pc-info.txt"]);
        Assert.Contains("1. ✔", zip["summary.txt"]);
        Assert.Equal(TvDiagStep.Done, steps.Seen[^1]);
        Assert.Contains(TvDiagStep.Writing, steps.Seen);
    }

    [Fact]
    public async Task Unreachable_stillWritesPcFilesAndReason()
    {
        var (diag, runner, _) = Create(a => a[0] switch
        {
            "mdns" => Ok($"List\nadb-X\t_adb._tcp\t{Endpoint}\n"),
            "connect" => Ok("failed to connect to " + Endpoint),
            _ => Ok(""),
        });

        var result = await diag.CollectAsync(null, OutDir, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        var zip = ReadZip(result.ZipPath!);
        Assert.All(new[] { "summary.txt", "pc-info.txt", "pc-log.txt", "errors.txt" }, k => Assert.Contains(k, zip.Keys));
        Assert.DoesNotContain("device.txt", zip.Keys);
        Assert.StartsWith("channel=none", zip["errors.txt"]);
        Assert.Contains("1. ✘", zip["summary.txt"]);
        Assert.Equal(1, runner.Count("kill-server"));
    }

    [Fact]
    public async Task NoTvFound_stillWritesZip()
    {
        var (diag, runner, _) = Create(_ => Ok(""));

        var result = await diag.CollectAsync(null, OutDir, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("1. ✘", ReadZip(result.ZipPath!)["summary.txt"]);
        Assert.Equal(1, runner.Count("kill-server"));
    }

    [Fact]
    public async Task Unauthorized_thenApproved_collects()
    {
        var states = new Queue<string>(["unauthorized", "unauthorized", "device"]);
        var (diag, _, _) = Create(Device(over: a => a.Contains("get-state") && states.Count > 0 ? Ok(states.Dequeue() + "\r\n") : null));
        var steps = new Steps();

        var result = await diag.CollectAsync(Endpoint, OutDir, steps, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains(TvDiagStep.WaitingForApproval, steps.Seen);
        Assert.DoesNotContain(TvDiagStep.Finding, steps.Seen);
        Assert.Contains("device.txt", ReadZip(result.ZipPath!).Keys);
    }

    [Fact]
    public async Task OldTvApp_noProvider()
    {
        const string provider = "Error while accessing provider:th.psrs.tvoverlay.diag\njava.lang.IllegalStateException: Could not find provider";
        var (diag, _, _) = Create(Device(over: a =>
            a.Contains("content") ? Ok(provider)
            : a.Contains("service") ? Ok("No services match: th.psrs.tvoverlay/.OverlayA11yService\nUse -h for help.\n")
            : null));

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        var zip = ReadZip(result.ZipPath!);
        Assert.Matches("≤ v2|not running", zip["tv-app-state.txt"]);
        Assert.DoesNotContain("Could not find provider", zip["tv-app-state.txt"]);
        Assert.Contains("5. ?", zip["summary.txt"]);
        Assert.StartsWith("channel=logcat-only", zip["errors.txt"]);
    }

    [Fact]
    public async Task OldTvApp_versionTwo_saysNoBuiltInLog_andCheck5Unknown()
    {
        var pkg = Fx("emulator-api34", "dumpsys-package.txt").Replace("versionCode=3", "versionCode=2");
        var (diag, _, _) = Create(Device(over: a =>
            a.Contains("content") ? Ok("Error while accessing provider:th.psrs.tvoverlay.diag")
            : a.Contains("service") ? Ok("No services match")
            : a.Contains("package") ? Ok(pkg)
            : null));

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        var zip = ReadZip(result.ZipPath!);
        Assert.Contains("≤ v2", zip["tv-app-log.txt"]);
        Assert.Contains("≤ v2", zip["tv-app-state.txt"]);
        Assert.Contains("≤ v2", zip["tv-app-crash.txt"]);
    }

    [Fact]
    public async Task LogcatTimeout_otherSectionsSurvive()
    {
        var (diag, runner, _) = Create(Device(over: a => a.Contains("logcat") ? throw new TimeoutException("slow") : null));

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        var zip = ReadZip(result.ZipPath!);
        Assert.DoesNotContain("logcat.txt", zip.Keys);
        Assert.Contains("logcat", zip["errors.txt"].Split('\n').Skip(1).First(l => l.Length > 0));
        Assert.Contains("device.txt", zip.Keys);
        Assert.Contains("tv-app-state.txt", zip.Keys);
        Assert.Equal(1, runner.Count("kill-server"));
    }

    [Fact]
    public async Task FirstTvTimeout_skipsRemainingTvSections()
    {
        var (diag, runner, _) = Create(Device(over: a => a.Contains("getprop") ? throw new TimeoutException("silent TV") : null));

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        var zip = ReadZip(result.ZipPath!);
        var errors = zip["errors.txt"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("device.txt: timed out", errors);
        Assert.Contains("packages.txt: skipped after timeout", errors);
        Assert.Contains("logcat.txt: skipped after timeout", errors);
        Assert.Single(errors, e => e.StartsWith("accessibility.txt:", StringComparison.Ordinal));
        Assert.False(runner.Called("logcat"));
        Assert.False(runner.Called("package"));
        Assert.False(runner.Called("content"));
        Assert.False(runner.Called("pidof"));
        Assert.Contains("summary.txt", zip.Keys);
        Assert.Equal(1, runner.Count("kill-server"));
    }

    [Theory]
    [InlineData("mdns")]
    [InlineData("getprop")]
    public async Task UnexpectedException_stillWritesZipAndNamesType(string failing)
    {
        var (diag, runner, _) = Create(Device(over: a => a.Contains(failing) ? throw new InvalidOperationException("boom 192.168.0.33") : null));

        var result = await diag.CollectAsync(null, OutDir, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        var zip = ReadZip(result.ZipPath!);
        Assert.Contains("tv: InvalidOperationException", zip["errors.txt"]);
        Assert.DoesNotContain("boom", zip["errors.txt"]);
        Assert.Contains("pc-info.txt", zip.Keys);
        Assert.Contains("summary.txt", zip.Keys);
        Assert.Equal(1, runner.Count("kill-server"));
    }

    [Fact]
    public async Task PcInfo_hasWebPortAndNewestApkLabel()
    {
        var (diag, _, _) = Create(Device(), webPort: 5123);

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        var info = ReadZip(result.ZipPath!)["pc-info.txt"];
        Assert.Contains("Web server port: 5123", info);
        Assert.Contains("Newest TV APK available on this PC: 3", info);
        Assert.DoesNotContain("Bundled", info);
    }

    [Fact]
    public async Task PcInfo_webServerNotRunning()
    {
        var (diag, _, _) = Create(Device(), webPort: null);

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        Assert.Contains("Web server port: not running", ReadZip(result.ZipPath!)["pc-info.txt"]);
    }

    [Fact]
    public async Task PcLogLockedForWriting_isCopied()
    {
        var (diag, _, log) = Create(Device(), logContent: "held open line\n");
        using var held = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        Assert.Equal("held open line\n", ReadZip(result.ZipPath!)["pc-log.txt"]);
    }

    [Fact]
    public async Task ServiceNotRunning_dumpsysFallback()
    {
        var (diag, _, _) = Create(Device("emulator-api34", a =>
            a.Contains("content") ? new ProcessResult(255, "", "denied")
            : a.Contains("service") ? Ok("No services match: th.psrs.tvoverlay/.OverlayA11yService")
            : a.Contains("accessibility") && a.Contains("dumpsys") ? Ok("ACCESSIBILITY MANAGER (dumpsys accessibility)\n  Bound services:{}\n")
            : null));

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        var zip = ReadZip(result.ZipPath!);
        Assert.Contains("not running", zip["tv-app-state.txt"]);
        Assert.Contains("4. ✘", zip["summary.txt"]);
        Assert.StartsWith("channel=logcat-only", zip["errors.txt"]);
    }

    [Fact]
    public async Task ContentFails_dumpsysServiceSuppliesStateAndLog()
    {
        var (diag, _, _) = Create(Device(over: a => a.Contains("content") ? new ProcessResult(255, "", "denied") : null));

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        var zip = ReadZip(result.ZipPath!);
        Assert.StartsWith("channel=dumpsys", zip["errors.txt"]);
        Assert.StartsWith("psrs-diag-state v1", zip["tv-app-state.txt"]);
        Assert.Contains("tv-app-log.txt", zip.Keys);
        Assert.DoesNotContain("---", zip["tv-app-state.txt"]);
    }

    [Fact]
    public async Task Privacy_noForbiddenStrings()
    {
        // AppHost is the hashing boundary: it hands TvDiagnostics only PcIdHash(PcId), never the raw id. Hash the raw id the
        // same way here, then check the hash shows where expected and the raw id nowhere.
        const string rawPcId = "pc-raw-id";
        var hash = TvDiagText.PcIdHash(rawPcId);
        var getprop = Fx("emulator-api34", "getprop.txt") + "\n[ro.serialno]: [ABC123]\n[persist.sys.wifi.ssid]: [HomeWifi]\n";
        var logcat = Fx("emulator-api34", "logcat.txt") +
            "\n11-25 04:50:00.000  999  999 I NetflixTag: opened com.netflix.ninja now\n";
        var a11yDump = Fx("emulator-api34", "dumpsys-accessibility.txt") + "\n  Bound services:{Service[label=Secret, feedbackType[1], capabilities=1, eventTypes=1, notificationTimeout=0, component=com.secret/.Svc]}\n";
        var (diag, _, _) = Create(Device(over: a =>
            a.Contains("getprop") ? Ok(getprop)
            : a.Contains("logcat") ? Ok(logcat)
            : a.Contains("accessibility") && a.Contains("dumpsys") ? Ok(a11yDump)
            : a.Contains("settings") && !a.Contains("accessibility_enabled") ? Ok("com.secret/.Svc:th.psrs.tvoverlay/.OverlayA11yService\n")
            : null), pcIdHash: hash);

        var result = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        var zip = ReadZip(result.ZipPath!);
        Assert.Contains($"PC id hash: {hash}", zip["pc-info.txt"]);
        Assert.Contains($"PC id hash: {hash}", zip["summary.txt"]);
        foreach (var (name, text) in zip)
        {
            Assert.DoesNotContain("ABC123", text);
            Assert.DoesNotContain("netflix", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("com.secret", text);
            Assert.DoesNotContain(rawPcId, text);
            Assert.DoesNotContain("HomeWifi", text);
        }
        Assert.Contains("OtherEnabledCount", zip["accessibility.txt"]);
    }

    [Fact]
    public async Task ExistingName_getsNumberedSuffix()
    {
        var (diag, _, _) = Create(Device());

        var first = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);
        var second = await diag.CollectAsync(Endpoint, OutDir, new Steps(), CancellationToken.None);

        Assert.EndsWith("psrs-tv-diagnostics-20261125-073005.zip", first.ZipPath);
        Assert.EndsWith("psrs-tv-diagnostics-20261125-073005-2.zip", second.ZipPath);
        Assert.Empty(Directory.GetFiles(OutDir, "*.tmp"));
    }

    [Fact]
    public async Task OutputDirIsAFile_reportsWriteFailed()
    {
        var (diag, runner, _) = Create(Device());
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");

        var result = await diag.CollectAsync(Endpoint, blocker, new Steps(), CancellationToken.None);

        Assert.Equal(new TvDiagResult(false, null, "TvDiagWriteFailed"), result);
        Assert.Equal(1, runner.Count("kill-server"));
    }

    [Fact]
    public void ZipName_format() =>
        Assert.Equal("psrs-tv-diagnostics-20261125-143005.zip", TvDiagnostics.ZipName(new DateTime(2026, 11, 25, 14, 30, 5)));

    [Fact]
    public async Task Cancelled_propagatesAndKillsServer()
    {
        var (diag, runner, _) = Create(Device());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        runner.Handler = a => a[0] == "kill-server" ? Ok("") : throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diag.CollectAsync(Endpoint, OutDir, new Steps(), cts.Token));

        Assert.Equal(1, runner.Count("kill-server"));
    }
}
