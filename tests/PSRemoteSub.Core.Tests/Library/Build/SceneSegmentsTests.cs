using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public class SceneSegmentsTests
{
    [Fact]
    public void Build_dedupes_by_normalized_text_and_orders_main_story_first()
    {
        var rows = new[]
        {
            new SceneRow("Open world / side content (data.rpf)", "w@1", "Unknown", "Hey!"),
            new SceneRow("Main story (story000)", "m@1", "Jason", "~sl:0~Hey.~sl:1~Move it"),
            new SceneRow("Main story (story000)", "m@1", "Unknown", "..."),
        };
        var s = SceneSegments.Build(rows);
        Assert.Equal(new[] { "s000001", "s000002" }, s.Select(x => x.Id));
        Assert.Equal(new[] { "Hey.", "Move it" }, s.Select(x => x.En));
        Assert.Equal("Jason", s[0].Speaker);
        Assert.Equal("m@1", s[0].Scene);
        Assert.Equal("Main story (story000)", s[0].Part);
    }

    [Fact]
    public void Build_drops_unknown_speakers_and_keeps_file_order_within_a_part()
    {
        var rows = new[]
        {
            new SceneRow("Minigame (shared_minigame000)", "g@1", "Unknown (other speaker)", "Deal."),
            new SceneRow("Main story (story000)", "m@2", "Unknown", "Second"),
            new SceneRow("Main story (story000)", "m@1", "Lucia", "Third"),
        };
        var s = SceneSegments.Build(rows);
        Assert.Equal(new[] { "Second", "Third", "Deal." }, s.Select(x => x.En));
        Assert.Null(s[0].Speaker);
        Assert.Null(s[2].Speaker);
        Assert.Equal("Lucia", s[1].Speaker);
    }
}
