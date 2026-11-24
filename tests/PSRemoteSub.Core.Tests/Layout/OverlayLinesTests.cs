using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Tests.Layout;

public class OverlayLinesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static OverlayLines New(double minSeconds = 3) => new() { MinDisplay = TimeSpan.FromSeconds(minSeconds) };

    [Fact]
    public void Show_First_ShowsAtOnce()
    {
        var l = New();
        l.Show(1, "หนึ่ง", false, At(0));
        Assert.Equal((1L, "หนึ่ง", At(0)), (l.Current!.Id, l.Current.Text, l.Current.ShownAt));
    }

    [Fact]
    public void Show_NewerBeforeMin_Waits_ThenAdvances()
    {
        var l = New();
        l.Show(1, "หนึ่ง", false, At(0));
        l.Show(2, "สอง", false, At(1));
        Assert.Equal(1L, l.Current!.Id);
        Assert.Equal(TimeSpan.FromSeconds(2), l.NextChangeIn(At(1)));

        Assert.False(l.Advance(At(2.9)));
        Assert.Equal(1L, l.Current!.Id);

        Assert.True(l.Advance(At(3)));
        Assert.Equal((2L, At(3)), (l.Current!.Id, l.Current.ShownAt));
    }

    [Fact]
    public void Show_NewerAfterMin_ReplacesAtOnce()
    {
        var l = New();
        l.Show(1, "หนึ่ง", false, At(0));
        l.Show(2, "สอง", false, At(5));
        Assert.Equal(2L, l.Current!.Id);
    }

    [Fact]
    public void ZeroMin_ReplacesAtOnce_AndHidesOnClear()
    {
        var l = New(0);
        l.SetGameLine(1, At(0));
        l.Show(1, "หนึ่ง", false, At(0));
        l.SetGameLine(2, At(0.1));
        l.Show(2, "สอง", false, At(0.1));
        Assert.Equal(2L, l.Current!.Id);

        l.SetGameLine(null, At(0.2));
        Assert.Null(l.Current);
    }

    [Fact]
    public void GameLine_StaysWhileShown_NoTimer()
    {
        var l = New();
        l.SetGameLine(1, At(0));
        l.Show(1, "หนึ่ง", false, At(0));
        Assert.False(l.Advance(At(10)));
        Assert.Equal(1L, l.Current!.Id);
        Assert.Null(l.NextChangeIn(At(10)));
    }

    [Fact]
    public void Cleared_BeforeMin_StaysUntilMin()
    {
        var l = New();
        l.SetGameLine(1, At(0));
        l.Show(1, "หนึ่ง", false, At(0));
        l.SetGameLine(null, At(1));
        Assert.Equal(1L, l.Current!.Id);
        Assert.Equal(TimeSpan.FromSeconds(2), l.NextChangeIn(At(1)));

        Assert.True(l.Advance(At(3)));
        Assert.Null(l.Current);
        Assert.Null(l.NextChangeIn(At(10)));
    }

    [Fact]
    public void Cleared_ThenNewGameLine_StillHidesAtMin()
    {
        // The game cleared line 1, then showed line 2 (its translation not back yet): line 1 must not stay past its time.
        var l = New();
        l.SetGameLine(1, At(0));
        l.Show(1, "หนึ่ง", false, At(0));
        l.SetGameLine(null, At(1));
        l.SetGameLine(2, At(1.5));
        Assert.Equal(TimeSpan.FromSeconds(1.5), l.NextChangeIn(At(1.5)));

        Assert.True(l.Advance(At(3)));
        Assert.Null(l.Current);
    }

    [Fact]
    public void LateLine_AfterClear_NewerGameLine_HidesAtMin()
    {
        var l = New();
        l.SetGameLine(1, At(0));
        l.SetGameLine(null, At(1));
        l.SetGameLine(2, At(1.5));
        l.Show(1, "หนึ่ง", false, At(2)); // line 1's translation came after its subtitle cleared
        Assert.True(l.Advance(At(5)));
        Assert.Null(l.Current);
    }

    [Fact]
    public void GameMovesOn_WithoutClear_KeepsLineUntilNext()
    {
        // No clear between lines: line 1 stays until line 2's text arrives, as before.
        var l = New();
        l.SetGameLine(1, At(0));
        l.Show(1, "หนึ่ง", false, At(0));
        l.SetGameLine(2, At(1));
        Assert.False(l.Advance(At(10)));
        Assert.Equal(1L, l.Current!.Id);
    }

    [Fact]
    public void Burst_WhileGameLine_DrainsThenHidesAfterClear()
    {
        var l = New();
        l.SetGameLine(1, At(0));
        l.Show(1, "1", false, At(0));
        l.SetGameLine(2, At(0.5));
        l.Show(2, "2", false, At(0.5));
        l.SetGameLine(3, At(1));
        l.Show(3, "3", false, At(1));
        l.SetGameLine(null, At(1.5));

        l.Advance(At(3));
        Assert.Equal(2L, l.Current!.Id);
        l.Advance(At(6));
        Assert.Equal(3L, l.Current!.Id);
        Assert.True(l.Advance(At(9)));
        Assert.Null(l.Current);
    }

    [Fact]
    public void Cleared_AfterMin_HidesAtOnce()
    {
        var l = New();
        l.SetGameLine(1, At(0));
        l.Show(1, "หนึ่ง", false, At(0));
        l.SetGameLine(null, At(5));
        Assert.Null(l.Current);
    }

    [Fact]
    public void LateLine_NotGameLine_ShowsForMinThenHides()
    {
        var l = New();
        l.SetGameLine(null, At(0));
        l.Show(7, "เจ็ด", false, At(0));
        Assert.Equal(7L, l.Current!.Id);
        Assert.Equal(TimeSpan.FromSeconds(2), l.NextChangeIn(At(1)));

        Assert.True(l.Advance(At(3)));
        Assert.Null(l.Current);
    }

    [Fact]
    public void Queue_CappedAtTwo_DropsOldestWaiting()
    {
        var l = New();
        l.Show(1, "1", false, At(0));
        l.Show(2, "2", false, At(0.5));
        l.Show(3, "3", false, At(1));
        l.Show(4, "4", false, At(1.5));

        Assert.True(l.Advance(At(3)));
        Assert.Equal(3L, l.Current!.Id);
        Assert.True(l.Advance(At(6)));
        Assert.Equal(4L, l.Current!.Id);
    }

    [Fact]
    public void Queue_RaisedCap_KeepsEveryLineThatCameAtOnce()
    {
        var l = New();
        l.MaxWaiting = 4;
        foreach (var id in new long[] { 1, 2, 3, 4, 5 })
            l.Show(id, id.ToString(), false, At(0));

        var shown = new List<long> { l.Current!.Id };
        for (int i = 1; l.Advance(At(3 * i)) && l.Current is { } c; i++)
            shown.Add(c.Id);
        Assert.Equal([1L, 2, 3, 4, 5], shown);
    }

    [Fact]
    public void MaxWaiting_AtLeastOne()
    {
        var l = New();
        l.MaxWaiting = 0;
        Assert.Equal(1, l.MaxWaiting);
    }

    [Fact]
    public void Show_OlderThanShown_Ignored()
    {
        var l = New();
        l.SetGameLine(5, At(0));
        l.Show(5, "ห้า", false, At(0));
        l.Show(4, "สี่", false, At(0.5));
        Assert.Equal(5L, l.Current!.Id);
        Assert.False(l.Advance(At(10)));
        Assert.Equal(5L, l.Current!.Id);
    }

    [Fact]
    public void Show_OlderThanHiddenLine_Ignored()
    {
        var l = New();
        l.Show(5, "ห้า", false, At(0));
        Assert.True(l.Advance(At(3)));
        Assert.Null(l.Current);

        l.Show(4, "สี่", false, At(4));
        Assert.Null(l.Current);
    }

    [Fact]
    public void Show_OutOfOrderWhileWaiting_SortedById()
    {
        var l = New();
        l.Show(1, "1", false, At(0));
        l.Show(3, "3", false, At(0.5));
        l.Show(2, "2", false, At(1));

        l.Advance(At(3));
        Assert.Equal(2L, l.Current!.Id);
        l.Advance(At(6));
        Assert.Equal(3L, l.Current!.Id);
    }

    [Fact]
    public void Show_SameIdAsCurrent_UpdatesText()
    {
        var l = New();
        l.Show(1, "ก", true, At(0));
        l.Show(1, "ข", false, At(1));
        Assert.Equal(("ข", false, At(0)), (l.Current!.Text, l.Current.AwaitingAi, l.Current.ShownAt));
    }

    [Fact]
    public void Refine_UpdatesWaitingLine_BeforeItShows()
    {
        var l = New();
        l.Show(1, "หนึ่ง", false, At(0));
        l.Show(2, "ก", true, At(1));
        Assert.False(l.Refine(2, "ข", false));

        l.Advance(At(3));
        Assert.Equal(("ข", false), (l.Current!.Text, l.Current.AwaitingAi));
    }

    [Fact]
    public void Refine_Current_ReturnsTrue()
    {
        var l = New();
        l.Show(1, "ก", true, At(0));
        Assert.True(l.Refine(1, "ข", false));
        Assert.Equal("ข", l.Current!.Text);
        Assert.False(l.Refine(9, "x", false));
    }

    [Fact]
    public void ClockMovedBack_LineDue()
    {
        // The wall clock was set back: a line "from the future" must not stay up until the clock catches up.
        var l = New();
        l.Show(1, "หนึ่ง", false, At(60));
        Assert.Equal(TimeSpan.Zero, l.NextChangeIn(At(0))); // due now, not in a minute
        l.Show(2, "สอง", false, At(0));
        Assert.Equal(2L, l.Current!.Id);
    }

    [Fact]
    public void MinDisplay_Negative_TreatedAsZero()
    {
        var l = New(-5);
        l.SetGameLine(2, At(0));
        l.Show(1, "1", false, At(0));
        l.Show(2, "2", false, At(0));
        Assert.Equal(2L, l.Current!.Id);
    }

    [Fact]
    public void Reset_ClearsQueue()
    {
        var l = New();
        l.SetGameLine(2, At(1));
        l.Show(1, "1", false, At(0));
        l.Show(2, "2", false, At(1));
        l.Reset();

        Assert.Null(l.Current);
        Assert.Null(l.NextChangeIn(At(10)));
        Assert.False(l.Advance(At(10)));
        Assert.Null(l.Current);

        // The last-shown id is forgotten too: ids from a new reading are shown.
        l.Show(1, "ใหม่", false, At(11));
        Assert.Equal(1L, l.Current!.Id);
    }
}
