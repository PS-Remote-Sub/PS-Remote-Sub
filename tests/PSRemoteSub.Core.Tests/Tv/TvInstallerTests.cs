using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class TvInstallerTests
{
    private const string Endpoint = "192.168.0.33:5555";
    private const string Apk = "tv.apk";

    /// <summary>
    /// Scripted runner. Every call must start with the private server port (<c>-P 5038</c>); <see cref="Calls"/> and
    /// <see cref="Handler"/> get the adb arguments after it (without the exe).
    /// </summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], ProcessResult> Handler { get; set; } = _ => new ProcessResult(0, "", "");

        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            Assert.Equal("adb.exe", exe);
            Assert.Equal(["-P", "5038"], args.Take(2));
            var a = args.Skip(2).ToArray();
            Calls.Add(a);
            return Task.FromResult(Handler(a));
        }

        public bool Called(string verb) => Calls.Any(c => c.Contains(verb));
        public int Count(string verb) => Calls.Count(c => c.Contains(verb));
    }

    private static ProcessResult Ok(string stdout) => new(0, stdout, "");

    /// <summary>Manual clock: only moves when a fake delay (or a slow fake adb call) advances it.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan t) => _ticks += t.Ticks;
    }

    private static (TvInstaller Installer, FakeRunner Runner, List<TimeSpan> Delays) Create(int bundled = 2) =>
        CreateWithClock(bundled, out _);

    private static (TvInstaller Installer, FakeRunner Runner, List<TimeSpan> Delays) CreateWithClock(int bundled, out FakeClock clock)
    {
        var runner = new FakeRunner();
        var delays = new List<TimeSpan>();
        var fake = clock = new FakeClock();
        Task Delay(TimeSpan t, CancellationToken _) { delays.Add(t); fake.Advance(t); return Task.CompletedTask; }
        var adb = new AdbClient("adb.exe", runner, Delay, fake);
        return (new TvInstaller(adb, Apk, bundled, Delay), runner, delays);
    }

    private sealed class Steps : IProgress<TvInstallStep>
    {
        public List<TvInstallStep> Seen { get; } = [];
        public void Report(TvInstallStep value) => Seen.Add(value);
    }

    /// <summary>Authorized device whose enabled_accessibility_services values come from <paramref name="secure"/> in order (last repeats).</summary>
    private static Func<string[], ProcessResult> HappyDevice(Queue<string> secure, int? installedVersion = null) => a =>
    {
        if (a[0] == "connect") return Ok($"connected to {Endpoint}\r\n");
        if (a.Contains("get-state")) return Ok("device\r\n");
        if (a.Contains("install")) return Ok("Performing Streamed Install\r\nSuccess\r\n");
        if (a.Contains("dumpsys"))
            return Ok(installedVersion is { } v ? $"    versionCode={v} minSdk=26 targetSdk=36\r\n" : "Unable to find package\r\n");
        if (a.Contains("get")) return Ok((secure.Count > 1 ? secure.Dequeue() : secure.Peek()) + "\r\n");
        return Ok("");
    };

    [Fact]
    public async Task Install_EnablesAndVerifies()
    {
        var (installer, runner, delays) = Create();
        runner.Handler = HappyDevice(new Queue<string>(["null", AccessibilityList.Ours]));
        var steps = new Steps();

        var result = await installer.InstallAsync(Endpoint, steps, CancellationToken.None);

        Assert.Equal(new TvInstallResult(true, Endpoint, null), result);
        Assert.Equal([TvInstallStep.Connecting, TvInstallStep.Installing, TvInstallStep.Enabling, TvInstallStep.Done], steps.Seen);
        var put = Assert.Single(runner.Calls, c => c.Contains("put"));
        Assert.Equal(["-s", Endpoint, "shell", "settings", "put", "secure", "enabled_accessibility_services", AccessibilityList.Ours], put);
        Assert.Equal([TimeSpan.FromSeconds(3)], delays);
    }

    [Fact]
    public async Task Install_RetriesWhenDropped()
    {
        var (installer, runner, _) = Create();
        runner.Handler = HappyDevice(new Queue<string>(["null", "null", AccessibilityList.Ours]));

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, runner.Count("put"));
    }

    [Fact]
    public async Task Install_DroppedTwice_EnableFailed()
    {
        var (installer, runner, _) = Create();
        runner.Handler = HappyDevice(new Queue<string>(["null"]));

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal(new TvInstallResult(false, Endpoint, "TvEnableFailed"), result);
        Assert.Equal(2, runner.Count("put"));
    }

    [Fact]
    public async Task Install_KeepsOtherServices()
    {
        const string talkBack = "com.google.android.marvin.talkback/.TalkBackService";
        var (installer, runner, _) = Create();
        runner.Handler = HappyDevice(new Queue<string>([talkBack, $"{talkBack}:{AccessibilityList.Ours}"]));

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal($"{talkBack}:{AccessibilityList.Ours}", Assert.Single(runner.Calls, c => c.Contains("put")).Last());
    }

    [Fact]
    public async Task Install_SignatureMismatch()
    {
        var (installer, runner, _) = Create();
        var inner = HappyDevice(new Queue<string>(["null"]));
        runner.Handler = a => a.Contains("install")
            ? new ProcessResult(1, "Performing Streamed Install\r\n", "adb: failed to install tv.apk: Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: signatures do not match]")
            : inner(a);

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal(new TvInstallResult(false, Endpoint, "TvSignatureMismatch"), result);
        Assert.False(runner.Called("put"));
    }

    [Fact]
    public async Task Install_OtherFailure_InstallFailed()
    {
        var (installer, runner, _) = Create();
        var inner = HappyDevice(new Queue<string>(["null"]));
        runner.Handler = a => a.Contains("install") ? new ProcessResult(1, "", "Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]") : inner(a);

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal("TvInstallFailed", result.ErrorKey);
    }

    [Fact]
    public async Task Install_NoEndpoint_FindsTv()
    {
        var (installer, runner, _) = Create();
        var inner = HappyDevice(new Queue<string>(["null", AccessibilityList.Ours]));
        runner.Handler = a => a[0] == "mdns"
            ? Ok("List of discovered mdns services\r\nadb-1a00bf004003264\t_adb._tcp\t" + Endpoint + "\r\n")
            : inner(a);
        var steps = new Steps();

        var result = await installer.InstallAsync(null, steps, CancellationToken.None);

        Assert.Equal(Endpoint, result.Endpoint);
        Assert.True(result.Ok);
        Assert.Equal(TvInstallStep.Finding, steps.Seen[0]);
    }

    [Fact]
    public async Task Install_NoEndpoint_NothingFound_TvNotFound()
    {
        var (installer, runner, _) = Create();
        runner.Handler = _ => Ok("List of discovered mdns services\r\n");

        var result = await installer.InstallAsync(null, new Steps(), CancellationToken.None);

        Assert.Equal(new TvInstallResult(false, null, "TvNotFound"), result);
    }

    [Fact]
    public async Task Install_ConnectFails_TvConnectFailed()
    {
        var (installer, runner, _) = Create();
        runner.Handler = _ => Ok($"failed to connect to '{Endpoint}': Connection refused\r\n");

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal("TvConnectFailed", result.ErrorKey);
    }

    [Fact]
    public async Task Install_WaitsForApproval_ThenContinues()
    {
        var (installer, runner, delays) = Create();
        var inner = HappyDevice(new Queue<string>(["null", AccessibilityList.Ours]));
        var states = new Queue<string>(["unauthorized", "unauthorized", "device"]);
        runner.Handler = a => a.Contains("get-state") ? Ok(states.Dequeue() + "\r\n") : inner(a);
        var steps = new Steps();

        var result = await installer.InstallAsync(Endpoint, steps, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains(TvInstallStep.WaitingForApproval, steps.Seen);
        Assert.Contains(delays, d => d != TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Install_NeverApproved_TvApprovalTimeout()
    {
        var (installer, runner, _) = Create();
        runner.Handler = a => a[0] == "connect" ? Ok($"failed to authenticate to {Endpoint}\r\n") : Ok("unauthorized\r\n");

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal("TvApprovalTimeout", result.ErrorKey);
        Assert.False(runner.Called("install"));
    }

    [Fact]
    public async Task Update_SkipsWhenSameVersion()
    {
        var (installer, runner, _) = Create(bundled: 2);
        runner.Handler = HappyDevice(new Queue<string>([AccessibilityList.Ours]), installedVersion: 2);

        var result = await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);

        Assert.Equal(new TvInstallResult(true, Endpoint, null), result);
        Assert.False(runner.Called("install"));
    }

    [Fact]
    public async Task Update_InstallsWhenOlder_AndKeepsEnabled()
    {
        var (installer, runner, _) = Create(bundled: 3);
        runner.Handler = HappyDevice(new Queue<string>([AccessibilityList.Ours]), installedVersion: 2);

        var result = await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, runner.Count("install"));
        Assert.False(runner.Called("put"));
    }

    [Fact]
    public async Task Update_NotInstalled_DoesNothing()
    {
        var (installer, runner, _) = Create();
        runner.Handler = HappyDevice(new Queue<string>(["null"]), installedVersion: null);

        var result = await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(runner.Called("install"));
    }

    [Fact]
    public async Task Update_Unauthorized_DoesNotWait()
    {
        var (installer, runner, delays) = Create();
        runner.Handler = a => a[0] == "connect" ? Ok($"failed to authenticate to {Endpoint}\r\n") : Ok("unauthorized\r\n");

        var result = await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);

        Assert.Equal(new TvInstallResult(false, Endpoint, "TvNeedsApproval"), result);
        Assert.Empty(delays);
        Assert.False(runner.Called("install"));
    }

    [Fact]
    public async Task Update_UnauthorizedAfterConnect_DoesNotWait()
    {
        var (installer, runner, delays) = Create();
        runner.Handler = a => a[0] == "connect" ? Ok($"already connected to {Endpoint}\r\n") : Ok("unauthorized\r\n");

        var result = await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);

        Assert.Equal("TvNeedsApproval", result.ErrorKey);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Install_ReadFails_NeverPuts()
    {
        var (installer, runner, _) = Create();
        var inner = HappyDevice(new Queue<string>(["null"]));
        runner.Handler = a => a.Contains("get") ? new ProcessResult(1, "", "error: device offline") : inner(a);

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal(new TvInstallResult(false, Endpoint, "TvEnableFailed"), result);
        Assert.False(runner.Called("put"));
    }

    [Fact]
    public async Task Install_ReadBackFails_NoSecondPut()
    {
        var (installer, runner, _) = Create();
        var inner = HappyDevice(new Queue<string>(["null"]));
        var reads = 0;
        runner.Handler = a => a.Contains("get") && ++reads == 2 ? new ProcessResult(1, "", "error: closed") : inner(a);

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal("TvEnableFailed", result.ErrorKey);
        Assert.Equal(1, runner.Count("put"));
    }

    [Fact]
    public async Task Install_RetryBuildsFromReadBackList()
    {
        const string talkBack = "com.google.android.marvin.talkback/.TalkBackService";
        const string other = "com.other/.Svc";
        var (installer, runner, _) = Create();
        runner.Handler = HappyDevice(new Queue<string>([talkBack, other, AccessibilityList.Ours]));

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.True(result.Ok);
        var puts = runner.Calls.Where(c => c.Contains("put")).Select(c => c.Last()).ToArray();
        Assert.Equal([$"{talkBack}:{AccessibilityList.Ours}", $"{other}:{AccessibilityList.Ours}"], puts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-s")]
    [InlineData("-a:5555")]
    [InlineData("a b:5555")]
    [InlineData("192.168.0.33")]
    [InlineData("192.168.0.33:0")]
    [InlineData("192.168.0.33:70000")]
    [InlineData("192.168.0.33:5555\n")]
    public async Task InvalidEndpoint_ConnectFailed_WithoutRunningAdb(string endpoint)
    {
        var (installer, runner, _) = Create();

        var install = await installer.InstallAsync(endpoint, new Steps(), CancellationToken.None);
        var update = await installer.UpdateIfOlderAsync(endpoint, CancellationToken.None);

        Assert.Equal("TvConnectFailed", install.ErrorKey);
        Assert.Equal("TvConnectFailed", update.ErrorKey);
        Assert.All(runner.Calls, c => Assert.Equal(["kill-server"], c)); // only the clean-up ever runs
    }

    [Theory]
    [InlineData("192.168.0.33:5555")]
    [InlineData("tv.local:5555")]
    [InlineData("10.0.0.1:65535")]
    public void ValidEndpoint(string endpoint) => Assert.True(AdbClient.IsValidEndpoint(endpoint));

    [Theory]
    [InlineData("a$b", "'a$b'")]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a:b/.C-d_1", "a:b/.C-d_1")]
    public async Task PutSecure_QuotesOnlyUnsafeValues(string value, string expectedArg)
    {
        var runner = new FakeRunner();
        var adb = new AdbClient("adb.exe", runner);

        await adb.PutSecureAsync(Endpoint, "k", value);

        Assert.Equal(["-s", Endpoint, "shell", "settings", "put", "secure", "k", expectedArg], runner.Calls.Single());
    }

    [Fact]
    public async Task Install_Success_KillsServerLast()
    {
        var (installer, runner, _) = Create();
        runner.Handler = HappyDevice(new Queue<string>(["null", AccessibilityList.Ours]));

        await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal(["kill-server"], runner.Calls[^1]);
        Assert.Equal(1, runner.Count("kill-server"));
    }

    [Fact]
    public async Task Install_Failure_KillsServerLast()
    {
        var (installer, runner, _) = Create();
        runner.Handler = a => a[0] == "kill-server" ? Ok("") : Ok($"failed to connect to '{Endpoint}': Connection refused\r\n");

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal("TvConnectFailed", result.ErrorKey);
        Assert.Equal(["kill-server"], runner.Calls[^1]);
    }

    [Fact]
    public async Task Update_KillsServerLast()
    {
        var (installer, runner, _) = Create(bundled: 3);
        runner.Handler = HappyDevice(new Queue<string>([AccessibilityList.Ours]), installedVersion: 2);

        await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);

        Assert.Equal(["kill-server"], runner.Calls[^1]);
        Assert.Equal(1, runner.Count("kill-server"));
    }

    [Fact]
    public async Task Update_Cancelled_StillKillsServer()
    {
        var (installer, runner, _) = Create();
        using var cts = new CancellationTokenSource();
        runner.Handler = a =>
        {
            if (a[0] == "connect") { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
            return Ok("");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.UpdateIfOlderAsync(Endpoint, cts.Token));

        Assert.Equal(["kill-server"], runner.Calls[^1]);
    }

    [Fact]
    public async Task KillServer_Failure_IsSwallowed()
    {
        var (installer, runner, _) = Create();
        runner.Handler = a => a[0] == "kill-server"
            ? throw new System.ComponentModel.Win32Exception("adb missing")
            : Ok($"failed to connect to '{Endpoint}': Connection refused\r\n");

        var result = await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);

        Assert.Equal("TvConnectFailed", result.ErrorKey);
    }

    [Fact]
    public async Task Update_DumpsysFails_IsFailureNotNotInstalled()
    {
        var (installer, runner, _) = Create();
        var inner = HappyDevice(new Queue<string>(["null"]));
        runner.Handler = a => a.Contains("dumpsys") ? new ProcessResult(255, "", "error: closed") : inner(a);

        var result = await installer.UpdateIfOlderAsync(Endpoint, CancellationToken.None);

        Assert.Equal(new TvInstallResult(false, Endpoint, "TvInstallFailed"), result);
        Assert.False(runner.Called("install"));
    }

    [Fact]
    public async Task Install_SlowAdbCalls_ApprovalLimitIsElapsedTime()
    {
        var (installer, runner, delays) = CreateWithClock(2, out var clock);
        runner.Handler = a =>
        {
            if (a.Contains("get-state")) clock.Advance(TimeSpan.FromSeconds(70));
            return a[0] == "connect" ? Ok($"failed to authenticate to {Endpoint}\r\n") : Ok("unauthorized\r\n");
        };

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal("TvApprovalTimeout", result.ErrorKey);
        Assert.True(delays.Count < 3, "poll steps must not stretch the limit");
    }

    [Fact]
    public async Task Install_RunnerTimeoutWhileWaiting_TvApprovalTimeout()
    {
        var (installer, runner, _) = Create();
        var states = 0;
        runner.Handler = a => a.Contains("get-state") && ++states > 1
            ? throw new TimeoutException("adb hung")
            : a[0] == "connect" ? Ok($"failed to authenticate to {Endpoint}\r\n") : Ok("unauthorized\r\n");

        var result = await installer.InstallAsync(Endpoint, new Steps(), CancellationToken.None);

        Assert.Equal("TvApprovalTimeout", result.ErrorKey);
    }
}
