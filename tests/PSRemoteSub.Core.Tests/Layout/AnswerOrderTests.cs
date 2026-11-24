using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Tests.Layout;

public class AnswerOrderTests
{
    private static SubtitleEntry Answer(long id, string? text = "th") =>
        new(id, DateTimeOffset.UnixEpoch, "en" + id, text is null ? null : text + id, "en", null);

    private static AnswerOrder Expecting(params long[] ids)
    {
        var order = new AnswerOrder();
        foreach (var id in ids)
            order.Expect(id);
        return order;
    }

    private static long[] Ids(IReadOnlyList<SubtitleEntry> due) => [.. due.Select(e => e.Id)];

    [Fact]
    public void InOrder_EachAnswerIsDueAtOnce()
    {
        var order = Expecting(1, 2);
        Assert.Equal([1L], Ids(order.Answer(Answer(1))));
        Assert.Equal([2L], Ids(order.Answer(Answer(2))));
    }

    [Fact]
    public void NewerFirst_WaitsForTheOlder_ThenBothInOrder()
    {
        var order = Expecting(1, 2, 3);
        Assert.Empty(order.Answer(Answer(3)));
        Assert.Empty(order.Answer(Answer(2)));
        Assert.True(order.Holds(2));
        Assert.Equal([1L, 2, 3], Ids(order.Answer(Answer(1))));
        Assert.False(order.Holds(3));
    }

    [Fact]
    public void OlderStillPending_OnlyTheAnswersBeforeItAreDue()
    {
        var order = Expecting(1, 2, 3);
        Assert.Empty(order.Answer(Answer(3)));
        Assert.Equal([1L], Ids(order.Answer(Answer(1)))); // 2 still pending: 3 waits for it
        Assert.Equal([2L, 3], Ids(order.Answer(Answer(2))));
    }

    [Fact]
    public void Untranslated_WithANewerLine_IsSkipped()
    {
        var order = Expecting(1, 2);
        Assert.Empty(order.Answer(Answer(1, text: null))); // 2 still pending
        Assert.Equal([2L], Ids(order.Answer(Answer(2))));
    }

    [Fact]
    public void Untranslated_DueWithANewerAnswer_IsSkipped()
    {
        var order = Expecting(1, 2);
        Assert.Empty(order.Answer(Answer(2)));
        Assert.Equal([2L], Ids(order.Answer(Answer(1, text: null))));
    }

    [Fact]
    public void Untranslated_Newest_IsShown()
    {
        var order = Expecting(1);
        Assert.Equal([1L], Ids(order.Answer(Answer(1, text: null))));
    }

    [Fact]
    public void WaitingAnswer_RefinedAgain_TakesTheNewText()
    {
        var order = Expecting(1, 2);
        order.Answer(Answer(2, "first"));
        order.Answer(Answer(2, "second"));
        Assert.Equal(["th1", "second2"], order.Answer(Answer(1)).Select(e => e.Translated));
    }

    [Fact]
    public void NotHeld_Nothing()
    {
        var order = Expecting(1);
        Assert.Empty(order.Answer(Answer(5)));
        Assert.True(order.Holds(1));
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var order = Expecting(1, 2);
        order.Answer(Answer(2));
        order.Reset();
        Assert.False(order.Holds(1));
        Assert.False(order.Holds(2));
    }
}
