using PSRemoteSub.Core.Pipeline;

namespace PSRemoteSub.Core.Tests.Pipeline;

public class StabilityFilterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int ms) => T0.AddMilliseconds(ms);

    private static List<StabilityDecision> Run(StabilityFilter f, params string?[] reads)
    {
        var res = new List<StabilityDecision>();
        for (var i = 0; i < reads.Length; i++) res.Add(f.Feed(reads[i], At(i * 300)));
        return res;
    }

    [Fact]
    public void TwoMatchingReads_EmitsStableOnce()
    {
        var r = Run(new StabilityFilter(new StabilityOptions()), "Hello there", "Hello there", "Hello there");
        Assert.Same(StabilityDecision.None, r[0]);
        Assert.Equal("Hello there", Assert.IsType<StabilityDecision.Stable>(r[1]).Text);
        Assert.Same(StabilityDecision.None, r[2]);
    }

    [Fact]
    public void OcrJitter_DoesNotReemit()
    {
        var r = Run(new StabilityFilter(new StabilityOptions()), "Hello there", "Hello there", "Hel1o there", "Hello there");
        Assert.Single(r.OfType<StabilityDecision.Stable>());
    }

    [Fact]
    public void Typewriter_WaitsUntilGrowthStops()
    {
        var r = Run(new StabilityFilter(new StabilityOptions()),
            "We", "We have", "We have to go", "We have to go", "We have to go");
        var stable = Assert.Single(r.OfType<StabilityDecision.Stable>());
        Assert.Equal("We have to go", stable.Text);
    }

    [Fact]
    public void NewLine_EmitsAgain()
    {
        var r = Run(new StabilityFilter(new StabilityOptions()),
            "Where are you going", "Where are you going", "Nobody knows the answer", "Nobody knows the answer");
        var stable = r.OfType<StabilityDecision.Stable>().ToList();
        Assert.Equal(2, stable.Count);
        Assert.Equal("Where are you going", stable[0].Text);
        Assert.Equal("Nobody knows the answer", stable[1].Text);
    }

    [Fact]
    public void NoSubtitle_ClearsAfter1500ms()
    {
        var f = new StabilityFilter(new StabilityOptions());
        f.Feed("Hello there", At(0));
        Assert.IsType<StabilityDecision.Stable>(f.Feed("Hello there", At(300)));
        Assert.Same(StabilityDecision.None, f.Feed(null, At(1000)));
        Assert.Same(StabilityDecision.None, f.Feed(null, At(2200)));
        Assert.IsType<StabilityDecision.Cleared>(f.Feed(null, At(2500)));
        Assert.Same(StabilityDecision.None, f.Feed(null, At(2800)));
    }

    [Fact]
    public void SameLineAfterClear_EmitsAgain()
    {
        var f = new StabilityFilter(new StabilityOptions());
        f.Feed("Hello there", At(0));
        Assert.IsType<StabilityDecision.Stable>(f.Feed("Hello there", At(300)));
        f.Feed(null, At(600));
        Assert.IsType<StabilityDecision.Cleared>(f.Feed(null, At(2100)));
        Assert.Same(StabilityDecision.None, f.Feed("Hello there", At(2400)));
        Assert.IsType<StabilityDecision.Stable>(f.Feed("Hello there", At(2700)));
    }

    [Fact]
    public void ShortGap_DoesNotClear()
    {
        var f = new StabilityFilter(new StabilityOptions());
        var all = new List<StabilityDecision>
        {
            f.Feed("Hello there", At(0)),
            f.Feed("Hello there", At(300)),
            f.Feed(null, At(600)),
            f.Feed("Hello there", At(900)),
            f.Feed("Hello there", At(1200)),
        };
        Assert.Single(all.OfType<StabilityDecision.Stable>());
        Assert.DoesNotContain(all, d => d is StabilityDecision.Cleared);
    }
}
