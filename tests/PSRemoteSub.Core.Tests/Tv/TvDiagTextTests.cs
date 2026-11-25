using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class TvDiagTextTests
{
    private static string Fx(string device, string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tv-diag", device, file));

    private static readonly string[] Devices = ["emulator-api34", "emulator-api30"];

    public static TheoryData<string> DeviceData => new(Devices);

    private const string Sample =
        "psrs-diag-state v1\nservice.running=true\napp.version=1.1.0 (3)\nservice.connected=true\noverlay.attached=true\n" +
        "overlay.error=\ndrawing=yes\npc.connected=ba7816bf 192.168.1.5:5000\nsse.state=open\nsse.lastFailure=\n" +
        "pc.heard=ba7816bf 192.168.1.5:5000 1200\npc.heard=deadbeef 192.168.1.9:5000 900\ncount.beacons=4\n";

    // ---- PcIdHash / props / logcat / state / header

    [Fact]
    public void PcIdHash_matchesTv() => Assert.Equal("ba7816bf", TvDiagText.PcIdHash("abc"));

    [Fact]
    public void WhitelistedProps_dropsOthers()
    {
        var input = "[ro.serialno]: [SER123]\n[ro.boot.serialno]: [SER123]\n[persist.sys.wifi.ssid]: [Home]\n" +
                    "[ro.product.model]: [BRAVIA]\n[ro.build.version.sdk]: [30]\n[ro.config.low_ram]: [true]\n";
        var props = TvDiagText.WhitelistedProps(input);
        Assert.Equal(3, props.Count);
        Assert.Equal("BRAVIA", props["ro.product.model"]);
        Assert.DoesNotContain("ro.serialno", props.Keys);
    }

    [Fact]
    public void WhitelistedProps_fixtureDropsFakeSecrets()
    {
        var props = TvDiagText.WhitelistedProps(Fx("emulator-api34", "getprop.txt"));
        Assert.Equal(12, props.Count);
        Assert.DoesNotContain("ro.serialno", props.Keys);
        Assert.DoesNotContain("persist.sys.wifi.ssid", props.Keys);
        Assert.DoesNotContain(props.Values, v => v.Contains("FAKE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilterLogcat_keepsOursDropsOthers()
    {
        var logcat = string.Join('\n',
            "11-25 11:47:22.780   516   544 I ActivityManager: Start proc 4188:th.psrs.tvoverlay/u0a192 for content provider",
            "11-25 11:47:23.000  4188  4188 D Whatever: from our pid",
            "11-25 11:47:23.001  7000  7000 D PsrsTv: tagged line",
            "11-25 11:47:23.002  9999  9999 E AndroidRuntime: FATAL EXCEPTION: com.netflix.ninja",
            "11-25 11:47:23.003  9999  9999 I netflix: hello com.netflix.ninja",
            "11-25 11:47:24.000   516   567 I AppsFilter: interaction: PackageSetting{f941b70 th.psrs.tvoverlay/10192} -> PackageSetting{b0bfee9 com.netflix.ninja/10200}",
            "11-25 11:47:25.000  4188  4188 E PsrsTv: boom",
            "java.lang.IllegalStateException: x",
            "\tat th.psrs.tvoverlay.Foo.bar(Foo.kt:1)",
            "11-25 11:47:26.000  9999  9999 E Other: trace",
            "\tat com.netflix.ninja.Foo.bar(Foo.java:1)");
        var output = TvDiagText.FilterLogcat(logcat, [4188]);
        var lines = output.Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Contains("Start proc", lines[0]);
        Assert.Contains("our pid", lines[1]);
        Assert.Contains("tagged line", lines[2]);
        Assert.Contains("boom", lines[3]);
        Assert.Contains("IllegalStateException", lines[4]);
        Assert.DoesNotContain("netflix", output);
    }

    [Fact]
    public void FilterLogcat_realFixture_noForeignPackagesAndKeepsOurs()
    {
        var output = TvDiagText.FilterLogcat(Fx("emulator-api34", "logcat.txt"), [4188, 5215]);
        Assert.Contains("th.psrs.tvoverlay", output);
        Assert.DoesNotContain("PackageSetting", output);
        Assert.DoesNotContain("com.google", output);
        Assert.All(output.Split('\n'), l =>
            Assert.True(l.Contains("th.psrs.tvoverlay") || l.Contains(" 4188 ") || l.Contains(" 5215 ") || l.Contains("PsrsTv") || !char.IsDigit(l[0]), l));
    }

    [Theory]
    [InlineData("th.co.other.app")]
    [InlineData("app.foo.bar")]
    [InlineData("ru.yandex.x")]
    public void FilterLogcat_mentionLineNamingOtherPackage_isDropped(string other)
    {
        var line = $"11-25 11:47:22.780   516   544 I ActivityManager: Start proc 1:th.psrs.tvoverlay/u0a1 after {other}";
        Assert.Equal("", TvDiagText.FilterLogcat(line, []));
    }

    [Fact]
    public void FilterLogcat_plainMentionLine_isKept()
    {
        var line = "11-25 11:47:22.780   516   544 I ActivityManager: Start proc 1234:th.psrs.tvoverlay/u0a1 for service {th.psrs.tvoverlay/th.psrs.tvoverlay.OverlayA11yService}";
        Assert.Equal(line, TvDiagText.FilterLogcat(line, []));
    }

    [Theory]
    [InlineData("11-25 11:47:22.780   516   544 W BroadcastQueue: Background execution not allowed: receiving Intent { act=android.intent.action.PACKAGE_ADDED dat=package:th.psrs.tvoverlay flg=0x4000010 } to com.whatsapp/.Receiver")]
    [InlineData("11-25 11:47:22.780   516   544 W BroadcastQueue: Background execution not allowed: receiving Intent { act=android.intent.action.PACKAGE_ADDED dat=package:th.psrs.tvoverlay flg=0x4000010 } to com.Vendor.TvApp/.R")]
    [InlineData("11-25 11:47:22.780   516   544 I WindowManager: Changing focus from Window{a1b2 u0 tv.twitch/x} to Window{c3d4 u0 th.psrs.tvoverlay}")]
    [InlineData("11-25 11:47:22.780   516   544 I WindowManager: focus Window{a1b2 u0 th.psrs.tvoverlay} after _com.other.app")]
    [InlineData("11-25 11:47:22.780   516   544 I WindowManager: focus Window{a1b2 u0 th.psrs.tvoverlay} after th.psrs.tvoverlay2")]
    [InlineData("11-25 11:47:22.780   516   544 I WindowManager: focus Window{a1b2 u0 th.psrs.tvoverlay} after Netflix/.Main")]
    public void FilterLogcat_mentionLineWithForeignToken_isDropped(string line)
    {
        Assert.Equal("", TvDiagText.FilterLogcat(line, []));
    }

    [Fact]
    public void FilterLogcat_mentionLineWithOnlyOursFrameworkAndNumbers_isKept()
    {
        var line = "11-25 11:47:22.780   516   544 I ActivityManager: Start proc 4188:th.psrs.tvoverlay/u0a192 for content provider " +
                   "{th.psrs.tvoverlay/th.psrs.tvoverlay.DiagProvider} act=android.intent.action.MAIN flg=0x4000010 version 1.1.0 at 192.168.0.5 (DiagProvider.kt:12)";
        Assert.Equal(line, TvDiagText.FilterLogcat(line, []));
    }

    [Fact]
    public void FilterLogcat_realFixture_keepsStartProcLine()
    {
        var output = TvDiagText.FilterLogcat(Fx("emulator-api34", "logcat.txt"), [5215]);
        Assert.Contains("ActivityManager: Start proc 4188:th.psrs.tvoverlay/u0a192", output);
        Assert.Contains("CoreBackPreview: Window{eb8f76 u0 th.psrs.tvoverlay}", output);
    }

    [Fact]
    public void FilterLogcat_pidReuse_otherAppLinesBeforeOurStartProcAreDropped()
    {
        var logcat = string.Join('\n',
            "11-25 11:47:20.000  4188  4188 I Chatty: secret from the previous owner of 4188",
            "11-25 11:47:22.780   516   544 I ActivityManager: Start proc 4188:th.psrs.tvoverlay/u0a192 for content provider",
            "11-25 11:47:23.000  4188  4188 D Whatever: from our pid");
        var output = TvDiagText.FilterLogcat(logcat, [4188]);
        Assert.DoesNotContain("secret", output);
        Assert.Contains("from our pid", output);
    }

    [Fact]
    public void FilterLogcat_pidReuse_otherAppStartedAfterUsWithSamePid_isDropped()
    {
        var logcat = string.Join('\n',
            "11-25 11:47:22.780   516   544 I ActivityManager: Start proc 4188:th.psrs.tvoverlay/u0a192 for content provider",
            "11-25 11:47:23.000  4188  4188 D Whatever: from our pid",
            "11-25 11:47:24.000   516   544 I ActivityManager: Start proc 4188:com.other.app/u0a200 for activity",
            "11-25 11:47:25.000  4188  4188 D Chatty: secret of the new owner");
        var output = TvDiagText.FilterLogcat(logcat, []);
        Assert.Contains("from our pid", output);
        Assert.DoesNotContain("secret", output);
        Assert.DoesNotContain("com.other.app", output);
    }

    [Fact]
    public void FilterLogcat_pidLearnedFromStartProc_keepsEarlierProcessCrash()
    {
        var logcat = string.Join('\n',
            "11-25 11:47:22.780   516   544 I ActivityManager: Start proc 3000:th.psrs.tvoverlay/u0a192 for service",
            "11-25 11:47:23.000  3000  3000 E AndroidRuntime: FATAL EXCEPTION: main",
            "11-25 11:47:23.000  3000  3000 E AndroidRuntime: Process: th.psrs.tvoverlay, PID: 3000",
            "11-25 11:47:30.000   516   544 I ActivityManager: Start proc 4188:th.psrs.tvoverlay/u0a192 for service",
            "11-25 11:47:31.000  4188  4188 D Whatever: from the current process");
        var output = TvDiagText.FilterLogcat(logcat, [4188]);
        Assert.Contains("FATAL EXCEPTION", output);
        Assert.Contains("PID: 3000", output);
        Assert.Contains("from the current process", output);
    }

    [Fact]
    public void FilterLogcat_realFixture_everyPackageTokenIsOursOrFramework()
    {
        var output = TvDiagText.FilterLogcat(Fx("emulator-api34", "logcat.txt"), [4188, 5215]);
        var token = new System.Text.RegularExpressions.Regex(@"\b[a-z][a-z0-9_]*(?:\.[a-z0-9_]+){2,}\b");
        var foreign = token.Matches(output.Replace("th.psrs.tvoverlay", ""))
            .Select(m => m.Value)
            .Where(v => !new[] { "android.", "java.", "javax.", "kotlin.", "dalvik.", "libcore." }.Any(v.StartsWith))
            .ToList();
        Assert.Empty(foreign);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage\n\n\u0000")]
    public void PureFunctions_neverThrow(string? garbage)
    {
        Assert.Empty(TvDiagText.WhitelistedProps(garbage!));
        Assert.Equal("", TvDiagText.FilterLogcat(garbage!, []));
        Assert.Null(TvDiagText.ParsePackage(garbage!));
        Assert.Null(TvDiagText.ParseState(garbage));
        Assert.False(TvDiagText.HasHeader(garbage, "psrs-diag-log v1"));
        Assert.Equal((null, null), TvDiagText.SplitDumpsysService(garbage));
        _ = TvDiagText.ParseAccessibility(garbage, garbage, garbage);
        _ = TvDiagText.PcIdHash(null!);
    }

    [Fact]
    public void ParseState_requiresHeader()
    {
        Assert.Null(TvDiagText.ParseState("Error while accessing provider"));
        var state = TvDiagText.ParseState(Sample)!;
        Assert.True(state.Running);
        Assert.Equal("yes", state.Values["drawing"]);
        Assert.Equal(2, state.Heard.Count);
        Assert.False(TvDiagText.ParseState("psrs-diag-state v1\nservice.running=false\n")!.Running);
    }

    [Fact]
    public void ParseState_fixtures()
    {
        foreach (var d in Devices)
        {
            var s = TvDiagText.ParseState(Fx(d, "content-state.txt"))!;
            Assert.True(s.Running);
            Assert.Equal("no:not-connected", s.Values["drawing"]);
        }
        Assert.Null(TvDiagText.ParseState(Fx("emulator-api34-no-app", "content-state.txt")));
    }

    [Fact]
    public void HasHeader_checksFirstLine()
    {
        Assert.True(TvDiagText.HasHeader(Fx("emulator-api34", "content-log.txt"), "psrs-diag-log v1"));
        Assert.True(TvDiagText.HasHeader(Fx("emulator-api34", "content-crash.txt"), "psrs-diag-crash v1"));
        Assert.False(TvDiagText.HasHeader(Fx("emulator-api34-no-app", "content-state.txt"), "psrs-diag-state v1"));
    }

    // ---- dumpsys service split

    [Theory]
    [MemberData(nameof(DeviceData))]
    public void SplitDumpsysService_fixture(string device)
    {
        var (state, log) = TvDiagText.SplitDumpsysService(Fx(device, "dumpsys-service.txt"));
        Assert.StartsWith("psrs-diag-state v1\nservice.running=true", state);
        Assert.DoesNotContain("---", state);
        Assert.True(TvDiagText.ParseState(state)!.Running);
        Assert.NotNull(log);
        Assert.StartsWith("psrs-diag-log v1", log);
        Assert.DoesNotContain("\n    ", log.Replace("\n    \tat", "")); // dedented (stack lines keep their tab)
    }

    [Fact]
    public void SplitDumpsysService_noApp() =>
        Assert.Equal((null, null), TvDiagText.SplitDumpsysService(Fx("emulator-api34-no-app", "dumpsys-service.txt")));

    // ---- package

    [Fact]
    public void ParsePackage_api34() =>
        Assert.Equal(new TvPackageInfo(3, "1.1.0", "2026-11-25 11:47:21", "2026-11-25 11:47:21", true),
            TvDiagText.ParsePackage(Fx("emulator-api34", "dumpsys-package.txt")));

    [Fact]
    public void ParsePackage_api30() =>
        Assert.Equal(new TvPackageInfo(3, "1.1.0", "2026-11-25 11:49:01", "2026-11-25 11:49:01", true),
            TvDiagText.ParsePackage(Fx("emulator-api30", "dumpsys-package.txt")));

    [Fact]
    public void ParsePackage_notInstalled() =>
        Assert.Null(TvDiagText.ParsePackage(Fx("emulator-api34-no-app", "dumpsys-package.txt")));

    // ---- accessibility

    [Fact]
    public void ParseAccessibility_countsOthersWithoutNames()
    {
        var info = TvDiagText.ParseAccessibility("com.a/.A:th.psrs.tvoverlay/.OverlayA11yService", "1", null);
        Assert.True(info.Listed);
        Assert.Equal(1, info.OtherEnabledCount);
        Assert.DoesNotContain("com.a", info.ToString());
    }

    [Fact]
    public void ParseAccessibility_listedVariants()
    {
        Assert.True(TvDiagText.ParseAccessibility("th.psrs.tvoverlay/th.psrs.tvoverlay.OverlayA11yService", null, null).Listed);
        Assert.False(TvDiagText.ParseAccessibility("null", null, null).Listed);
        Assert.False(TvDiagText.ParseAccessibility("com.a/.A", null, null).Listed);
        Assert.Null(TvDiagText.ParseAccessibility(null, null, null).Listed);
    }

    [Theory]
    [MemberData(nameof(DeviceData))]
    public void ParseAccessibility_boundFromFixture(string device)
    {
        var info = TvDiagText.ParseAccessibility(Fx(device, "enabled-services.txt"), "1", Fx(device, "dumpsys-accessibility.txt"));
        Assert.True(info.Bound);
        Assert.True(info.Listed);
        Assert.Equal(0, info.OtherEnabledCount);
        Assert.DoesNotContain("com.google", info.ToString());
    }

    [Fact]
    public void ParseAccessibility_boundVariants()
    {
        Assert.False(TvDiagText.ParseAccessibility(null, null, "     Bound services:{}\n").Bound);
        Assert.False(TvDiagText.ParseAccessibility(null, null, "Bound services:{Service[label=TalkBack]}").Bound);
        Assert.Null(TvDiagText.ParseAccessibility(null, null, "nothing here").Bound);
    }

    // ---- summary

    private static TvDiagFacts Facts(
        bool authorized = true, string? failure = null, IReadOnlyDictionary<string, string>? props = null,
        TvPackageInfo? package = null, int? newest = 3, TvA11yInfo? a11y = null, TvAppState? state = null,
        IReadOnlyList<string>? clients = null, string? crash = null, bool crashRead = true) =>
        new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(7)), "1.2.3", "Windows 11", authorized, failure,
            props ?? new Dictionary<string, string>
            {
                ["ro.build.version.sdk"] = "30", ["ro.build.version.release"] = "11", ["ro.product.model"] = "BRAVIA 4K",
                ["ro.config.low_ram"] = "true", ["ro.build.fingerprint"] = "sony/x:11",
            },
            package ?? new TvPackageInfo(3, "1.1.0", null, null, true), newest,
            a11y ?? new TvA11yInfo(true, true, 0), state ?? TvDiagText.ParseState(Sample),
            TvDiagText.PcIdHash("abc"), clients ?? [], crash, crashRead);

    private static string Line(string summary, int n) =>
        summary.Split('\n').Single(l => l.StartsWith($"{n}. ", StringComparison.Ordinal)).TrimEnd('\r');

    private static void AssertMark(string summary, int n, char mark) =>
        Assert.StartsWith($"{n}. {mark} ", Line(summary, n));

    [Fact]
    public void Summary_allGood_ordersChecks()
    {
        var s = TvDiagText.Summary(Facts());
        var idx = Enumerable.Range(1, 8).Select(n => s.IndexOf($"\n{n}. ", StringComparison.Ordinal)).ToList();
        Assert.All(idx, i => Assert.True(i > 0));
        Assert.Equal(idx.OrderBy(i => i), idx);
        for (var n = 1; n <= 7; n++)
            AssertMark(s, n, '✔');
        Assert.Contains("1.2.3", s);
    }

    [Fact]
    public void Summary_unauthorized()
    {
        var s = TvDiagText.Summary(Facts(authorized: false, failure: "unauthorized"));
        AssertMark(s, 1, '✘');
        Assert.Contains("unauthorized", Line(s, 1));
        for (var n = 2; n <= 7; n++)
            AssertMark(s, n, '?');
    }

    [Fact]
    public void Summary_oldAndroid_check2Fails()
    {
        var props = new Dictionary<string, string> { ["ro.build.version.sdk"] = "25", ["ro.build.version.release"] = "7.1" };
        AssertMark(TvDiagText.Summary(Facts(props: props)), 2, '✘');
    }

    [Fact]
    public void Summary_outdatedApp_check3Fails()
    {
        var s = TvDiagText.Summary(Facts(package: new TvPackageInfo(2, "1.0.0", null, null, true), newest: 3));
        AssertMark(s, 3, '✘');
        Assert.Contains("3", Line(s, 3));
    }

    [Fact]
    public void Summary_notInstalled_check3Fails()
    {
        // ParsePackage returns null for "Unable to find package"
        var f = Facts() with { Package = null };
        AssertMark(TvDiagText.Summary(f), 3, '✘');
    }

    [Fact]
    public void Summary_listedNotBound_check4Fails() =>
        AssertMark(TvDiagText.Summary(Facts(a11y: new TvA11yInfo(true, false, 0))), 4, '✘');

    [Fact]
    public void Summary_overlayNotAttached_check5FailsWithReason()
    {
        var state = TvDiagText.ParseState("psrs-diag-state v1\nservice.running=true\noverlay.attached=false\noverlay.error=BadTokenException: nope\n");
        var s = TvDiagText.Summary(Facts(state: state));
        AssertMark(s, 5, '✘');
        Assert.Contains("BadTokenException: nope", Line(s, 5));
    }

    [Fact]
    public void Summary_noState_check5And7Unknown()
    {
        var s = TvDiagText.Summary(Facts() with { State = null, CrashChannelRead = false });
        AssertMark(s, 5, '?');
        AssertMark(s, 7, '?');
    }

    [Fact]
    public void Summary_crashShownWithoutState()
    {
        var s = TvDiagText.Summary(Facts(crash: "time=2026-11-25T04:47:42.160Z thread=main") with { State = null });
        AssertMark(s, 7, '✘');
        Assert.Contains("2026-11-25T04:47:42.160Z", Line(s, 7));
        AssertMark(TvDiagText.Summary(Facts() with { State = null }), 7, '✔'); // crash channel said "none"
    }

    [Fact]
    public void Summary_connectionCheck6()
    {
        AssertMark(TvDiagText.Summary(Facts()), 6, '✔');
        var other = TvDiagText.ParseState("psrs-diag-state v1\nservice.running=true\noverlay.attached=true\npc.connected=none\nsse.state=failed\nsse.lastFailure=timeout\n");
        var s = TvDiagText.Summary(Facts(state: other));
        AssertMark(s, 6, '✘');
        Assert.Contains("timeout", Line(s, 6));
        AssertMark(TvDiagText.Summary(Facts(state: other, clients: ["192.168.1.7"])), 6, '?');
    }

    [Fact]
    public void Summary_crash_check7FailsWithTime()
    {
        var s = TvDiagText.Summary(Facts(crash: "time=2026-11-25T04:47:42.160Z thread=main"));
        AssertMark(s, 7, '✘');
        Assert.Contains("2026-11-25T04:47:42.160Z", Line(s, 7));
    }

    [Fact]
    public void Summary_check8_infoWithoutMark()
    {
        var line = Line(TvDiagText.Summary(Facts()), 8);
        Assert.Contains("low_ram true", line);
        Assert.Contains("BRAVIA 4K", line);
        Assert.DoesNotContain("✔", line);
        Assert.DoesNotContain("✘", line);
        Assert.DoesNotMatch(@"^8\. \?", line);
    }

    [Fact]
    public void Summary_realApi34Fixture_endToEnd()
    {
        var f = new TvDiagFacts(DateTimeOffset.UnixEpoch, "dev", "macOS", true, null,
            TvDiagText.WhitelistedProps(Fx("emulator-api34", "getprop.txt")),
            TvDiagText.ParsePackage(Fx("emulator-api34", "dumpsys-package.txt")), 3,
            TvDiagText.ParseAccessibility(Fx("emulator-api34", "enabled-services.txt"), "1", Fx("emulator-api34", "dumpsys-accessibility.txt")),
            TvDiagText.ParseState(Fx("emulator-api34", "content-state.txt")), TvDiagText.PcIdHash("abc"), [],
            "time=2026-11-25T04:47:42.160Z thread=main");
        var s = TvDiagText.Summary(f);
        AssertMark(s, 1, '✔'); AssertMark(s, 2, '✔'); AssertMark(s, 3, '✔'); AssertMark(s, 4, '✔'); AssertMark(s, 5, '✔');
        AssertMark(s, 6, '✘'); AssertMark(s, 7, '✘');
        Assert.DoesNotContain("EMULATOR-FAKE-SERIAL", s);
        Assert.DoesNotContain("FakeWifi", s);
        Assert.DoesNotContain("youtube", s);
    }
}
