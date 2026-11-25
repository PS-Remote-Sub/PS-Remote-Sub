using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class AdbDiagnosticsCommandsTests
{
    private const string Serial = "192.168.0.33:5555";

    private static (AdbClient Adb, FakeAdbRunner Runner) Create(ProcessResult? result = null)
    {
        var runner = new FakeAdbRunner();
        if (result is { } r)
            runner.Handler = _ => r;
        return (new AdbClient("adb.exe", runner), runner);
    }

    public static TheoryData<string, Func<AdbClient, Task<string?>>, string[]> Commands => new()
    {
        { "getprop", a => a.GetPropsAsync(Serial), ["shell", "getprop"] },
        { "dumpsys package", a => a.DumpsysPackageAsync(Serial), ["shell", "dumpsys", "package", "th.psrs.tvoverlay"] },
        { "dumpsys accessibility", a => a.DumpsysAccessibilityAsync(Serial), ["shell", "dumpsys", "accessibility"] },
        { "content log", a => a.ContentReadAsync(Serial, TvDiagChannel.Log), ["shell", "content", "read", "--uri", "content://th.psrs.tvoverlay.diag/log"] },
        { "content state", a => a.ContentReadAsync(Serial, TvDiagChannel.State), ["shell", "content", "read", "--uri", "content://th.psrs.tvoverlay.diag/state"] },
        { "content crash", a => a.ContentReadAsync(Serial, TvDiagChannel.Crash), ["shell", "content", "read", "--uri", "content://th.psrs.tvoverlay.diag/crash"] },
        { "dumpsys service", a => a.DumpsysServiceAsync(Serial), ["shell", "dumpsys", "activity", "service", "th.psrs.tvoverlay/.OverlayA11yService"] },
        { "logcat", a => a.LogcatAsync(Serial), ["shell", "logcat", "-d", "-v", "threadtime", "-b", "main,system,crash"] },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Command_RunsExactArguments_ReturnsStdOut(string name, Func<AdbClient, Task<string?>> run, string[] tail)
    {
        _ = name;
        var (adb, runner) = Create(new ProcessResult(0, "out\n", ""));

        var text = await run(adb);

        Assert.Equal("out\n", text);
        Assert.Equal(["-s", Serial, .. tail], runner.Calls.Single());
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Command_NonZeroExit_ReturnsNull(string name, Func<AdbClient, Task<string?>> run, string[] tail)
    {
        _ = name; _ = tail;
        var (adb, _) = Create(new ProcessResult(1, "partial", "error"));

        Assert.Null(await run(adb));
    }

    [Fact]
    public async Task Logcat_UsesThirtySecondTimeout_OthersQuickTimeout()
    {
        var (adb, runner) = Create();

        await adb.LogcatAsync(Serial);
        await adb.GetPropsAsync(Serial);

        Assert.Equal(TimeSpan.FromSeconds(30), runner.Timeouts[0]);
        Assert.Equal(TimeSpan.FromSeconds(15), runner.Timeouts[1]);
    }

    [Fact]
    public async Task ContentRead_ProviderErrorOnExitZero_ReturnsTextForCallerToJudge()
    {
        const string error = "Error while accessing provider:th.psrs.tvoverlay.diag\n";
        var (adb, _) = Create(new ProcessResult(0, error, ""));

        Assert.Equal(error, await adb.ContentReadAsync(Serial, TvDiagChannel.State));
    }

    [Fact]
    public async Task Pids_ParsesWhitespaceSeparatedIntegers()
    {
        var (adb, runner) = Create(new ProcessResult(0, "1234 5678\n", ""));

        Assert.Equal([1234, 5678], await adb.PidsAsync(Serial));
        Assert.Equal(["-s", Serial, "shell", "pidof", "th.psrs.tvoverlay"], runner.Calls.Single());
    }

    [Theory]
    [InlineData("", 0, new int[0])]
    [InlineData("\r\n", 0, new int[0])]
    [InlineData("12 abc 34\r\n", 0, new[] { 12, 34 })]
    [InlineData("1234\n", 1, new int[0])]
    public async Task Pids_EmptyOrUnparsable(string stdout, int exit, int[] expected)
    {
        var (adb, _) = Create(new ProcessResult(exit, stdout, ""));

        Assert.Equal(expected, await adb.PidsAsync(Serial));
    }

    [Fact]
    public void Constants()
    {
        Assert.Equal("th.psrs.tvoverlay.diag", AdbClient.DiagAuthority);
        Assert.Equal("th.psrs.tvoverlay/.OverlayA11yService", AdbClient.TvServiceComponent);
    }
}
