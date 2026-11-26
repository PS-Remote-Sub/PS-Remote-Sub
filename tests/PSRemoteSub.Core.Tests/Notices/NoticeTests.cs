using PSRemoteSub.Core.Notices;

namespace PSRemoteSub.Core.Tests.Notices;

public class NoticeTests
{
    private static Notice Make(NoticeKind kind, string title, string? topic = null) =>
        new(kind, title, null, [], topic);

    [Theory]
    [InlineData(NoticeKind.Done, true)]
    [InlineData(NoticeKind.Info, false)]
    [InlineData(NoticeKind.Warn, false)]
    public void AutoDismiss_OnlyForDone(NoticeKind kind, bool expected) =>
        Assert.Equal(expected, Make(kind, "t").AutoDismiss);

    [Fact]
    public void Add_ToEmpty_ShowsIt()
    {
        var n = Make(NoticeKind.Info, "a");
        var shown = NoticeQueue.Add([], n);
        Assert.Same(n, Assert.Single(shown));
    }

    [Fact]
    public void Add_NewestFirst()
    {
        var a = Make(NoticeKind.Warn, "a");
        var b = Make(NoticeKind.Info, "b");
        var shown = NoticeQueue.Add(NoticeQueue.Add([], a), b);
        Assert.Equal([b, a], shown);
    }

    [Fact]
    public void Add_SameTopic_ReplacesShownOne()
    {
        var minimized = Make(NoticeKind.Warn, "minimized", "source");
        var other = Make(NoticeKind.Info, "update", "update");
        var lost = Make(NoticeKind.Warn, "lost", "source");
        var shown = NoticeQueue.Add(NoticeQueue.Add(NoticeQueue.Add([], minimized), other), lost);
        Assert.Equal([lost, other], shown);
    }

    [Fact]
    public void Add_DoneWithTopic_DismissesStickyOfThatTopic()
    {
        var lost = Make(NoticeKind.Warn, "lost", "source");
        var found = Make(NoticeKind.Done, "found", "source");
        var shown = NoticeQueue.Add(NoticeQueue.Add([], lost), found);
        Assert.DoesNotContain(lost, shown);
        Assert.Same(found, Assert.Single(shown));
    }

    [Fact]
    public void Add_NullTopics_DoNotReplaceEachOther()
    {
        var a = Make(NoticeKind.Warn, "a");
        var b = Make(NoticeKind.Warn, "b");
        var shown = NoticeQueue.Add(NoticeQueue.Add([], a), b);
        Assert.Equal(2, shown.Count);
    }

    [Fact]
    public void Add_SameContentWithoutTopic_ReplacesTheOldCopy()
    {
        var a = Make(NoticeKind.Warn, "same");
        var other = Make(NoticeKind.Warn, "other");
        var b = Make(NoticeKind.Warn, "same");
        var shown = NoticeQueue.Add(NoticeQueue.Add(NoticeQueue.Add([], a), other), b);
        Assert.Equal(2, shown.Count);
        Assert.Same(b, shown[0]);
        Assert.Same(other, shown[1]);
    }

    [Fact]
    public void Add_FourStickies_KeepsThreeNewest()
    {
        IReadOnlyList<Notice> shown = [];
        var notices = Enumerable.Range(0, 4).Select(i => Make(NoticeKind.Warn, $"w{i}")).ToArray();
        foreach (var n in notices)
            shown = NoticeQueue.Add(shown, n);
        Assert.Equal(NoticeQueue.MaxShown, shown.Count);
        Assert.Equal([notices[3], notices[2], notices[1]], shown);
    }

    [Fact]
    public void Add_OverCap_DropsOldestAutoDismissingBeforeSticky()
    {
        var sticky1 = Make(NoticeKind.Warn, "s1");
        var done = Make(NoticeKind.Done, "d");
        var sticky2 = Make(NoticeKind.Info, "s2");
        var newest = Make(NoticeKind.Warn, "s3");
        var shown = NoticeQueue.Add(NoticeQueue.Add(NoticeQueue.Add(NoticeQueue.Add([], sticky1), done), sticky2), newest);
        Assert.Equal([newest, sticky2, sticky1], shown);
    }

    [Fact]
    public void Add_OverCap_NeverDropsTheNewOne()
    {
        var a = Make(NoticeKind.Warn, "a");
        var b = Make(NoticeKind.Warn, "b");
        var c = Make(NoticeKind.Warn, "c");
        var done = Make(NoticeKind.Done, "d");
        var shown = NoticeQueue.Add(NoticeQueue.Add(NoticeQueue.Add(NoticeQueue.Add([], a), b), c), done);
        Assert.Equal([done, c, b], shown);
    }

    [Fact]
    public void Dismiss_RemovesThatInstanceOnly()
    {
        var a = Make(NoticeKind.Warn, "a");
        var b = Make(NoticeKind.Warn, "b");
        var shown = NoticeQueue.Dismiss(NoticeQueue.Add(NoticeQueue.Add([], a), b), a);
        Assert.Same(b, Assert.Single(shown));
    }

    [Fact]
    public void DismissTopic_RemovesMatchingTopic()
    {
        var a = Make(NoticeKind.Warn, "a", "ai");
        var b = Make(NoticeKind.Warn, "b", "web");
        var shown = NoticeQueue.DismissTopic(NoticeQueue.Add(NoticeQueue.Add([], a), b), "ai");
        Assert.Same(b, Assert.Single(shown));
    }

    // ---- SourceWatch: Remote Play minimized / lost / found again ----

    [Fact]
    public void SourceWatch_NotFoundAtStart_SaysNothing()
    {
        var w = new SourceWatch();
        Assert.Equal(SourceChange.None, w.Update(found: false, minimized: false));
    }

    [Fact]
    public void SourceWatch_FoundThenLost_SaysLostOnce()
    {
        var w = new SourceWatch();
        Assert.Equal(SourceChange.None, w.Update(true, false));
        Assert.Equal(SourceChange.Lost, w.Update(false, false));
        Assert.Equal(SourceChange.None, w.Update(false, false));
    }

    [Fact]
    public void SourceWatch_LostThenFound_SaysFound()
    {
        var w = new SourceWatch();
        w.Update(true, false);
        w.Update(false, false);
        Assert.Equal(SourceChange.Found, w.Update(true, false));
        Assert.Equal(SourceChange.None, w.Update(true, false));
    }

    [Fact]
    public void SourceWatch_Minimized_SaysMinimizedEvenAtStart_ThenFoundWhenRestored()
    {
        var w = new SourceWatch();
        Assert.Equal(SourceChange.Minimized, w.Update(true, true));
        Assert.Equal(SourceChange.None, w.Update(true, true));
        Assert.Equal(SourceChange.Found, w.Update(true, false));
    }

    [Fact]
    public void SourceWatch_MinimizedThenClosed_SaysLost()
    {
        var w = new SourceWatch();
        w.Update(true, true);
        Assert.Equal(SourceChange.Lost, w.Update(false, false));
    }

    [Fact]
    public void SourceWatch_Reset_ForgetsTheWindow()
    {
        var w = new SourceWatch();
        w.Update(true, false);
        w.Reset();
        Assert.Equal(SourceChange.None, w.Update(false, false));
    }
}
