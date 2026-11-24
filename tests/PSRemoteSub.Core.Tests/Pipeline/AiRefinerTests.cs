using Microsoft.Extensions.Time.Testing;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Tests.Pipeline.Fakes;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Tests.Pipeline;

public class AiRefinerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-refiner-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Rig : IDisposable
    {
        private readonly Lock _statusGate = new();
        private PipelineStatus _status = PipelineStatus.Initial;

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        public SubtitleHub Hub { get; }
        public FakeAiTranslator Ai { get; } = new();
        public FakeAiTranslatorSource Source { get; }
        public AiRefiner Refiner { get; }

        /// <summary>The library <see cref="Publish"/> puts in each job's context (the one the line was read under).</summary>
        public ActiveLibrary? Library { get; }

        /// <param name="google">Start with a Google handle (<see cref="TranslatorKind.Google"/>) instead of an AI one.</param>
        public Rig(ActiveLibrary? library = null, bool google = false)
        {
            Hub = new SubtitleHub(Time);
            Source = new FakeAiTranslatorSource(google
                ? new AiTranslatorHandle(Ai, null, 1, "google", Kind: TranslatorKind.Google)
                : new AiTranslatorHandle(Ai, null, 1));
            Refiner = new AiRefiner(Hub, Source, Time, change =>
            {
                lock (_statusGate)
                {
                    _status = change(_status);
                    Hub.SetStatus(_status);
                }
            });
            Library = library;
        }

        public PipelineStatus Status => Hub.Status;

        public static AiJobContext Ctx(int contextLines = 4, string? gameTitle = null) =>
            new(SourceLanguages.Get("en"), "th", gameTitle, contextLines);

        /// <summary>Publishes as the pipeline does (pending, no text yet), then hands it to the refiner.</summary>
        public SubtitleEntry Publish(string text, int contextLines = 4)
        {
            var entry = Hub.Publish(text, null, "en", null, awaitingAi: Refiner.IsActive);
            Refiner.OnPublished(entry, Ctx(contextLines) with { Library = Library });
            return entry;
        }

        /// <summary>As <see cref="Publish"/>, for a line of the given zone.</summary>
        public SubtitleEntry PublishIn(string zone, string text, int contextLines = 4, bool learn = true)
        {
            var entry = Hub.Publish(text, null, "en", null, awaitingAi: Refiner.IsActive, zoneId: zone);
            Refiner.OnPublished(entry, Ctx(contextLines) with { Library = Library, Learn = learn });
            return entry;
        }

        public async Task<SubtitleEntry> PublishIdleAsync(string text, int contextLines = 4)
        {
            var entry = Publish(text, contextLines);
            await Refiner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return Get(entry.Id);
        }

        public SubtitleEntry Get(long id) => Hub.GetHistory().Single(e => e.Id == id);

        public void Dispose() => Refiner.Dispose();
    }

    /// <summary>Settled without a translation: the source shows as untranslated, never dim.</summary>
    private static void AssertUntranslated(SubtitleEntry entry)
    {
        Assert.Null(entry.Translated);
        Assert.False(entry.AwaitingAi);
    }

    private static void AssertRefined(SubtitleEntry entry, string aiText)
    {
        Assert.Equal(aiText, entry.Translated);
        Assert.False(entry.AwaitingAi);
        Assert.Null(entry.Error);
    }

    private static IEnumerable<(string, string?)> Pairs(AiTranslationRequest r) =>
        r.Context.Select(c => (c.Source, c.Translation));

    [Fact]
    public async Task Success_RefinesEntry()
    {
        using var rig = new Rig();

        var published = rig.Publish("A");
        Assert.True(published.AwaitingAi);
        await rig.Refiner.WhenIdleAsync();

        AssertRefined(rig.Get(published.Id), "ai:A");
        var request = Assert.Single(rig.Ai.Requests);
        Assert.Equal(("A", "English", "Thai"), (request.Text, request.SourceLanguageName, request.TargetLanguageName));
        Assert.Null(rig.Status.AiError);
    }

    [Fact]
    public async Task Request_CarriesGameTitle()
    {
        using var rig = new Rig();
        var entry = rig.Hub.Publish("A", null, "en", null, awaitingAi: true);

        rig.Refiner.OnPublished(entry, Rig.Ctx(gameTitle: "Astro Bot"));
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal("Astro Bot", Assert.Single(rig.Ai.Requests).GameTitle);
    }

    [Fact]
    public async Task Context_SendsPreviousPairsInOrder()
    {
        using var rig = new Rig();

        await rig.PublishIdleAsync("A", contextLines: 2);
        await rig.PublishIdleAsync("B", contextLines: 2);
        await rig.PublishIdleAsync("C", contextLines: 2);
        await rig.PublishIdleAsync("D", contextLines: 2);

        var requests = rig.Ai.Requests;
        Assert.Empty(requests[0].Context);
        Assert.Equal([("A", "ai:A")], Pairs(requests[1]));
        Assert.Equal([("A", "ai:A"), ("B", "ai:B")], Pairs(requests[2]));
        Assert.Equal([("B", "ai:B"), ("C", "ai:C")], Pairs(requests[3]));
    }

    [Fact]
    public async Task Context_UntranslatedLine_SentWithoutTranslation()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Unavailable, 503));

        await rig.PublishIdleAsync("A");
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await rig.PublishIdleAsync("B");

        Assert.Equal([("A", (string?)null)], Pairs(rig.Ai.Requests[1]));
    }

    [Fact]
    public async Task Context_SkipsExactRepeat()
    {
        using var rig = new Rig();

        await rig.PublishIdleAsync("A");
        await rig.PublishIdleAsync("A");
        await rig.PublishIdleAsync("B");

        Assert.Equal([("A", "ai:A")], Pairs(rig.Ai.Requests[2]));
    }

    [Fact]
    public async Task Context_HoldsAtMost10Pairs()
    {
        using var rig = new Rig();

        for (int i = 0; i < 12; i++)
            await rig.PublishIdleAsync("L" + i, contextLines: 10);
        await rig.PublishIdleAsync("Last", contextLines: 10);

        Assert.Equal(Enumerable.Range(2, 10).Select(i => "L" + i), rig.Ai.Requests[^1].Context.Select(c => c.Source));
    }

    [Fact]
    public async Task ContextLines0_SendsNoContext()
    {
        using var rig = new Rig();

        await rig.PublishIdleAsync("A", contextLines: 0);
        await rig.PublishIdleAsync("B", contextLines: 0);

        Assert.Empty(rig.Ai.Requests[1].Context);
    }

    [Fact]
    public async Task ResetContext_Clears()
    {
        using var rig = new Rig();

        await rig.PublishIdleAsync("A");
        rig.Refiner.ResetContext();
        await rig.PublishIdleAsync("B");

        Assert.Empty(rig.Ai.Requests[1].Context);
    }

    [Fact]
    public async Task Queue_TranslatesEveryLineInOrder()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        var a = rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        var b = rig.Publish("B");
        var c = rig.Publish("C");

        Assert.True(rig.Get(b.Id).AwaitingAi);
        Assert.True(rig.Get(c.Id).AwaitingAi);

        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal(["A", "B", "C"], rig.Ai.Texts);
        AssertRefined(rig.Get(a.Id), "ai:A");
        AssertRefined(rig.Get(b.Id), "ai:B");
        AssertRefined(rig.Get(c.Id), "ai:C");
    }

    [Fact]
    public async Task Queue_FullDropsOldestWaiting()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        var waiting = new[] { "B", "C", "D", "E", "F" }.Select(t => rig.Publish(t)).ToArray();

        Assert.Equal(AiRefiner.DefaultMaxWaiting, rig.Refiner.MaxWaiting);
        Assert.All(waiting[..2], e => AssertUntranslated(rig.Get(e.Id))); // settled at once: the queue was full
        Assert.All(waiting[2..], e => Assert.True(rig.Get(e.Id).AwaitingAi));

        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal(["A", "D", "E", "F"], rig.Ai.Texts);
        Assert.All(waiting[..2], e => AssertUntranslated(rig.Get(e.Id)));
        Assert.All(waiting[2..], e => AssertRefined(rig.Get(e.Id), "ai:" + e.Source));
    }

    [Fact]
    public async Task Queue_KeepsAsManyAsSet()
    {
        using var rig = new Rig();
        rig.Refiner.MaxWaiting = 1;
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        var waiting = new[] { "B", "C" }.Select(t => rig.Publish(t)).ToArray();

        AssertUntranslated(rig.Get(waiting[0].Id)); // only one may wait
        Assert.True(rig.Get(waiting[1].Id).AwaitingAi);

        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();
        Assert.Equal(["A", "C"], rig.Ai.Texts);
    }

    [Fact]
    public async Task Queue_LoweredWhileFull_DropsTheOldestOnTheNextLine()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        var waiting = new[] { "B", "C", "D" }.Select(t => rig.Publish(t)).ToArray();
        rig.Refiner.MaxWaiting = 1;
        var e = rig.Publish("E");

        Assert.All(waiting, w => AssertUntranslated(rig.Get(w.Id)));
        Assert.True(rig.Get(e.Id).AwaitingAi);

        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();
        Assert.Equal(["A", "E"], rig.Ai.Texts);
    }

    [Fact]
    public async Task Parallel_SendsUpToMaxParallelAtOnce_ThenTheOldestWaiting()
    {
        using var rig = new Rig();
        rig.Refiner.MaxParallel = 3;
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        var lines = new[] { "A", "B", "C", "D", "E" }.Select(t => rig.Publish(t)).ToArray();
        await rig.Ai.WaitForCallsAsync(3);

        Assert.Equal(["A", "B", "C"], rig.Ai.Texts); // D and E wait for a free slot
        Assert.All(lines, e => Assert.True(rig.Get(e.Id).AwaitingAi));

        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();
        Assert.Equal(["A", "B", "C", "D", "E"], rig.Ai.Texts.Order()); // D and E go out on two lanes at once
        Assert.All(lines, e => AssertRefined(rig.Get(e.Id), "ai:" + e.Source));
    }

    [Fact]
    public async Task Parallel_AnswersOutOfOrder_EachLandsOnItsLine()
    {
        using var rig = new Rig();
        rig.Refiner.MaxParallel = 2;
        var slow = FakeAiTranslator.NewGate();
        rig.Ai.Returns("ai:A", slow).Returns("ai:B");

        var a = rig.Publish("A");
        var b = rig.Publish("B");
        await rig.Ai.WaitForCallsAsync(2);
        await WaitUntil(() => !rig.Get(b.Id).AwaitingAi);

        AssertRefined(rig.Get(b.Id), "ai:B"); // B came back first
        Assert.True(rig.Get(a.Id).AwaitingAi);

        slow.SetResult();
        await rig.Refiner.WhenIdleAsync();
        AssertRefined(rig.Get(a.Id), "ai:A");
    }

    [Fact]
    public async Task Parallel_RaisedWhileLinesWait_SendsTheWaitingFirst()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        rig.Publish("B");
        rig.Refiner.MaxParallel = 3;
        rig.Publish("C");
        await rig.Ai.WaitForCallsAsync(3);

        Assert.Equal(["A", "B", "C"], rig.Ai.Texts);
        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 10)]
    public void MaxParallel_IsClamped(int set, int expected)
    {
        using var rig = new Rig();
        rig.Refiner.MaxParallel = set;
        Assert.Equal(expected, rig.Refiner.MaxParallel);
    }

    [Fact]
    public void MaxParallel_DefaultsToOne()
    {
        using var rig = new Rig();
        Assert.Equal(1, rig.Refiner.MaxParallel);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(5);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 30)]
    public void MaxWaiting_IsClamped(int set, int expected)
    {
        using var rig = new Rig();
        rig.Refiner.MaxWaiting = set;
        Assert.Equal(expected, rig.Refiner.MaxWaiting);
    }

    [Fact]
    public async Task Queue_ContextIncludesTranslationFinishedWhileWaiting()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Returns("ai:A", gate);

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        rig.Publish("B");
        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();

        var context = rig.Ai.Requests[1].Context;
        Assert.Equal(("A", "ai:A"), (context[^1].Source, context[^1].Translation));
    }

    [Fact]
    public async Task Queue_ContextLost_NeverUsesLaterLines()
    {
        // The context was reset (settings changed) while B waited, and D came after: B's "previous lines" must not be D.
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Returns("ai:A", gate);

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        rig.Publish("B");
        rig.Refiner.ResetContext();
        rig.Publish("D");
        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal(["A", "B", "D"], rig.Ai.Texts);
        Assert.Empty(rig.Ai.Requests[1].Context);
    }

    [Fact]
    public async Task Queue_PauseSettlesAllWaiting()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Unavailable, 503), gate);

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        var b = rig.Publish("B");
        var c = rig.Publish("C");
        var d = rig.Publish("D");
        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal(["A"], rig.Ai.Texts);
        AssertUntranslated(rig.Get(b.Id));
        AssertUntranslated(rig.Get(c.Id));
        AssertUntranslated(rig.Get(d.Id));
    }

    [Fact]
    public async Task Dispose_DropsWaiting()
    {
        var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        rig.Publish("B");
        rig.Dispose();
        gate.SetResult();
        await rig.Refiner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["A"], rig.Ai.Texts);
    }

    [Fact]
    public async Task RefineOfOldEntry_DoesNotTouchCurrent()
    {
        using var rig = new Rig();
        var gateA = FakeAiTranslator.NewGate();
        var gateC = FakeAiTranslator.NewGate();
        rig.Ai.Returns("ai:A", gateA).Returns("ai:C", gateC);

        var a = rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        var c = rig.Publish("C");

        gateA.SetResult();
        await rig.Ai.WaitForCallsAsync(2); // C's call started, so A's result has been applied

        AssertRefined(rig.Get(a.Id), "ai:A");
        var current = rig.Get(c.Id);
        Assert.Equal(((string?)null, true), (current.Translated, current.AwaitingAi));

        gateC.SetResult();
        await rig.Refiner.WhenIdleAsync();
        AssertRefined(rig.Get(c.Id), "ai:C");
    }

    [Fact]
    public async Task Unavailable_Pauses30s_ThenRetries()
    {
        using var rig = new Rig();
        var t0 = rig.Time.GetUtcNow();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Unavailable, 503));

        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Equal(("AiDown", (DateTimeOffset?)t0.AddSeconds(30)), (rig.Status.AiError, rig.Status.AiRetryAt));

        rig.Time.Advance(TimeSpan.FromSeconds(10));
        AssertUntranslated(await rig.PublishIdleAsync("B"));
        Assert.Single(rig.Ai.Requests);

        rig.Time.Advance(TimeSpan.FromSeconds(20));
        AssertRefined(await rig.PublishIdleAsync("C"), "ai:C");
        Assert.Equal(2, rig.Ai.Requests.Count);
        Assert.Equal((null, (DateTimeOffset?)null), (rig.Status.AiError, rig.Status.AiRetryAt));
    }

    [Fact]
    public async Task RateLimited_Pauses60s()
    {
        using var rig = new Rig();
        var t0 = rig.Time.GetUtcNow();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.RateLimited, 429));

        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Equal(("AiRateLimited", (DateTimeOffset?)t0.AddSeconds(60)), (rig.Status.AiError, rig.Status.AiRetryAt));

        rig.Time.Advance(TimeSpan.FromSeconds(59));
        AssertUntranslated(await rig.PublishIdleAsync("B"));
        Assert.Single(rig.Ai.Requests);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        AssertRefined(await rig.PublishIdleAsync("C"), "ai:C");
        Assert.Null(rig.Status.AiError);
    }

    [Fact]
    public async Task Auth_StopsUntilGenerationChanges()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Auth, 401));

        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Equal(("AiAuth", (DateTimeOffset?)null), (rig.Status.AiError, rig.Status.AiRetryAt));

        rig.Time.Advance(TimeSpan.FromHours(1));
        AssertUntranslated(await rig.PublishIdleAsync("B"));
        Assert.Single(rig.Ai.Requests);
        Assert.Equal("AiAuth", rig.Status.AiError);

        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 2);
        AssertRefined(await rig.PublishIdleAsync("C"), "ai:C");
        Assert.Equal(2, rig.Ai.Requests.Count);
        Assert.Null(rig.Status.AiError);
    }

    [Fact]
    public async Task AiRefiner_ModelNotFound_StopsUntilGenerationChanges()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.ModelNotFound, 404));

        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Equal(("AiModelNotFound", (DateTimeOffset?)null), (rig.Status.AiError, rig.Status.AiRetryAt));

        rig.Time.Advance(TimeSpan.FromHours(1));
        AssertUntranslated(await rig.PublishIdleAsync("B"));
        Assert.Single(rig.Ai.Requests);
        Assert.Equal("AiModelNotFound", rig.Status.AiError);

        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 2); // model name fixed
        rig.Refiner.Refresh();
        Assert.Null(rig.Status.AiError);
        AssertRefined(await rig.PublishIdleAsync("C"), "ai:C");
        Assert.Equal(2, rig.Ai.Requests.Count);
    }

    [Fact]
    public async Task AiRefiner_Billing_StopsUntilGenerationChanges()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Billing, 400, "Your credit balance is too low"));

        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Equal(("AiBilling", (DateTimeOffset?)null), (rig.Status.AiError, rig.Status.AiRetryAt));

        rig.Time.Advance(TimeSpan.FromHours(1));
        AssertUntranslated(await rig.PublishIdleAsync("B"));
        Assert.Single(rig.Ai.Requests);

        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 2); // credit added, settings touched
        rig.Refiner.Refresh();
        Assert.Null(rig.Status.AiError);
    }

    [Fact]
    public async Task InFlightFailure_AfterRebuildNotYetSeen_DoesNotPause()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Auth, 401), gate);

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 2); // rebuilt, but no Refresh or new line yet
        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();

        Assert.Null(rig.Status.AiError);
        AssertRefined(await rig.PublishIdleAsync("B"), "ai:B");
    }

    [Fact]
    public async Task UnexpectedLaneFault_StillSettlesEntry()
    {
        using var rig = new Rig();
        var entry = rig.Hub.Publish("A", null, "en", null, awaitingAi: true);

        // A broken context makes building the request throw inside the lane, outside the translator call.
        rig.Refiner.OnPublished(entry, new AiJobContext(null!, "th", null, 4));
        await rig.Refiner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Ai.Requests);
        AssertUntranslated(rig.Get(entry.Id));
    }

    [Fact]
    public async Task NewGeneration_ClearsTimedPause()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.RateLimited, 429));
        await rig.PublishIdleAsync("A");

        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 2);
        AssertRefined(await rig.PublishIdleAsync("B"), "ai:B");
        Assert.Null(rig.Status.AiError);
    }

    [Fact]
    public async Task Refresh_NewGeneration_ClearsAuthPauseAndStatus()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Auth, 401));
        await rig.PublishIdleAsync("A");
        Assert.Equal("AiAuth", rig.Status.AiError);

        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 2); // key fixed
        rig.Refiner.Refresh();

        Assert.Null(rig.Status.AiError); // before any new line
        AssertRefined(await rig.PublishIdleAsync("B"), "ai:B");
    }

    [Fact]
    public void Refresh_ConfigError_SetsStatusImmediately()
    {
        using var rig = new Rig();
        rig.Source.Current = new AiTranslatorHandle(null, "AiKeyNeeded", 2);

        rig.Refiner.Refresh();

        Assert.Equal(("AiKeyNeeded", (DateTimeOffset?)null), (rig.Status.AiError, rig.Status.AiRetryAt));
        Assert.Empty(rig.Ai.Requests);
    }

    [Fact]
    public void Refresh_NeverThrows_AndIsIgnoredAfterDispose()
    {
        var rig = new Rig();
        rig.Dispose();
        rig.Source.Current = new AiTranslatorHandle(null, "AiKeyNeeded", 2);

        rig.Refiner.Refresh();

        Assert.Null(rig.Status.AiError);
    }

    [Fact]
    public async Task OlderGeneration_DoesNotResetAuthPause()
    {
        using var rig = new Rig();
        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 2);
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Auth, 401));
        await rig.PublishIdleAsync("A");

        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 1); // a stale read: generations only increase
        AssertUntranslated(await rig.PublishIdleAsync("B"));
        rig.Refiner.Refresh();
        Assert.Single(rig.Ai.Requests);
        Assert.Equal("AiAuth", rig.Status.AiError);

        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 3);
        AssertRefined(await rig.PublishIdleAsync("C"), "ai:C");
    }

    [Fact]
    public async Task SuccessFromOlderGeneration_DoesNotClearNewStatus()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Returns("ai:A", gate);

        var a = rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        rig.Source.Current = new AiTranslatorHandle(null, "AiKeyNeeded", 2); // settings changed mid-call
        rig.Refiner.Refresh();
        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();

        AssertRefined(rig.Get(a.Id), "ai:A"); // the answer is still good for its line
        Assert.Equal("AiKeyNeeded", rig.Status.AiError);
    }

    [Fact]
    public async Task ConfigError_SetsStatusNoCall()
    {
        using var rig = new Rig();
        rig.Source.Current = new AiTranslatorHandle(null, "AiKeyNeeded", 1);
        Assert.False(rig.Refiner.IsActive);

        var entry = rig.Hub.Publish("A", null, "en", null, awaitingAi: true);
        rig.Refiner.OnPublished(entry, Rig.Ctx());
        await rig.Refiner.WhenIdleAsync();

        Assert.Empty(rig.Ai.Requests);
        AssertUntranslated(rig.Get(entry.Id));
        Assert.Equal(("AiKeyNeeded", (DateTimeOffset?)null), (rig.Status.AiError, rig.Status.AiRetryAt));
    }

    [Fact]
    public async Task PendingEntry_SettledWhenPauseBeganWhileItWaited()
    {
        using var rig = new Rig();
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Unavailable, 503), gate);

        rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        var b = rig.Publish("B");
        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal(["A"], rig.Ai.Texts);
        AssertUntranslated(rig.Get(b.Id));
    }

    [Fact]
    public async Task EmptyAnswer_LeavesLineUntranslated()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.BadResponse, 200));

        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Equal("AiDown", rig.Status.AiError);
    }

    // One odd answer (the model explained itself, ran on, or said nothing) is that line's problem, not the AI's: the
    // next line is sent at once instead of after a 30 s pause.
    [Fact]
    public async Task UnusableAnswer_SettlesTheLine_DoesNotPause()
    {
        using var rig = new Rig();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.BadResponse, null, unusableAnswer: true));

        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Null(rig.Status.AiError);

        await rig.PublishIdleAsync("B");
        Assert.Equal(["A", "B"], rig.Ai.Texts);
    }

    [Fact]
    public async Task UnusableAnswer_ThreeInARow_Pauses()
    {
        using var rig = new Rig();
        var t0 = rig.Time.GetUtcNow();
        var unusable = new AiTranslationException(AiErrorKind.BadResponse, null, unusableAnswer: true);
        rig.Ai.Throws(unusable).Throws(unusable).Throws(unusable);

        await rig.PublishIdleAsync("A");
        await rig.PublishIdleAsync("B");
        Assert.Null(rig.Status.AiError);
        await rig.PublishIdleAsync("C");
        Assert.Equal(("AiDown", (DateTimeOffset?)t0.AddSeconds(30)), (rig.Status.AiError, rig.Status.AiRetryAt));
    }

    [Fact]
    public async Task UnusableAnswer_CountResetsOnSuccess()
    {
        using var rig = new Rig();
        var unusable = new AiTranslationException(AiErrorKind.BadResponse, null, unusableAnswer: true);

        rig.Ai.Throws(unusable).Throws(unusable).Returns("ok").Throws(unusable).Throws(unusable);
        await rig.PublishIdleAsync("A");
        await rig.PublishIdleAsync("B");
        await rig.PublishIdleAsync("C");
        await rig.PublishIdleAsync("D");
        await rig.PublishIdleAsync("E");
        Assert.Null(rig.Status.AiError);
    }

    [Fact]
    public async Task UnexpectedException_TreatedAsUnavailable()
    {
        using var rig = new Rig();
        var t0 = rig.Time.GetUtcNow();
        rig.Ai.Throws(new InvalidOperationException("boom"));

        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Equal(("AiDown", (DateTimeOffset?)t0.AddSeconds(30)), (rig.Status.AiError, rig.Status.AiRetryAt));
    }

    [Fact]
    public async Task Dispose_CancelsInFlight_AndDropsSilently()
    {
        var rig = new Rig();
        rig.Ai.Gate = FakeAiTranslator.NewGate(); // never released: only cancellation ends the call

        var a = rig.Publish("A");
        await rig.Ai.WaitForCallsAsync(1);
        var b = rig.Publish("B");
        rig.Dispose();
        await rig.Refiner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["A"], rig.Ai.Texts);
        Assert.True(rig.Get(a.Id).AwaitingAi);
        Assert.True(rig.Get(b.Id).AwaitingAi);
        Assert.Null(rig.Status.AiError);

        rig.Refiner.OnPublished(rig.Hub.Publish("C", null, "en", null, awaitingAi: true), Rig.Ctx());
        Assert.Single(rig.Ai.Requests);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotStopTheLane()
    {
        using var rig = new Rig();
        rig.Hub.Changed += (_, e) =>
        {
            if (e is SubtitleRefined)
                throw new InvalidOperationException("subscriber");
        };

        await rig.PublishIdleAsync("A");
        var b = await rig.PublishIdleAsync("B");

        Assert.Equal(["A", "B"], rig.Ai.Texts);
        AssertRefined(b, "ai:B");
    }

    [Fact]
    public async Task Refine_UsesDialogue_NotNames()
    {
        using var rig = new Rig();
        var entry = rig.Hub.Publish("Lucia: Where?\nJason: Out.", null, "en", null, awaitingAi: true,
            dialogue: "Where?\nOut.", speakers: [new SpeakerTag("ลูเซีย", null), new SpeakerTag("เจสัน", null)]);
        rig.Refiner.OnPublished(entry, Rig.Ctx());
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal("Where?\nOut.", Assert.Single(rig.Ai.Requests).Text);
        Assert.Equal("ai:Where?\nOut.", rig.Get(entry.Id).Translated);
        Assert.Equal(2, rig.Get(entry.Id).Speakers!.Count);
    }

    [Fact]
    public async Task TranslateNames_ReturnsOnePerName()
    {
        using var rig = new Rig();
        rig.Ai.Map = r => r.Kind == AiRequestKind.Names ? "ลูเซีย\nเจสัน" : "x";
        var names = await rig.Refiner.TranslateNamesAsync(["Lucia", "Jason"], Rig.Ctx(), CancellationToken.None);
        Assert.Equal(["ลูเซีย", "เจสัน"], names);
    }

    [Fact]
    public async Task TranslateNames_CountMismatch_Null()
    {
        using var rig = new Rig();
        rig.Ai.Map = _ => "ลูเซีย";
        Assert.Null(await rig.Refiner.TranslateNamesAsync(["Lucia", "Jason"], Rig.Ctx(), CancellationToken.None));
    }

    private static readonly IReadOnlyList<SpeakerTag?> Two = [new SpeakerTag("ลูเซีย", null), new SpeakerTag("เจสัน", null)];

    [Fact]
    public async Task Refine_LineCountMismatch_KeepsTextDropsSpeakers()
    {
        using var rig = new Rig();
        rig.Ai.Map = _ => "all on one line";
        var entry = rig.Hub.Publish("Lucia: A\nJason: B", null, "en", null, awaitingAi: true, dialogue: "A\nB", speakers: Two);
        rig.Refiner.OnPublished(entry, Rig.Ctx());
        await rig.Refiner.WhenIdleAsync();

        // The text without names beats names on the wrong lines.
        var e = rig.Get(entry.Id);
        Assert.Equal(("all on one line", false), (e.Translated, e.AwaitingAi));
        Assert.Null(e.Speakers);
        Assert.Null(e.Dialogue);
    }

    [Fact]
    public async Task Refine_BlankLines_Dropped()
    {
        using var rig = new Rig();
        rig.Ai.Map = _ => "ก\r\n\nข\n";
        var entry = rig.Hub.Publish("Lucia: A\nJason: B", null, "en", null, awaitingAi: true, dialogue: "A\nB", speakers: Two);
        rig.Refiner.OnPublished(entry, Rig.Ctx());
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal("ก\nข", rig.Get(entry.Id).Translated);
    }

    // ---- Library ----

    private static readonly GameInfo Gta = new("GTA", "Leonida, today", [
        new GameCharacter("Lucia", ["Lu"], "female", "ลูเซีย", null, null, null, null),
        new GameCharacter("Jason", ["Jay"], "male", "เจสัน", null, null, null, null),
    ], []);

    private ActiveLibrary NewLibrary(GameInfo? info = null, string style = "Keep it casual.") =>
        ActiveLibrary.Build(
            new LibraryData("gta6", new LibraryVersion(20261121, 1), null, [], [], info ?? Gta, style));

    [Fact]
    public async Task Refiner_AddsBrief_WhenLibraryActive()
    {
        using var rig = new Rig(NewLibrary());
        var entry = rig.Hub.Publish("Where is Lucia?", null, "en", null, awaitingAi: true);
        rig.Refiner.OnPublished(entry, Rig.Ctx() with { Library = rig.Library, SpeakerLabels = ["Jay"] });
        await rig.Refiner.WhenIdleAsync();

        var brief = Assert.Single(rig.Ai.Requests).Brief;
        Assert.NotNull(brief);
        Assert.Contains("Keep it casual.", brief.Static);
        Assert.Contains("Leonida, today", brief.Static);
        Assert.Contains("Jason:", brief.PerLine); // the speaker label
        Assert.Contains("Lucia:", brief.PerLine); // mentioned in the text
    }

    [Fact]
    public async Task Refiner_NoBrief_WithoutLibrary_OrWhenEmpty()
    {
        using var none = new Rig(null);
        await none.PublishIdleAsync("Where is Lucia?");
        Assert.Null(Assert.Single(none.Ai.Requests).Brief);

        using var empty = new Rig(NewLibrary(GameInfo.Empty, style: ""));
        await empty.PublishIdleAsync("Where is Lucia?");
        Assert.Null(Assert.Single(empty.Ai.Requests).Brief);
    }

    [Theory]
    [InlineData("en", "ja")]
    [InlineData("ja", "th")]
    public async Task Refiner_NotEnToTh_NoBrief_NothingLearned(string source, string target)
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);
        var entry = rig.Hub.Publish("Where is Lucia?", null, source, null, awaitingAi: true);
        rig.Refiner.OnPublished(entry, new AiJobContext(SourceLanguages.Get(source), target, null, 4, Library: lib));
        await rig.Refiner.WhenIdleAsync();

        Assert.Null(Assert.Single(rig.Ai.Requests).Brief);
        AssertRefined(rig.Get(entry.Id), "ai:Where is Lucia?");
        Assert.Equal(0, lib.Matcher.LearnedCount);
    }

    [Fact]
    public async Task Refiner_TeachesMatcherPerSpeaker_WritesNoThai()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);
        rig.Source.Current = new AiTranslatorHandle(rig.Ai, null, 1, "test/model");
        rig.Ai.Map = _ => "ไปไหน\nออกไป";
        var entry = rig.Hub.Publish("Lu: Where?\nJay: Out.", null, "en", null, awaitingAi: true, dialogue: "Where?\nOut.", speakers: Two);
        rig.Refiner.OnPublished(entry, Rig.Ctx() with { Library = rig.Library, SpeakerLabels = ["Lu", "Jay"] });
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal(2, lib.Matcher.LearnedCount);
        Assert.Equal("ไปไหน", lib.Matcher.Lookup("Where?", "Lu")?.Thai); // taught to the matcher at once
        Assert.Equal("ออกไป", lib.Matcher.Lookup("Out.", null)?.Thai);
        if (Directory.Exists(_dir)) // the answer stays in memory: no file holds the Thai
            Assert.All(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories),
                f => Assert.DoesNotContain("ไปไหน", File.ReadAllText(f)));
    }

    [Fact]
    public async Task Learn_Off_WritesNothing()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);

        rig.PublishIn("z1", "Where are you going?", learn: false);
        await rig.Refiner.WhenIdleAsync();
        Assert.Equal(0, lib.Matcher.LearnedCount);

        rig.PublishIn("z1", "Just out for a bit.");
        await rig.Refiner.WhenIdleAsync();
        Assert.Equal(1, lib.Matcher.LearnedCount);
    }

    [Fact]
    public async Task Context_IsPerZone()
    {
        using var rig = new Rig();

        rig.PublishIn("z1", "A1");
        await rig.Refiner.WhenIdleAsync();
        rig.PublishIn("z2", "B1");
        await rig.Refiner.WhenIdleAsync();
        rig.PublishIn("z1", "A2");
        await rig.Refiner.WhenIdleAsync();
        rig.PublishIn("z2", "B2");
        await rig.Refiner.WhenIdleAsync();

        var requests = rig.Ai.Requests;
        Assert.Empty(requests[0].Context);
        Assert.Empty(requests[1].Context);
        Assert.Equal([("A1", "ai:A1")], Pairs(requests[2]));
        Assert.Equal([("B1", "ai:B1")], Pairs(requests[3]));
    }

    [Fact]
    public async Task PhoneBurst_DoesNotDropDialogueLine()
    {
        using var rig = new Rig();
        rig.Refiner.MaxWaiting = 3;
        var gate = FakeAiTranslator.NewGate();
        rig.Ai.Gate = gate;

        rig.PublishIn("z2", "P0"); // in flight
        await rig.Ai.WaitForCallsAsync(1);
        var dialogue = rig.PublishIn("z1", "D1");
        var phone = new[] { "P1", "P2", "P3", "P4" }.Select(t => rig.PublishIn("z2", t)).ToArray();

        AssertUntranslated(rig.Get(phone[0].Id)); // the oldest of the burst's own zone
        Assert.True(rig.Get(dialogue.Id).AwaitingAi);
        Assert.All(phone[1..], e => Assert.True(rig.Get(e.Id).AwaitingAi));

        gate.SetResult();
        await rig.Refiner.WhenIdleAsync();
        Assert.Equal(["P0", "D1", "P2", "P3", "P4"], rig.Ai.Texts); // oldest first across zones
    }

    [Fact]
    public async Task Refiner_RecordsOneLine_WhenLinesDoNotMatchSpeakers()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);
        rig.Ai.Map = _ => "ไปไหน ออกไป";
        var entry = rig.Hub.Publish("Lu: Where?\nJay: Out.", null, "en", null, awaitingAi: true, dialogue: "Where?\nOut.", speakers: Two);
        rig.Refiner.OnPublished(entry, Rig.Ctx() with { Library = rig.Library, SpeakerLabels = ["Lu", "Jay"] });
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal(1, lib.Matcher.LearnedCount);
        Assert.Equal("ไปไหน ออกไป", lib.Matcher.Lookup("Where?\nOut.", null)?.Thai);
    }

    [Fact]
    public async Task Refiner_FailedOrSettledLine_NotLearned()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Unavailable, 503));

        await rig.PublishIdleAsync("Where are you going?"); // fails
        await rig.PublishIdleAsync("Just out for a bit."); // settled during the pause

        Assert.Equal(0, lib.Matcher.LearnedCount);
    }

    [Fact]
    public async Task RememberTranslated_IsContext_NotSent()
    {
        using var rig = new Rig();
        var hit = rig.Hub.Publish("A", "ก", "en", null, awaitingAi: false);
        rig.Refiner.RememberTranslated(hit);
        await rig.PublishIdleAsync("B");

        Assert.Equal(["B"], rig.Ai.Texts);
        Assert.Equal([("A", (string?)"ก")], Pairs(rig.Ai.Requests[0]));
    }

    // ---- Bubbles already translated ----

    /// <summary>Publishes bubbles <paramref name="text"/> with the parts already known, as the pipeline does.</summary>
    private static SubtitleEntry PublishParts(Rig rig, string text, IReadOnlyList<string?>? known, PartMemory memory)
    {
        var entry = rig.Hub.Publish(text, null, "en", null, awaitingAi: true, zoneId: "z1");
        rig.Refiner.OnPublished(entry, Rig.Ctx() with { KnownParts = known, Memory = memory });
        return entry;
    }

    [Fact]
    public async Task PartialRequest_SendsOnlyUnknownParts()
    {
        using var rig = new Rig();
        var memory = new PartMemory();

        var entry = PublishParts(rig, "A\nB\nC", ["ก", null, "ค"], memory);
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal("B", Assert.Single(rig.Ai.Requests).Text);
        AssertRefined(rig.Get(entry.Id), "ก\nai:B\nค");
        Assert.Equal("ai:B", memory.Get("B"));
    }

    [Fact]
    public async Task PartialRequest_ContextKeepsTheWholeLine()
    {
        using var rig = new Rig();
        var memory = new PartMemory();

        PublishParts(rig, "A\nB", ["ก", null], memory);
        await rig.Refiner.WhenIdleAsync();
        rig.PublishIn("z1", "C");
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal([("A\nB", (string?)"ก\nai:B")], Pairs(rig.Ai.Requests[1]));
    }

    [Fact]
    public async Task PartialRequest_LineCountMismatch_ShowsTheAnswerWithoutSpeakers_RemembersNothing()
    {
        using var rig = new Rig();
        rig.Ai.Map = _ => "x\ny";
        var memory = new PartMemory();
        var entry = rig.Hub.Publish("Lu: A\nJay: B\nLu: C", null, "en", null, awaitingAi: true, zoneId: "z1",
            dialogue: "A\nB\nC", speakers: [.. Two, new SpeakerTag("ลูเซีย", null)]);

        rig.Refiner.OnPublished(entry, Rig.Ctx() with { KnownParts = ["ก", null, "ค"], Memory = memory });
        await rig.Refiner.WhenIdleAsync();

        var e = rig.Get(entry.Id);
        Assert.Equal(("x\ny", false), (e.Translated, e.AwaitingAi));
        Assert.Null(e.Speakers);
        Assert.Null(memory.Get("B"));
    }

    [Fact]
    public async Task NothingKnown_WholeLineSent_EveryPartRemembered()
    {
        using var rig = new Rig();
        rig.Ai.Map = r => string.Join('\n', r.Text.Split('\n').Select(l => "ai:" + l));
        var memory = new PartMemory();

        var entry = PublishParts(rig, "A\nB", null, memory);
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal("A\nB", Assert.Single(rig.Ai.Requests).Text);
        AssertRefined(rig.Get(entry.Id), "ai:A\nai:B");
        Assert.Equal(("ai:A", "ai:B"), (memory.Get("A"), memory.Get("B")));
    }

    [Fact]
    public async Task NothingKnown_LineCountMismatch_RemembersNothing()
    {
        using var rig = new Rig();
        rig.Ai.Map = _ => "one line";
        var memory = new PartMemory();

        var entry = PublishParts(rig, "A\nB", null, memory);
        await rig.Refiner.WhenIdleAsync();

        AssertRefined(rig.Get(entry.Id), "one line");
        Assert.Null(memory.Get("A"));
    }

    [Fact]
    public async Task Bubbles_WithoutNames_LearnedOneRowPerBubble()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);
        rig.Ai.Map = r => string.Join('\n', r.Text.Split('\n').Select(l => "ai:" + l));
        var entry = rig.Hub.Publish("Where?\nOut.", null, "en", null, awaitingAi: true, zoneId: "z1");

        rig.Refiner.OnPublished(entry, Rig.Ctx() with { Library = lib, Memory = new PartMemory() });
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal(2, lib.Matcher.LearnedCount);
        Assert.Equal("ai:Where?", lib.Matcher.Lookup("Where?", null)?.Thai);
        Assert.Equal("ai:Out.", lib.Matcher.Lookup("Out.", null)?.Thai);
    }

    [Fact]
    public async Task Bubbles_LineCountMismatch_NothingLearned()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);
        rig.Ai.Map = _ => "one line";
        var entry = rig.Hub.Publish("Where?\nOut.", null, "en", null, awaitingAi: true, zoneId: "z1");

        rig.Refiner.OnPublished(entry, Rig.Ctx() with { Library = lib, Memory = new PartMemory() });
        await rig.Refiner.WhenIdleAsync();

        AssertRefined(rig.Get(entry.Id), "one line");
        Assert.Equal(0, lib.Matcher.LearnedCount);
    }

    [Fact]
    public async Task PartialRequest_LearnsOnlyThePartsTranslated()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);
        var entry = rig.Hub.Publish("Lu: Where?\nJay: Out.", null, "en", null, awaitingAi: true, zoneId: "z1",
            dialogue: "Where?\nOut.", speakers: Two);

        rig.Refiner.OnPublished(entry, Rig.Ctx() with
        {
            Library = lib, SpeakerLabels = ["Lu", "Jay"], KnownParts = ["ไปไหน", null],
            Memory = new PartMemory(),
        });
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal("Out.", Assert.Single(rig.Ai.Requests).Text);
        // "Where?" came from the library or the memory: it is not recorded again.
        Assert.Equal(1, lib.Matcher.LearnedCount);
        Assert.Equal("ai:Out.", lib.Matcher.Lookup("Out.", "Jay")?.Thai);
        Assert.Null(lib.Matcher.Lookup("Where?", null));
    }

    // ---- Google ----

    [Fact]
    public async Task Google_SendsNoContextNoBrief_WithCodes()
    {
        using var rig = new Rig(NewLibrary(), google: true);

        await rig.PublishIdleAsync("A");
        await rig.PublishIdleAsync("B");

        var request = rig.Ai.Requests[1];
        Assert.Equal("B", request.Text);
        Assert.Empty(request.Context);
        Assert.Null(request.Brief);
        Assert.Equal(("en", "th"), (request.SourceCode, request.TargetCode));
    }

    [Fact]
    public async Task Ai_SendsCodesToo()
    {
        using var rig = new Rig();

        await rig.PublishIdleAsync("A");

        var request = Assert.Single(rig.Ai.Requests);
        Assert.Equal(("en", "th"), (request.SourceCode, request.TargetCode));
    }

    [Fact]
    public async Task Google_DoesNotTeachMatcher()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib, google: true);

        AssertRefined(await rig.PublishIdleAsync("Where are you going?"), "ai:Where are you going?");

        Assert.Equal(0, lib.Matcher.LearnedCount);
    }

    [Fact]
    public async Task Ai_StillTeachesMatcher()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib);

        AssertRefined(await rig.PublishIdleAsync("Where are you going?"), "ai:Where are you going?");

        Assert.Equal(1, lib.Matcher.LearnedCount);
    }

    [Fact]
    public async Task Google_Bubbles_StillPutInZoneMemory_NothingLearned()
    {
        var lib = NewLibrary();
        using var rig = new Rig(lib, google: true);
        rig.Ai.Map = r => string.Join('\n', r.Text.Split('\n').Select(l => "ai:" + l));
        var memory = new PartMemory();
        var entry = rig.Hub.Publish("Where?\nOut.", null, "en", null, awaitingAi: true, zoneId: "z1");

        rig.Refiner.OnPublished(entry, Rig.Ctx() with { Library = lib, Memory = memory });
        await rig.Refiner.WhenIdleAsync();

        Assert.Equal("ai:Where?", memory.Get("Where?"));
        Assert.Equal(0, lib.Matcher.LearnedCount);
    }

    [Fact]
    public async Task Google_NamesReturnNull()
    {
        using var rig = new Rig(google: true);

        Assert.Null(await rig.Refiner.TranslateNamesAsync(["Lucia"], Rig.Ctx(), CancellationToken.None));
        Assert.Empty(rig.Ai.Requests);
    }

    [Fact]
    public async Task Google_Failure_ShowsGoogleDown()
    {
        using var rig = new Rig(google: true);
        var t0 = rig.Time.GetUtcNow();
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Unavailable, null));

        AssertUntranslated(await rig.PublishIdleAsync("A"));

        Assert.Equal((AiRefiner.GoogleDownKey, (DateTimeOffset?)t0.AddSeconds(30)), (rig.Status.AiError, rig.Status.AiRetryAt));
        Assert.Equal("GoogleDown", AiRefiner.GoogleDownKey);
    }

    [Fact]
    public async Task Google_Paused_ThenAiHandle_ClearsStatus()
    {
        using var rig = new Rig(google: true);
        rig.Ai.Throws(new AiTranslationException(AiErrorKind.Unavailable, null));
        AssertUntranslated(await rig.PublishIdleAsync("A"));
        Assert.Equal("GoogleDown", rig.Status.AiError);

        var otherAi = new FakeAiTranslator();
        rig.Source.Current = new AiTranslatorHandle(otherAi, null, 2);
        rig.Refiner.Refresh();
        Assert.Null(rig.Status.AiError);

        AssertRefined(await rig.PublishIdleAsync("B"), "ai:B");
        Assert.Equal("B", Assert.Single(otherAi.Requests).Text);
        Assert.Single(rig.Ai.Requests);
    }
}
