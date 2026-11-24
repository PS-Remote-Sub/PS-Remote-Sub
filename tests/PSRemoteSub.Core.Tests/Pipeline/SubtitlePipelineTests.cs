using Microsoft.Extensions.Time.Testing;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Tests.Pipeline.Fakes;
using PSRemoteSub.Core.Text;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Tests.Pipeline;

public class SubtitlePipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-pipeline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string A = "Hello there";
    private const string B = "Where are you going";
    private const string C = "See you tomorrow";

    private static readonly RegionFraction Region = new(0, 0.5, 1, 0.5);
    private static readonly WindowInfo Window1 = new(1, 100, new PixelRect(0, 0, 40, 20), false);
    private static readonly WindowInfo Window2 = new(2, 100, new PixelRect(0, 0, 40, 20), false);

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        public FakeWindowWatcher Watcher { get; } = new();
        public FakeFrameSource Frames { get; } = new();
        public ScriptedOcr Ocr { get; } = new();
        /// <summary>The AI behind the default source: translates <c>x</c> to <c>th:x</c>.</summary>
        public FakeAiTranslator Ai { get; } = new() { Map = r => "th:" + r.Text };
        public FakeAiTranslatorSource AiSource { get; }
        public SubtitleHub Hub { get; }
        public SubtitlePipeline Pipeline { get; }
        public AiRefiner Refiner { get; }
        public List<HubEvent> Events { get; } = [];
        public List<(string Profile, string Name, string? Thai, string? Color)> Learned { get; } = [];

        private void OnLearned(string profile, string name, SpeakerInfo info)
        {
            lock (Learned) Learned.Add((profile, name, info.Thai, info.Color));
        }

        /// <param name="aiSource">What the refiner reads; by default a usable handle over <see cref="Ai"/>.</param>
        public Rig(PipelineConfig? config = null, WindowInfo? window = null, bool withWindow = true,
            FakeAiTranslatorSource? aiSource = null, LibraryHolder? library = null)
        {
            Hub = new SubtitleHub(Time);
            Hub.Changed += (_, e) => { lock (Events) Events.Add(e); };
            AiSource = aiSource ?? new FakeAiTranslatorSource(new AiTranslatorHandle(Ai, null, 1));
            // Late-bound: the refiner reports status through the pipeline created just below.
            SubtitlePipeline? pipeline = null;
            Refiner = new AiRefiner(Hub, AiSource, Time, change => pipeline?.UpdateAiStatus(change));
            Pipeline = pipeline = new SubtitlePipeline(Watcher, Frames, Ocr, Hub, Time, PipelineOptions.Default,
                Refiner, OnLearned, library);
            Pipeline.UpdateConfig(config ?? Config());
            if (withWindow)
                Watcher.Raise(window ?? Window1);
        }

        /// <summary>The migrated dialogue zone over the bottom half.</summary>
        public static ZoneConfig Dialogue { get; } = new("z1", "บทพูด", Region, PreprocessFilter.Normal);

        public static PipelineConfig Config(RegionFraction? region = null, bool paused = false) =>
            ConfigOf(Dialogue with { Region = region ?? Region }) with { Paused = paused };

        public static PipelineConfig ConfigOf(params ZoneConfig[] zones) =>
            new(zones, SourceLanguages.Get("en"), "th", false);

        public static Frame NewFrame() => new(40, 20, new byte[40 * 20 * 4]);

        /// <summary>Advance 300 ms, deliver a fresh frame, run one tick.</summary>
        public async Task TickAsync(bool freshFrame = true)
        {
            Time.Advance(TimeSpan.FromMilliseconds(300));
            if (freshFrame)
                Frames.Push(NewFrame(), Time.GetUtcNow());
            await Pipeline.TickAsync(CancellationToken.None);
        }

        /// <summary>Feeds each OCR text through one tick.</summary>
        public async Task ReadAsync(params string[] texts)
        {
            Ocr.Returns(texts);
            foreach (var _ in texts)
                await TickAsync();
        }

        public IReadOnlyList<HubEvent> EventsSnapshot() { lock (Events) return [.. Events]; }

        /// <summary>Waits for the name lookups, then for the AI lane.</summary>
        public async Task WhenAllIdleAsync()
        {
            await Pipeline.WhenNamesIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Refiner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            Pipeline.Dispose();
            Refiner.Dispose();
        }
    }

    private static FakeAiTranslatorSource AiSource(FakeAiTranslator? ai, string? configError = null) =>
        new(new AiTranslatorHandle(configError is null ? ai : null, configError, 1));

    [Fact]
    public async Task Pipeline_StableLine_CarriesOcrBounds()
    {
        using var rig = new Rig();
        // Frame 40x20, region = bottom half, crop 40x10 upscaled 2x for OCR.
        rig.Ocr.ReturnsAt(A, new PixelRect(20, 4, 40, 12)).ReturnsAt(A, new PixelRect(20, 4, 40, 12));

        await rig.TickAsync();
        await rig.TickAsync();
        await rig.WhenAllIdleAsync();

        var entry = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal(new RegionFraction(0.15, 0.4, 0.7, 0.6), entry.Bounds);
        // One line, one row: its English line is the whole text.
        Assert.Equal([new RegionFraction(0.15, 0.4, 0.7, 0.6)], entry.RowBounds!);
    }

    [Fact]
    public async Task StableText_IsTranslatedAndPublished()
    {
        using var rig = new Rig();
        rig.Ai.Map = _ => "สวัสดี";

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        var entry = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal((A, "สวัสดี", "en", (string?)null, false),
            (entry.Source, entry.Translated, entry.SourceLang, entry.Error, entry.AwaitingAi));
        Assert.Equal([A], rig.Ai.Texts);
        Assert.Equal("en-US", rig.Ocr.LastTag);
    }

    [Fact]
    public async Task Translator_ReceivesNormalizedText()
    {
        using var rig = new Rig();

        await rig.ReadAsync("  Hello   |  there ", "Hello there");
        await rig.WhenAllIdleAsync();

        Assert.Equal(["Hello there"], rig.Ai.Texts);
        Assert.Equal("Hello there", Assert.Single(rig.Hub.GetHistory()).Source);
    }

    [Fact]
    public async Task StableLine_PublishedPending_ThenAiFillsIt()
    {
        using var rig = new Rig(Rig.Config() with { GameTitle = "Astro Bot" });
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        await rig.ReadAsync(A, A);
        await rig.Ai.WaitForCallsAsync(1);

        var entry = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal(((string?)null, true, (string?)null), (entry.Translated, entry.AwaitingAi, entry.Error));

        gate.SetResult();
        await rig.WhenAllIdleAsync();

        entry = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal(("th:" + A, false), (entry.Translated, entry.AwaitingAi));
        var request = Assert.Single(rig.Ai.Requests);
        Assert.Equal((A, "English", "Thai", "Astro Bot"),
            (request.Text, request.SourceLanguageName, request.TargetLanguageName, request.GameTitle));
        Assert.Contains(rig.EventsSnapshot(), e => e is SubtitleRefined r && r.Entry.Id == entry.Id);
    }

    [Fact]
    public async Task Queue_PublishesEveryLine_AiTranslatesEachInOrder()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        await rig.ReadAsync(A, A, B, B, C, C);
        Assert.Equal([A, B, C], rig.Hub.GetHistory().Select(e => e.Source));
        Assert.All(rig.Hub.GetHistory(), e => Assert.Null(e.Translated));

        gate.SetResult();
        await rig.WhenAllIdleAsync();

        // B came while A was being translated: it waits its turn, so every line is translated, in order.
        Assert.Equal(["th:" + A, "th:" + B, "th:" + C], rig.Hub.GetHistory().Select(e => e.Translated));
        Assert.All(rig.Hub.GetHistory(), e => Assert.False(e.AwaitingAi));
        Assert.Equal([A, B, C], rig.Ai.Texts);
    }

    [Fact]
    public async Task Pipeline_NoAiConfigured_LineSettlesUntranslated_ConfigErrorInStatus()
    {
        using var rig = new Rig(aiSource: AiSource(null, "AiKeyNeeded"));

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        var entry = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal((A, (string?)null, false, (string?)null), (entry.Source, entry.Translated, entry.AwaitingAi, entry.Error));
        Assert.Equal("AiKeyNeeded", rig.Hub.Status.AiError);
        Assert.Null(rig.Hub.Status.AiRetryAt);
    }

    [Fact]
    public async Task Pipeline_AiFails_LineSettlesUntranslated_AndLinesDuringThePauseToo()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Unavailable, 503));

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        var entry = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal(((string?)null, false, A), (entry.Translated, entry.AwaitingAi, entry.Source));
        Assert.Equal("AiDown", rig.Hub.Status.AiError);

        // Paused: the next line is published as not awaiting and never sent.
        await rig.ReadAsync(B, B);
        await rig.WhenAllIdleAsync();
        entry = rig.Hub.GetHistory()[^1];
        Assert.Equal((B, (string?)null, false), (entry.Source, entry.Translated, entry.AwaitingAi));
        Assert.Single(rig.Ai.Requests);
    }

    [Fact]
    public async Task Pipeline_AiFailure_SetsStatus_ViaPipeline()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.RateLimited, 429));

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        Assert.Equal(("AiRateLimited", (DateTimeOffset?)rig.Time.GetUtcNow().AddSeconds(60)),
            (rig.Hub.Status.AiError, rig.Hub.Status.AiRetryAt));
        Assert.True(rig.Hub.Status.RemotePlayFound); // merged into the pipeline's status, not replacing it

        await rig.TickAsync(); // a tick's status update keeps the AI fields
        Assert.Equal("AiRateLimited", rig.Hub.Status.AiError);
    }

    [Fact]
    public async Task NoRegion_SetsStatus_NoOcrCalls()
    {
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue with { Region = null }));

        await rig.TickAsync();
        await rig.TickAsync();

        Assert.Equal(0, rig.Ocr.Calls);
        Assert.True(rig.Hub.Status.NoRegion);
        Assert.True(rig.Hub.Status.RemotePlayFound);
        Assert.False(rig.Hub.Status.Paused);
    }

    [Fact]
    public async Task Paused_SetsStatus_NoOcrCalls_AndResumes()
    {
        using var rig = new Rig(Rig.Config(paused: true));

        await rig.TickAsync();
        Assert.Equal(0, rig.Ocr.Calls);
        Assert.True(rig.Hub.Status.Paused);

        rig.Pipeline.UpdateConfig(Rig.Config(paused: false));
        await rig.TickAsync();
        Assert.Equal(1, rig.Ocr.Calls);
        Assert.False(rig.Hub.Status.Paused);
    }

    [Fact]
    public async Task NoFrameAfterCaptureStart_SetsFramesStalled_After20s()
    {
        using var rig = new Rig();

        await rig.TickAsync(freshFrame: false); // capture starts now (T)
        rig.Time.Advance(TimeSpan.FromMilliseconds(19_400));
        await rig.TickAsync(freshFrame: false); // T + 19.7 s
        Assert.False(rig.Hub.Status.FramesStalled);
        await rig.TickAsync(freshFrame: false); // T + 20.0 s
        Assert.True(rig.Hub.Status.FramesStalled);
        Assert.Equal(0, rig.Ocr.Calls);

        // A fresh frame recovers.
        await rig.TickAsync();
        Assert.False(rig.Hub.Status.FramesStalled);
        Assert.Equal(1, rig.Ocr.Calls);
    }

    [Fact]
    public async Task StaticFrame_For10s_NoStall_AndOcrNotRerun()
    {
        using var rig = new Rig();

        await rig.TickAsync(); // WGC delivers one frame, then nothing while the scene stays still
        for (var i = 0; i < 34; i++)
            await rig.TickAsync(freshFrame: false); // + 10.2 s

        Assert.False(rig.Hub.Status.FramesStalled);
        Assert.Equal(1, rig.Ocr.Calls);
    }

    [Fact]
    public async Task FrameAge20s_SetsFramesStalled_AndFreshFrameRecovers()
    {
        using var rig = new Rig();

        await rig.TickAsync(); // frame at T
        rig.Time.Advance(TimeSpan.FromMilliseconds(19_400));
        await rig.TickAsync(freshFrame: false); // T + 19.7 s
        Assert.False(rig.Hub.Status.FramesStalled);
        await rig.TickAsync(freshFrame: false); // T + 20.0 s
        Assert.True(rig.Hub.Status.FramesStalled);
        Assert.Equal(1, rig.Ocr.Calls);

        await rig.TickAsync();
        Assert.False(rig.Hub.Status.FramesStalled);
        Assert.Equal(2, rig.Ocr.Calls);
    }

    [Fact]
    public async Task StaticFrame_StillStabilizesAndTranslates()
    {
        using var rig = new Rig();
        rig.Ocr.Returns(A);

        await rig.TickAsync();
        await rig.TickAsync(freshFrame: false); // same frame: the earlier read counts again
        await rig.WhenAllIdleAsync();

        var entry = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal((A, "th:" + A), (entry.Source, entry.Translated));
        Assert.Equal(1, rig.Ocr.Calls);
    }

    [Fact]
    public async Task StaticFrame_WithoutSubtitle_StillClears()
    {
        using var rig = new Rig();
        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        rig.Ocr.Returns("");
        await rig.TickAsync(); // the subtitle leaves, then the scene stays still
        for (var i = 0; i < 5; i++)
            await rig.TickAsync(freshFrame: false);

        Assert.IsType<SubtitleCleared>(rig.EventsSnapshot()[^1]);
        Assert.Equal(3, rig.Ocr.Calls);
    }

    [Fact]
    public async Task UnchangedFrame_IsReadAgain_AfterFilterChange()
    {
        using var rig = new Rig();
        await rig.TickAsync();
        await rig.TickAsync(freshFrame: false);
        Assert.Equal(1, rig.Ocr.Calls);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with { Filter = PreprocessFilter.WhiteText }));
        await rig.TickAsync(freshFrame: false);
        Assert.Equal(2, rig.Ocr.Calls);
    }

    [Fact]
    public async Task RestoreAfterMinimize_NoImmediateFramesStalled()
    {
        using var rig = new Rig();
        await rig.TickAsync(); // frame at T

        rig.Watcher.Raise(Window1 with { IsMinimized = true });
        rig.Time.Advance(TimeSpan.FromSeconds(60));
        await rig.TickAsync(freshFrame: false);
        Assert.True(rig.Hub.Status.Minimized);
        Assert.False(rig.Hub.Status.FramesStalled);

        rig.Watcher.Raise(Window1);
        await rig.TickAsync(freshFrame: false); // the last frame is a minute old
        Assert.False(rig.Hub.Status.Minimized);
        Assert.False(rig.Hub.Status.FramesStalled);

        // The backstop still works once restored: 20 s without any new frame.
        rig.Time.Advance(TimeSpan.FromMilliseconds(19_700));
        await rig.TickAsync(freshFrame: false);
        Assert.True(rig.Hub.Status.FramesStalled);
    }

    [Fact]
    public async Task ResumeAfterPause_NoImmediateFramesStalled()
    {
        using var rig = new Rig();
        await rig.TickAsync(); // frame at T

        rig.Pipeline.UpdateConfig(Rig.Config(paused: true));
        rig.Time.Advance(TimeSpan.FromSeconds(60));
        await rig.TickAsync(freshFrame: false);
        Assert.True(rig.Hub.Status.Paused);

        rig.Pipeline.UpdateConfig(Rig.Config(paused: false));
        await rig.TickAsync(freshFrame: false);
        Assert.False(rig.Hub.Status.Paused);
        Assert.False(rig.Hub.Status.FramesStalled);
    }

    [Fact]
    public async Task OcrLanguageMissing_SetsOcrUnavailable_NoOcrCalls()
    {
        using var rig = new Rig();
        rig.Ocr.Available = false;

        await rig.TickAsync();
        await rig.TickAsync();
        Assert.True(rig.Hub.Status.OcrUnavailable);
        Assert.False(rig.Hub.Status.FramesStalled);
        Assert.Equal(0, rig.Ocr.Calls);

        // Installed while running: reading starts on the next tick.
        rig.Ocr.Available = true;
        await rig.TickAsync();
        Assert.False(rig.Hub.Status.OcrUnavailable);
        Assert.Equal(1, rig.Ocr.Calls);
    }

    [Fact]
    public async Task WindowReplaced_RestartsFrameSource()
    {
        using var rig = new Rig(withWindow: false);

        rig.Watcher.Raise(Window1);
        await rig.TickAsync();
        Assert.Equal(["Stop", "Start:1"], rig.Frames.Calls);
        Assert.True(rig.Hub.Status.RemotePlayFound);

        // Same handle (moved / resized): nothing.
        rig.Watcher.Raise(Window1 with { ClientScreenRect = new PixelRect(5, 5, 40, 20) });
        await rig.TickAsync();
        Assert.Equal(["Stop", "Start:1"], rig.Frames.Calls);

        rig.Watcher.Raise(null);
        await rig.TickAsync();
        Assert.Equal(["Stop", "Start:1", "Stop"], rig.Frames.Calls);
        Assert.False(rig.Hub.Status.RemotePlayFound);

        rig.Watcher.Raise(Window2);
        await rig.TickAsync();
        Assert.Equal(["Stop", "Start:1", "Stop", "Stop", "Start:2"], rig.Frames.Calls);
        Assert.True(rig.Hub.Status.RemotePlayFound);
    }

    [Fact]
    public async Task WindowFoundBeforePipelineCreated_StartsFrameSource()
    {
        var watcher = new FakeWindowWatcher();
        watcher.Raise(Window1);
        var frames = new FakeFrameSource();
        var time = new FakeTimeProvider();
        var hub = new SubtitleHub(time);
        using var refiner = new AiRefiner(hub, AiSource(new FakeAiTranslator()), time, _ => { });
        using var pipeline = new SubtitlePipeline(watcher, frames, new ScriptedOcr(), hub, time,
            PipelineOptions.Default, refiner);

        await pipeline.TickAsync(CancellationToken.None);

        Assert.Equal(["Stop", "Start:1"], frames.Calls);
    }

    [Fact]
    public void Constructor_RequiresTheRefiner()
    {
        var time = new FakeTimeProvider();
        Assert.Throws<ArgumentNullException>(() => new SubtitlePipeline(new FakeWindowWatcher(), new FakeFrameSource(),
            new ScriptedOcr(), new SubtitleHub(time), time, PipelineOptions.Default, null!));
    }

    [Fact]
    public async Task FrameSourceStartFailure_IsRetriedOnNextTick()
    {
        using var rig = new Rig(withWindow: false);
        rig.Frames.ThrowOnStart = new InvalidOperationException("capture failed");

        rig.Watcher.Raise(Window1);
        await rig.TickAsync(freshFrame: false);
        Assert.Equal(["Stop", "Start:1"], rig.Frames.Calls);

        rig.Frames.ThrowOnStart = null;
        await rig.TickAsync(freshFrame: false);
        Assert.Equal(["Stop", "Start:1", "Stop", "Start:1"], rig.Frames.Calls);
    }

    [Fact]
    public async Task Minimized_SkipsOcr()
    {
        using var rig = new Rig(window: Window1 with { IsMinimized = true });

        await rig.TickAsync();
        await rig.TickAsync();
        Assert.Equal(0, rig.Ocr.Calls);
        Assert.True(rig.Hub.Status.RemotePlayFound);
        Assert.True(rig.Hub.Status.Minimized);
        Assert.False(rig.Hub.Status.FramesStalled);

        rig.Watcher.Raise(Window1);
        await rig.TickAsync();
        Assert.Equal(1, rig.Ocr.Calls);
        Assert.False(rig.Hub.Status.Minimized);
    }

    [Fact]
    public async Task Cleared_ClearsHub()
    {
        using var rig = new Rig();
        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        Assert.DoesNotContain(rig.EventsSnapshot(), e => e is SubtitleCleared);

        // No subtitle for 1.5 s (first empty read starts the clock).
        await rig.ReadAsync("", "", "", "", "", "");
        Assert.IsType<SubtitleCleared>(rig.EventsSnapshot()[^1]);
        Assert.Single(rig.Hub.GetHistory());
    }

    [Fact]
    public async Task ClearedWhileTranslating_LateAnswerOnlyRefinesTheLine()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        await rig.ReadAsync(A, A, "", "", "", "", "", "");
        var before = rig.EventsSnapshot();
        Assert.IsType<SubtitleCleared>(before[^1]);
        var published = Assert.Single(before.OfType<SubtitlePublished>()).Entry;
        Assert.True(published.AwaitingAi);

        gate.SetResult();
        await rig.WhenAllIdleAsync();

        // The answer refines the line in place; nothing publishes it again, so the overlay and the page (which keep a
        // cleared line hidden) do not bring it back.
        var after = rig.EventsSnapshot().Skip(before.Count)
            .Where(e => e is SubtitlePublished or SubtitleRefined or SubtitleCleared).ToList();
        var refined = Assert.IsType<SubtitleRefined>(Assert.Single(after)).Entry;
        Assert.Equal((published.Id, "th:" + A), (refined.Id, refined.Translated));
    }

    [Fact]
    public async Task OcrException_SkipsTick_AndNextTickWorks()
    {
        using var rig = new Rig();
        rig.Ocr.Throws(new InvalidOperationException("ocr failed"));

        await rig.TickAsync();
        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        Assert.Equal(A, Assert.Single(rig.Hub.GetHistory()).Source);
    }

    [Fact]
    public async Task UpdateConfig_RegionChange_ResetsStabilityFilter()
    {
        using var rig = new Rig();

        await rig.ReadAsync(A);
        rig.Pipeline.UpdateConfig(Rig.Config(new RegionFraction(0, 0, 1, 0.5)));
        await rig.ReadAsync(A);
        await rig.WhenAllIdleAsync();
        Assert.Empty(rig.Hub.GetHistory());

        await rig.ReadAsync(A);
        await rig.WhenAllIdleAsync();
        Assert.Single(rig.Hub.GetHistory());
    }

    [Fact]
    public async Task UpdateConfig_FilterChange_KeepsStabilityFilter()
    {
        using var rig = new Rig();

        await rig.ReadAsync(A);
        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with { Filter = PreprocessFilter.WhiteText }));
        await rig.ReadAsync(A);
        await rig.WhenAllIdleAsync();

        Assert.Single(rig.Hub.GetHistory());
    }

    [Fact]
    public async Task LastCapture_HoldsRawCropAndProcessedImage()
    {
        using var rig = new Rig();
        Assert.Null(rig.Pipeline.LastCapture);

        await rig.ReadAsync(A);

        var (raw, processed) = rig.Pipeline.LastCapture!.Value;
        Assert.Equal((40, 10), (raw.Width, raw.Height)); // bottom half of 40x20
        Assert.Same(processed, rig.Ocr.LastImage);
    }

    [Fact]
    public async Task RunAsync_TicksOnTimer_AndSurvivesOcrFault()
    {
        using var rig = new Rig();
        rig.Frames.Push(Rig.NewFrame(), rig.Time.GetUtcNow());
        rig.Ocr.Throws(new InvalidOperationException("ocr failed")).Returns(A);
        using var cts = new CancellationTokenSource();

        var run = rig.Pipeline.RunAsync(cts.Token);
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        await rig.Ocr.WaitForCallsAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        await rig.Ocr.WaitForCallsAsync(2).WaitAsync(TimeSpan.FromSeconds(10));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Pipeline_GameTitleChange_ResetsContext()
    {
        using var rig = new Rig();

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        await rig.ReadAsync(B, B);
        await rig.WhenAllIdleAsync();
        Assert.Equal([A], rig.Ai.Requests[1].Context.Select(c => c.Source));

        rig.Pipeline.UpdateConfig(Rig.Config() with { GameTitle = "Other game" });
        await rig.ReadAsync(C, C);
        await rig.WhenAllIdleAsync();

        Assert.Empty(rig.Ai.Requests[2].Context);
        Assert.Equal("Other game", rig.Ai.Requests[2].GameTitle);
    }

    [Fact]
    public async Task Pipeline_ProfileChange_ResetsContext()
    {
        using var rig = new Rig(Rig.Config() with { ProfileName = "Game A" });

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        await rig.ReadAsync(B, B);
        await rig.WhenAllIdleAsync();
        Assert.Equal([A], rig.Ai.Requests[1].Context.Select(c => c.Source));

        // Same language, target and (empty) game title: only the profile differs.
        rig.Pipeline.UpdateConfig(Rig.Config() with { ProfileName = "Game B" });
        await rig.ReadAsync(C, C);
        await rig.WhenAllIdleAsync();

        Assert.Empty(rig.Ai.Requests[2].Context);
    }

    [Fact]
    public async Task Pipeline_TargetLanguageChange_ResetsContext()
    {
        using var rig = new Rig();

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        rig.Pipeline.UpdateConfig(Rig.Config() with { TargetLang = "en" });
        await rig.ReadAsync(B, B);
        await rig.WhenAllIdleAsync();

        Assert.Empty(rig.Ai.Requests[1].Context);
        Assert.Equal("English", rig.Ai.Requests[1].TargetLanguageName);
    }

    [Fact]
    public async Task Pipeline_UnrelatedConfigChange_KeepsContext()
    {
        using var rig = new Rig();

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with { Filter = PreprocessFilter.WhiteText }) with { ContextLines = 1 });
        await rig.ReadAsync(B, B);
        await rig.WhenAllIdleAsync();

        Assert.Equal([A], rig.Ai.Requests[1].Context.Select(c => c.Source));
    }

    [Fact]
    public void Dispose_UnsubscribesFromWatcher()
    {
        var rig = new Rig();
        Assert.True(rig.Watcher.HasSubscribers);
        rig.Dispose();
        Assert.False(rig.Watcher.HasSubscribers);
    }

    // ---- Speakers ----

    private const string L1 = "Lucia: Where are you going?", J1 = "Jason: Just out for a bit.";
    private const string L2 = "Lucia: Wait for me at the car.", J2 = "Jason: Fine, but hurry up then.";
    private const string L3 = "Lucia: See you at the docks tonight.", L4 = "Lucia: Nobody tells me anything here.";

    /// <summary>Two stable reads of the same speaker lines, then waits for the name lookups and the AI lane.</summary>
    private static async Task ReadLinesAsync(Rig rig, params string[] lines)
    {
        rig.Ocr.ReturnsLines(lines).ReturnsLines(lines);
        await rig.TickAsync();
        await rig.TickAsync();
        await rig.WhenAllIdleAsync();
    }

    private static async Task ReadRawAsync(Rig rig, OcrLine line)
    {
        rig.Ocr.ReturnsRaw(line).ReturnsRaw(line);
        await rig.TickAsync();
        await rig.TickAsync();
        await rig.WhenAllIdleAsync();
    }

    private static int NameRequests(Rig rig, string names) =>
        rig.Ai.Requests.Count(r => r.Kind == AiRequestKind.Names && r.Text == names);

    [Fact]
    public async Task SpeakerLinesOnly_SignAboveDialogue_OnlyTheSpeakerIsTranslated()
    {
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue with { SpeakerLinesOnly = true }));
        await ReadLinesAsync(rig, "EVERYDAY DREICH", L1);

        var e = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal(L1, e.Source);
        Assert.Equal(["Where are you going?"], rig.Ai.Texts);
        Assert.DoesNotContain(rig.Ai.Texts, t => t.Contains("DREICH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SpeakerLinesOnly_NoName_NotPublishedNotTranslated()
    {
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue with { SpeakerLinesOnly = true }));
        await ReadLinesAsync(rig, "CHIPS DREICH");

        Assert.Empty(rig.Hub.GetHistory());
        Assert.Empty(rig.Ai.Requests);
    }

    [Fact]
    public async Task SpeakerLinesOnly_Off_NoNameLineStillTranslated()
    {
        using var rig = new Rig();
        await ReadLinesAsync(rig, "We have to leave before sunset.");

        Assert.Equal(["We have to leave before sunset."], rig.Ai.Texts);
    }

    [Fact]
    public async Task Pipeline_NewName_FirstSighting_OriginalName_NotTranslated()
    {
        using var rig = new Rig();
        await ReadLinesAsync(rig, L1);

        var e = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal([new SpeakerTag("Lucia", SpeakerPalette.For("Lucia"))], e.Speakers);
        Assert.Equal(["Where are you going?"], rig.Ai.Texts);
        lock (rig.Learned) Assert.Empty(rig.Learned);
    }

    [Fact]
    public async Task Pipeline_TwoSpeakers_SecondSighting_NamesOnce_DialogueOnly()
    {
        using var rig = new Rig();
        rig.Ai.Map = r => r.Text switch
        {
            "Lucia\nJason" => "ลูเซีย\nเจสัน",
            "Wait for me at the car.\nFine, but hurry up then." => "รอที่รถนะ\nได้ แต่รีบหน่อย",
            _ => "th:" + r.Text,
        };

        await ReadLinesAsync(rig, L1, J1);
        await ReadLinesAsync(rig, L2, J2);
        await ReadLinesAsync(rig, L1, J1); // the names learned on the second sighting show from here at the latest

        var e = rig.Hub.GetHistory()[^2];
        Assert.Equal("Lucia: Wait for me at the car.\nJason: Fine, but hurry up then.", e.Source);
        Assert.Equal("Wait for me at the car.\nFine, but hurry up then.", e.Dialogue);
        Assert.Equal("รอที่รถนะ\nได้ แต่รีบหน่อย", e.Translated);

        var last = rig.Hub.GetHistory()[^1];
        Assert.Equal(["ลูเซีย", "เจสัน"], last.Speakers!.Select(s => s!.Name));
        Assert.NotEqual(last.Speakers![0]!.Color, last.Speakers[1]!.Color);
        Assert.All(last.Speakers, s => Assert.NotEqual("#FFFFFF", s!.Color));

        Assert.Equal(1, NameRequests(rig, "Lucia\nJason"));
        Assert.Single(rig.Ai.Texts, t => t == "Wait for me at the car.\nFine, but hurry up then.");
        Assert.DoesNotContain(rig.Ai.Texts, t => t.Contains("Lucia:", StringComparison.Ordinal)); // names never in a subtitle request
    }

    [Fact]
    public async Task Pipeline_KnownName_NotTranslatedAgain()
    {
        using var rig = new Rig();
        await ReadLinesAsync(rig, L1);
        await ReadLinesAsync(rig, L2);
        await ReadLinesAsync(rig, L3);

        Assert.Equal(1, NameRequests(rig, "Lucia"));
        Assert.Equal(new SpeakerTag("th:Lucia", SpeakerPalette.For("Lucia")), rig.Hub.GetHistory()[^1].Speakers![0]);
    }

    [Fact]
    public async Task Pipeline_NamesTranslatedByAi_ShownOnLaterLines()
    {
        using var rig = new Rig();
        rig.Ai.Map = r => r.Kind == AiRequestKind.Names ? "ลูเซีย" : "ai:" + r.Text;

        await ReadLinesAsync(rig, L1);
        await ReadLinesAsync(rig, L2);
        await ReadLinesAsync(rig, L3);

        var e = rig.Hub.GetHistory()[^1];
        Assert.Equal(new SpeakerTag("ลูเซีย", SpeakerPalette.For("Lucia")), e.Speakers![0]);
        Assert.Equal("ai:See you at the docks tonight.", e.Translated);
        Assert.Single(rig.Ai.Requests, r => r.Kind == AiRequestKind.Names);
    }

    [Fact]
    public async Task Pipeline_KnownNameFromConfig_UsedWithColour()
    {
        var speakers = new Dictionary<string, SpeakerInfo> { ["Lucia"] = new() { Thai = "ลูเซีย", Color = "#E070C0" } };
        using var rig = new Rig(Rig.Config() with { Speakers = speakers });
        await ReadLinesAsync(rig, L1);

        Assert.Equal(["Where are you going?"], rig.Ai.Texts);
        Assert.Equal(new SpeakerTag("ลูเซีย", "#E070C0"), Assert.Single(rig.Hub.GetHistory()).Speakers![0]);
    }

    [Fact]
    public async Task Pipeline_NameTranslationFails_OriginalNameShown_RetriedAfterAMinute()
    {
        using var rig = new Rig();
        bool nameFails = true;
        rig.Ai.Map = r => r.Kind == AiRequestKind.Names && nameFails ? throw new InvalidOperationException() : "th:" + r.Text;

        await ReadLinesAsync(rig, L1);
        await ReadLinesAsync(rig, L2);
        var e = rig.Hub.GetHistory()[^1];
        Assert.Equal(new SpeakerTag("Lucia", SpeakerPalette.For("Lucia")), e.Speakers![0]);
        Assert.Equal(("th:Wait for me at the car.", (string?)null), (e.Translated, e.Error));
        Assert.Null(rig.Hub.Status.AiError); // a failed name lookup does not touch the AI status

        await ReadLinesAsync(rig, L3);
        Assert.Equal(1, NameRequests(rig, "Lucia"));

        nameFails = false;
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        await ReadLinesAsync(rig, L4);
        await ReadLinesAsync(rig, L1);
        Assert.Equal(2, NameRequests(rig, "Lucia"));
        Assert.Equal(new SpeakerTag("th:Lucia", SpeakerPalette.For("Lucia")), rig.Hub.GetHistory()[^1].Speakers![0]);
    }

    [Fact]
    public async Task Pipeline_NameLookupDoesNotHoldTheLine()
    {
        using var rig = new Rig();
        await ReadLinesAsync(rig, L1);

        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate; // every AI call from now on waits, the name lookup included
        rig.Ocr.ReturnsLines(L2).ReturnsLines(L2);
        await rig.TickAsync();
        await rig.TickAsync();
        await rig.Ai.WaitForCallsAsync(3); // L1's dialogue, then the name lookup and L2's dialogue, both held

        var e = rig.Hub.GetHistory()[^1];
        Assert.Equal((L2, true), (e.Source, e.AwaitingAi));
        Assert.Equal(new SpeakerTag("Lucia", SpeakerPalette.For("Lucia")), e.Speakers![0]);

        gate.SetResult();
        await rig.WhenAllIdleAsync();
        Assert.Equal("th:Wait for me at the car.", rig.Hub.GetHistory()[^1].Translated);
    }

    [Fact]
    public async Task Pipeline_LineCountMismatch_KeepsTextDropsSpeakers()
    {
        using var rig = new Rig();
        rig.Ai.Map = r => r.Text == "Where?\nOut." ? "ไปไหนออกไป" : "th:" + r.Text;

        await ReadLinesAsync(rig, "Lucia: Where?", "Jason: Out.");

        var e = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal(("ไปไหนออกไป", (string?)null, false), (e.Translated, e.Dialogue, e.AwaitingAi));
        Assert.Null(e.Speakers);
    }

    [Fact]
    public async Task Pipeline_BlankLinesInTranslation_Dropped()
    {
        using var rig = new Rig();
        rig.Ai.Map = r => r.Text == "Where?\nOut." ? "ไปไหน?\r\n\nออกไป\n" : "th:" + r.Text;

        await ReadLinesAsync(rig, "Lucia: Where?", "Jason: Out.");

        var e = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal("ไปไหน?\nออกไป", e.Translated);
        Assert.Equal(2, e.Speakers!.Count);
    }

    [Fact]
    public async Task Pipeline_NoNames_EntryUnchanged()
    {
        using var rig = new Rig();
        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        var e = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal(("th:" + A, (string?)null, (IReadOnlyList<SpeakerTag?>?)null), (e.Translated, e.Dialogue, e.Speakers));
    }

    [Fact]
    public async Task Pipeline_NewName_RaisesSpeakerLearned()
    {
        using var rig = new Rig(Rig.Config() with { ProfileName = "P" });
        await ReadLinesAsync(rig, L1);
        await ReadLinesAsync(rig, L2);

        lock (rig.Learned)
            Assert.Equal([("P", "Lucia", (string?)"th:Lucia", (string?)null)], rig.Learned);
    }

    [Fact]
    public async Task Pipeline_NameColour_SampledOnlyFromStableReads_OfTrustedNames()
    {
        using var rig = new Rig(Rig.Config() with { ProfileName = "P" });
        static OcrLine Labelled(string text) => new(text, new PixelRect(0, 0, 70, 10),
            [new OcrWord("Lucia:", new PixelRect(0, 0, 20, 10)), new OcrWord("x", new PixelRect(25, 0, 40, 10))]);

        rig.Ocr.ReturnsRaw(Labelled(L1)); // one unstable read
        await rig.TickAsync();
        lock (rig.Learned) Assert.Empty(rig.Learned);

        await ReadRawAsync(rig, Labelled(L1)); // stable, first sighting: not trusted yet
        lock (rig.Learned) Assert.Empty(rig.Learned);

        await ReadRawAsync(rig, Labelled(L2)); // second sighting: colour sampled (blank frame: white), name translated
        lock (rig.Learned)
            Assert.Contains(rig.Learned, l => l.Name == "Lucia" && l.Color == "#FFFFFF");
    }

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#F9FBFF")]
    public async Task Pipeline_SampledWhiteName_GetsPaletteColour(string learned)
    {
        var speakers = new Dictionary<string, SpeakerInfo> { ["Lucia"] = new() { Thai = "ลูเซีย", Color = learned } };
        using var rig = new Rig(Rig.Config() with { Speakers = speakers });
        await ReadLinesAsync(rig, L1);

        Assert.Equal(new SpeakerTag("ลูเซีย", SpeakerPalette.For("Lucia")), Assert.Single(rig.Hub.GetHistory()).Speakers![0]);
    }

    // ---- Library ----

    private const string HitEn = "Where are you going?", HitTh = "จะไปไหน";
    private const string Hit2En = "Just out for a bit.", Hit2Th = "แค่ออกไปแป๊บเดียว";

    private static readonly GameInfo Gta = new("GTA", "Leonida", [
        new GameCharacter("Lucia", ["Lu"], "female", "ลูเซีย", null, null, null, null),
        new GameCharacter("Jason", ["Jay"], "male", "เจสัน", null, null, null, null),
    ], []);

    /// <summary>A holder with an active library of <paramref name="lines"/> (English, Thai).</summary>
    private LibraryHolder Library(params (string En, string Th)[] lines)
    {
        var data = new LibraryData("gta6", new LibraryVersion(20261121, 1), null,
            [.. lines.Select((l, i) => new LibraryLine(l.En, l.Th, "game", null, null, null, i))], [], Gta, "");
        var holder = new LibraryHolder();
        holder.Set(ActiveLibrary.Build(data));
        return holder;
    }

    [Fact]
    public async Task Publish_LibraryHit_ShowsThaiAndSkipsAi()
    {
        var library = Library((A, "สวัสดีครับ"));
        using var rig = new Rig(library: library);

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        Assert.Empty(rig.Ai.Requests);
        var entry = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal((A, "สวัสดีครับ", false), (entry.Source, entry.Translated, entry.AwaitingAi));
        Assert.Equal((1, 0), library.Current!.Counts);
    }

    [Fact]
    public async Task Publish_TwoSpeakers_AllHit_ShowsThaiPerLine()
    {
        var library = Library((HitEn, HitTh), (Hit2En, Hit2Th));
        using var rig = new Rig(library: library);

        await ReadLinesAsync(rig, "Lu: " + HitEn, "Jay: " + Hit2En);

        Assert.DoesNotContain(rig.Ai.Requests, r => r.Kind == AiRequestKind.Subtitle);
        var e = Assert.Single(rig.Hub.GetHistory());
        Assert.Equal((HitTh + "\n" + Hit2Th, HitEn + "\n" + Hit2En, false), (e.Translated, e.Dialogue, e.AwaitingAi));
        Assert.Equal(["ลูเซีย", "เจสัน"], e.Speakers!.Select(s => s!.Name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publish_WithoutLibrary_SendsToAi(bool emptyHolder)
    {
        using var rig = new Rig(library: emptyHolder ? new LibraryHolder() : null);

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        Assert.Equal([A], rig.Ai.Texts);
        Assert.Equal("th:" + A, Assert.Single(rig.Hub.GetHistory()).Translated);
    }

    [Fact]
    public async Task Publish_LibrarySet_TargetNotThai_GoesToAi_NothingLearned()
    {
        // The library is English → Thai only: a line for another target never reads or teaches it.
        var library = Library((A, "สวัสดีครับ"));
        using var rig = new Rig(Rig.Config() with { TargetLang = "ja" }, library: library);

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        Assert.Equal([A], rig.Ai.Texts);
        Assert.Null(Assert.Single(rig.Ai.Requests).Brief);
        Assert.Equal("th:" + A, Assert.Single(rig.Hub.GetHistory()).Translated);
        Assert.Equal((0, 0), library.Current!.Counts);
        Assert.Equal(0, library.Current.Matcher.LearnedCount);
    }

    [Fact]
    public async Task Publish_TwoSpeakers_OneMiss_SendsWholeLineToAi()
    {
        var library = Library((HitEn, HitTh));
        using var rig = new Rig(library: library);

        await ReadLinesAsync(rig, "Lucia: " + HitEn, "Jason: " + Hit2En);

        Assert.Equal([HitEn + "\n" + Hit2En], rig.Ai.Requests.Where(r => r.Kind == AiRequestKind.Subtitle).Select(r => r.Text));
        Assert.Equal("th:" + HitEn + "\n" + Hit2En, Assert.Single(rig.Hub.GetHistory()).Translated);
        Assert.Equal((0, 1), library.Current!.Counts);
    }

    [Fact]
    public async Task Publish_HitIsContextForNextAiLine()
    {
        using var rig = new Rig(library: Library((A, "สวัสดีครับ")));

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        await rig.ReadAsync(B, B);
        await rig.WhenAllIdleAsync();

        var request = Assert.Single(rig.Ai.Requests);
        Assert.Equal(B, request.Text);
        Assert.Equal(new AiContextLine(A, "สวัสดีครับ"), request.Context[^1]);
    }

    [Fact]
    public async Task SpeakerTag_UsesCharacterThaiName()
    {
        using var rig = new Rig(library: Library());

        await ReadLinesAsync(rig, "Lu: " + HitEn);
        await ReadLinesAsync(rig, "Lu: " + Hit2En); // a second sighting: the book would look the name up now

        Assert.All(rig.Hub.GetHistory(), e => Assert.Equal("ลูเซีย", e.Speakers![0]!.Name));
        Assert.Equal(0, NameRequests(rig, "Lu")); // the library already knows it
    }

    [Fact]
    public async Task Publish_WithoutLibrary_LibrarySetBeforeSend_NoBrief_NothingLearned()
    {
        // The library is the one the line was read under: a line queued before a library arrives (a profile switch)
        // gets no game notes and teaches nothing to it.
        var holder = new LibraryHolder();
        using var rig = new Rig(library: holder);
        rig.Ai.Gate = FakeAiTranslator.NewGate();

        await rig.ReadAsync(A, A); // sent; the answer is held
        await rig.Ai.WaitForCallsAsync(1);
        await rig.ReadAsync(B, B); // queued behind A
        var library = Library((C, "เจอกันพรุ่งนี้")).Current!;
        holder.Set(library);
        rig.Ai.Gate.SetResult();
        await rig.WhenAllIdleAsync();

        Assert.Equal([A, B], rig.Ai.Texts);
        Assert.All(rig.Ai.Requests, r => Assert.Null(r.Brief));
        Assert.Equal(0, library.Matcher.LearnedCount);
        Assert.Equal(["th:" + A, "th:" + B], rig.Hub.GetHistory().Select(e => e.Translated));
    }

    [Fact]
    public async Task Refiner_LearnedIsMatchedNextTime()
    {
        var library = Library();
        using var rig = new Rig(library: library);

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        await rig.ReadAsync(B, B);
        await rig.WhenAllIdleAsync();
        await rig.ReadAsync(A, A); // the AI's answer for A is in the library now
        await rig.WhenAllIdleAsync();

        Assert.Equal([A, B], rig.Ai.Texts);
        var last = rig.Hub.GetHistory()[^1];
        Assert.Equal((A, "th:" + A, false), (last.Source, last.Translated, last.AwaitingAi));
        Assert.Equal(2, library.Current!.Matcher.LearnedCount);
        // the AI's answers stay in memory: no file anywhere under the test folder
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*.jsonl", SearchOption.AllDirectories).Any());
    }

    // ---- Zones ----
    // The OCR script follows the zones in order each tick, a zone's trigger box before its OCR box.

    private static readonly RegionFraction TopHalf = new(0, 0, 1, 0.5);

    /// <summary>A second zone over the top half, overlay-only.</summary>
    private static readonly ZoneConfig Chat = new("z2", "แชท", TopHalf, PreprocessFilter.Normal, SendToWeb: false);

    /// <summary>A zone over the bottom half that reads only while its trigger box (top half) shows SEND.</summary>
    private static readonly ZoneConfig Triggered = new("z2", "แชท", Region, PreprocessFilter.Normal,
        Trigger: new ZoneTrigger(TopHalf, ["SEND"]));

    /// <summary>Runs one tick per group of OCR texts (one text per box read in that tick).</summary>
    private static async Task TicksAsync(Rig rig, params string[][] ticks)
    {
        foreach (var texts in ticks)
        {
            rig.Ocr.Returns(texts);
            await rig.TickAsync();
        }
    }

    private static List<T> EventsOf<T>(Rig rig) => [.. rig.EventsSnapshot().OfType<T>()];

    [Fact]
    public async Task TwoZones_PublishSeparately()
    {
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue, Chat));

        await TicksAsync(rig, [A, B], [A, B]);
        await rig.WhenAllIdleAsync();

        Assert.Equal([("z1", "บทพูด", A, true), ("z2", "แชท", B, false)],
            rig.Hub.GetHistory().Select(e => (e.ZoneId, e.ZoneName, e.Source, e.SendToWeb)));
        Assert.Equal(4, rig.Ocr.Calls);
    }

    [Fact]
    public async Task ClearInOneZone_DoesNotClearTheOther()
    {
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue, Chat));
        await TicksAsync(rig, [A, B], [A, B]);

        await TicksAsync(rig, ["", B], ["", B], ["", B], ["", B], ["", B], ["", B]);

        Assert.Equal(["z1"], EventsOf<SubtitleCleared>(rig).Select(c => c.ZoneId));
    }

    [Fact]
    public async Task OcrFault_InOneZone_OtherZoneStillReads()
    {
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue, Chat));
        rig.Ocr.Throws(new InvalidOperationException("ocr failed")).Returns(B);
        await rig.TickAsync();
        await TicksAsync(rig, [A, B]);
        await rig.WhenAllIdleAsync();

        Assert.Equal([("z2", B)], rig.Hub.GetHistory().Select(e => (e.ZoneId, e.Source)));
    }

    [Fact]
    public async Task Trigger_NotSeen_DoesNotReadZoneBox()
    {
        using var rig = new Rig(Rig.ConfigOf(Triggered));

        await TicksAsync(rig, ["SCROLL"], ["SCROLL"], ["SCROLL"]);

        Assert.Equal(3, rig.Ocr.Calls);
        Assert.Empty(rig.Hub.GetHistory());
        Assert.Empty(EventsOf<TriggerChanged>(rig));
    }

    [Fact]
    public async Task Trigger_Seen_ReadsAndPublishes()
    {
        using var rig = new Rig(Rig.ConfigOf(Triggered));

        await TicksAsync(rig, ["SEND", A], ["SEND", A]);
        await rig.WhenAllIdleAsync();

        Assert.Equal(4, rig.Ocr.Calls);
        Assert.Equal([("z2", A)], rig.Hub.GetHistory().Select(e => (e.ZoneId, e.Source)));
        Assert.Equal([new TriggerChanged("z2", true)], EventsOf<TriggerChanged>(rig));
    }

    [Fact]
    public async Task Trigger_OneMiss_KeepsZone()
    {
        using var rig = new Rig(Rig.ConfigOf(Triggered));

        await TicksAsync(rig, ["SEND", A], ["SEND", A], ["SCROLL", A]);

        Assert.Equal(6, rig.Ocr.Calls); // the zone box is still read on the miss
        Assert.Empty(EventsOf<ZoneHidden>(rig));
        Assert.Equal([new TriggerChanged("z2", true)], EventsOf<TriggerChanged>(rig));
    }

    [Fact]
    public async Task Trigger_TwoMisses_HideZone()
    {
        using var rig = new Rig(Rig.ConfigOf(Triggered));
        await TicksAsync(rig, ["SEND", A], ["SEND", A], ["SCROLL", A]);
        Assert.Empty(EventsOf<ZoneHidden>(rig));

        await TicksAsync(rig, ["SCROLL"]);

        Assert.Equal(7, rig.Ocr.Calls); // turned off: the zone box is not read
        Assert.Equal([new ZoneHidden("z2")], EventsOf<ZoneHidden>(rig));
        Assert.Equal([new TriggerChanged("z2", true), new TriggerChanged("z2", false)], EventsOf<TriggerChanged>(rig));
    }

    [Fact]
    public async Task Trigger_OnAgain_SameLinePublishedAgain()
    {
        using var rig = new Rig(Rig.ConfigOf(Triggered));
        await TicksAsync(rig, ["SEND", A], ["SEND", A], ["SCROLL", A], ["SCROLL"]);

        await TicksAsync(rig, ["SEND", A], ["SEND", A]);
        await rig.WhenAllIdleAsync();

        // Turning off reset the zone's filter: the line it hid shows again.
        Assert.Equal([A, A], rig.Hub.GetHistory().Select(e => e.Source));
    }

    [Theory]
    [InlineData(false, "SEND")]
    [InlineData(true, " , ")]
    public async Task Trigger_EnabledWithoutBox_DoesNotRead(bool withBox, string words)
    {
        using var rig = new Rig(Rig.ConfigOf(Triggered with
        {
            Trigger = new ZoneTrigger(withBox ? TopHalf : null, TriggerWords.Parse(words)),
        }));

        await TicksAsync(rig, [A], [A]);

        Assert.Equal(0, rig.Ocr.Calls);
        Assert.Empty(rig.Hub.GetHistory());
        Assert.False(rig.Hub.Status.NoRegion);
    }

    /// <summary>A Bubbles zone with a trigger (its trigger box is read first each tick: pass <c>"SEND"</c>).</summary>
    private static readonly ZoneConfig TriggeredBubbles = Triggered with { Placement = OverlayPlacement.Fit };

    /// <summary>One tick per text for <see cref="TriggeredBubbles"/>, its trigger word seen in each.</summary>
    private static Task SentAsync(Rig rig, params string[] texts) =>
        TicksAsync(rig, [.. texts.Select(t => new[] { "SEND", t })]);

    [Fact]
    public async Task FitZone_TextMoves_HidesOnceAndRepublishesSameText()
    {
        using var rig = new Rig(Rig.ConfigOf(TriggeredBubbles));
        await SentAsync(rig, A, A);

        await SentAsync(rig, B, B);
        Assert.Equal([new ZoneHidden("z2")], EventsOf<ZoneHidden>(rig));

        await SentAsync(rig, A, A);
        await rig.WhenAllIdleAsync();
        Assert.Equal(2, rig.Hub.GetHistory().Count(e => e.Source == A));
    }

    [Fact]
    public async Task FitZone_TextGone_HidesAtOnce_AndTheSameTextShowsAgain()
    {
        using var rig = new Rig(Rig.ConfigOf(TriggeredBubbles));
        await SentAsync(rig, A, A);

        await SentAsync(rig, "", "");
        Assert.Equal([new ZoneHidden("z2")], EventsOf<ZoneHidden>(rig));

        await SentAsync(rig, A, A); // the filter was reset, so A is not taken for a duplicate
        await rig.WhenAllIdleAsync();
        Assert.Equal([A, A], rig.Hub.GetHistory().Select(e => e.Source));
        Assert.Empty(EventsOf<SubtitleCleared>(rig));
    }

    [Fact]
    public async Task FitZone_OcrNoise_DoesNotHide()
    {
        using var rig = new Rig(Rig.ConfigOf(TriggeredBubbles));

        await SentAsync(rig, A, A, "Hello thore");

        Assert.Empty(EventsOf<ZoneHidden>(rig));
    }

    [Fact]
    public async Task FitZoneWithoutTrigger_TextChanges_NoZoneHidden()
    {
        // A dialogue zone saved in Bubbles mode before zones: it behaves as it did then.
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue with { Placement = OverlayPlacement.Fit }));
        await rig.ReadAsync(A, A);

        await rig.ReadAsync(B, B, "");
        await rig.WhenAllIdleAsync();

        Assert.Empty(EventsOf<ZoneHidden>(rig));
        Assert.Equal([A, B], rig.Hub.GetHistory().Select(e => e.Source));
    }

    [Fact]
    public async Task ShownLine_TriggerEnabledWithoutBox_HidesZone()
    {
        using var rig = new Rig();
        await rig.ReadAsync(A, A);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with { Trigger = new ZoneTrigger(null, ["SEND"]) }));
        await rig.TickAsync();

        Assert.Equal([new ZoneHidden("z1")], EventsOf<ZoneHidden>(rig));
    }

    [Fact]
    public async Task ActiveTrigger_TriggerWordsChanged_HidesZoneAndTurnsTriggerOff()
    {
        using var rig = new Rig(Rig.ConfigOf(Triggered));
        await TicksAsync(rig, ["SEND", A], ["SEND", A]);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Triggered with { Trigger = new ZoneTrigger(TopHalf, ["BACK"]) }));
        await TicksAsync(rig, ["SEND"]);

        Assert.Equal([new ZoneHidden("z2")], EventsOf<ZoneHidden>(rig));
        Assert.Equal([new TriggerChanged("z2", true), new TriggerChanged("z2", false)], EventsOf<TriggerChanged>(rig));
    }

    [Fact]
    public async Task ZoneLearnToLibraryOff_AiAnswerIsNotLearned()
    {
        var library = Library();
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue with { LearnToLibrary = false }), library: library);

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        Assert.Equal("th:" + A, rig.Hub.GetHistory()[^1].Translated);
        Assert.Equal(0, library.Current!.Matcher.LearnedCount);
    }

    [Fact]
    public async Task ZoneLearnToLibraryOn_AiAnswerIsLearned()
    {
        var library = Library();
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue with { LearnToLibrary = true }), library: library);

        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        Assert.Equal("th:" + A, rig.Hub.GetHistory()[^1].Translated);
        Assert.Equal(1, library.Current!.Matcher.LearnedCount);
    }

    /// <summary>A Bubbles zone whose AI translates each line on its own (<c>x</c> → <c>th:x</c>).</summary>
    private static Rig BubblesRig(LibraryHolder? library = null)
    {
        var rig = new Rig(Rig.ConfigOf(Rig.Dialogue with { Placement = OverlayPlacement.Fit }), library: library);
        rig.Ai.Map = r => string.Join('\n', r.Text.Split('\n').Select(l => "th:" + l));
        return rig;
    }

    /// <summary>Two stable reads of one bubble per text (far enough apart to split), from <paramref name="top"/> down.</summary>
    private static async Task ReadBubblesAsync(Rig rig, int top, params string[] texts)
    {
        OcrLine[] lines = [.. texts.Select((t, i) => new OcrLine(t, new PixelRect(0, top + i * 7, 40, 2)))];
        rig.Ocr.ReturnsRaw(lines).ReturnsRaw(lines);
        await rig.TickAsync();
        await rig.TickAsync();
        await rig.WhenAllIdleAsync();
    }

    private static int SubtitleRequests(Rig rig) => rig.Ai.Requests.Count(r => r.Kind == AiRequestKind.Subtitle);

    /// <summary>Empty reads until the zone clears (a Bubbles zone without a trigger waits like a Block zone).</summary>
    private static Task GoneAsync(Rig rig) => rig.ReadAsync("", "", "", "", "", "");

    [Fact]
    public async Task FitZone_ScrollBack_UsesMemoryWithoutAi()
    {
        using var rig = BubblesRig();
        await ReadBubblesAsync(rig, 0, A, B, C);
        Assert.Equal(1, SubtitleRequests(rig));

        await GoneAsync(rig); // scrolled away
        await ReadBubblesAsync(rig, 2, A, B, C); // and back, a little lower

        Assert.Equal(1, SubtitleRequests(rig));
        Assert.Equal(2, rig.Hub.GetHistory().Count); // the second read was published, not just the first one kept
        var last = rig.Hub.GetHistory()[^1];
        Assert.Equal(($"th:{A}\nth:{B}\nth:{C}", false), (last.Translated, last.AwaitingAi));
    }

    [Fact]
    public async Task FitZone_NewBubble_SendsOnlyTheNewOne()
    {
        using var rig = BubblesRig();
        await ReadBubblesAsync(rig, 0, A, B);

        await ReadBubblesAsync(rig, 0, A, B, C); // a new message came in

        Assert.Equal([$"{A}\n{B}", C], rig.Ai.Requests.Where(r => r.Kind == AiRequestKind.Subtitle).Select(r => r.Text));
        Assert.Equal($"th:{A}\nth:{B}\nth:{C}", rig.Hub.GetHistory()[^1].Translated);
    }

    [Fact]
    public async Task FitZone_LibraryHitAndMemory_FillPartsTogether()
    {
        var library = Library((C, "เจอกันพรุ่งนี้"));
        using var rig = BubblesRig(library);
        await ReadBubblesAsync(rig, 0, A, B);

        await ReadBubblesAsync(rig, 0, A, B, C);

        Assert.Equal(1, SubtitleRequests(rig));
        Assert.Equal($"th:{A}\nth:{B}\nเจอกันพรุ่งนี้", rig.Hub.GetHistory()[^1].Translated);
    }

    [Fact]
    public async Task FitZone_BoxMoved_ForgetsTheBubbles()
    {
        using var rig = BubblesRig();
        await ReadBubblesAsync(rig, 0, A, B);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with
        {
            Placement = OverlayPlacement.Fit, Region = new RegionFraction(0, 0.4, 1, 0.6),
        }));
        await ReadBubblesAsync(rig, 0, A, B);

        Assert.Equal(2, SubtitleRequests(rig));
    }

    [Fact]
    public async Task FitZone_FilterChanged_KeepsTheBubbles()
    {
        using var rig = BubblesRig();
        await ReadBubblesAsync(rig, 0, A, B);
        await GoneAsync(rig);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with
        {
            Placement = OverlayPlacement.Fit, Filter = PreprocessFilter.WhiteText,
        }));
        await ReadBubblesAsync(rig, 0, A, B);

        Assert.Equal(1, SubtitleRequests(rig));
        Assert.Equal(2, rig.Hub.GetHistory().Count);
    }

    [Fact]
    public async Task FitZone_TargetLanguageChanged_ForgetsTheBubbles()
    {
        using var rig = BubblesRig();
        await ReadBubblesAsync(rig, 0, A, B);
        await GoneAsync(rig);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with { Placement = OverlayPlacement.Fit }) with
        {
            TargetLang = "ja",
        });
        await ReadBubblesAsync(rig, 0, A, B);

        // The same bubbles go to the AI again, for the new language.
        Assert.Equal([$"{A}\n{B}", $"{A}\n{B}"],
            rig.Ai.Requests.Where(r => r.Kind == AiRequestKind.Subtitle).Select(r => r.Text));
    }

    [Fact]
    public async Task FitZone_LearnsEachBubble_LibraryHasThemAfterTheMemoryIsGone()
    {
        var library = Library();
        using var rig = BubblesRig(library);
        await ReadBubblesAsync(rig, 0, A, B);
        Assert.Equal(2, library.Current!.Matcher.LearnedCount);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with
        {
            Placement = OverlayPlacement.Fit, Region = new RegionFraction(0, 0.4, 1, 0.6),
        }));
        await ReadBubblesAsync(rig, 0, B, A);

        Assert.Equal(1, SubtitleRequests(rig));
        Assert.Equal($"th:{B}\nth:{A}", rig.Hub.GetHistory()[^1].Translated);
    }

    [Fact]
    public async Task BlockZone_SameLineAgain_GoesToAiAgain()
    {
        // Bubbles only: a Block zone keeps the library's all-or-nothing lookup and remembers nothing.
        using var rig = new Rig();
        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();
        await rig.ReadAsync(B, B, A, A);
        await rig.WhenAllIdleAsync();

        Assert.Equal([A, B, A], rig.Ai.Texts);
    }

    [Fact]
    public async Task BlockZone_TextMoves_DoesNotHide()
    {
        using var rig = new Rig();

        await rig.ReadAsync(A, A, B, "");

        Assert.Empty(EventsOf<ZoneHidden>(rig));
    }

    [Fact]
    public async Task RemovedZone_IsHidden()
    {
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue, Chat));
        await TicksAsync(rig, [A, B], [A, B]);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue));
        await TicksAsync(rig, [A], [A]);

        Assert.Equal([new ZoneHidden("z2")], EventsOf<ZoneHidden>(rig));
        Assert.Equal(6, rig.Ocr.Calls);
    }

    [Fact]
    public async Task ZoneRenamed_KeepsFilterState()
    {
        using var rig = new Rig();
        await rig.ReadAsync(A, A);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue with { Name = "อื่น" }));
        await rig.ReadAsync(A, A);
        await rig.WhenAllIdleAsync();

        Assert.Single(rig.Hub.GetHistory());
    }

    [Fact]
    public async Task OneZoneMoved_OtherZoneKeepsFilterState()
    {
        using var rig = new Rig(Rig.ConfigOf(Rig.Dialogue, Chat));
        await TicksAsync(rig, [A, B], [A, B]);

        rig.Pipeline.UpdateConfig(Rig.ConfigOf(Rig.Dialogue, Chat with { Region = new RegionFraction(0, 0, 1, 0.4) }));
        await TicksAsync(rig, [A, B], [A, B]);
        await rig.WhenAllIdleAsync();

        // z1 keeps its line; z2's box moved, so its filter starts again and its line shows again.
        Assert.Equal([("z1", A), ("z2", B), ("z2", B)], rig.Hub.GetHistory().Select(e => (e.ZoneId, e.Source)));
    }

    [Fact]
    public void ZoneConfig_From_MapsTheZone()
    {
        var zone = new OcrZone
        {
            Id = "abc", Name = "แชท", Region = TopHalf, Filter = PreprocessFilter.WhiteText,
            Placement = OverlayPlacement.Fit, SendToWeb = false, LearnToLibrary = false, SpeakerLinesOnly = true,
            TriggerEnabled = true, TriggerRegion = Region, TriggerWords = "SEND, ส่ง",
        };

        var config = ZoneConfig.From(zone);

        Assert.Equal(("abc", "แชท", TopHalf, PreprocessFilter.WhiteText, OverlayPlacement.Fit, false, false, true),
            (config.Id, config.Name, config.Region, config.Filter, config.Placement, config.SendToWeb,
                config.LearnToLibrary, config.SpeakerLinesOnly));
        Assert.Equal(Region, config.Trigger!.Region);
        Assert.Equal(["SEND", "ส่ง"], config.Trigger.Words);
        Assert.Null(ZoneConfig.From(new OcrZone { TriggerRegion = Region, TriggerWords = "SEND" }).Trigger);
    }

    [Fact]
    public void ZoneConfig_SameReading_ComparesTriggerWordsBySequence()
    {
        var a = Triggered;
        var b = Triggered with { Trigger = new ZoneTrigger(TopHalf, ["SEND"]), Name = "x", SendToWeb = false };

        Assert.True(a.SameReading(b));
        Assert.False(a.SameReading(b with { Trigger = new ZoneTrigger(TopHalf, ["SENT"]) }));
        Assert.False(a.SameReading(b with { Trigger = null }));
        Assert.False(a.SameReading(b with { Placement = OverlayPlacement.Fit }));
    }
}
