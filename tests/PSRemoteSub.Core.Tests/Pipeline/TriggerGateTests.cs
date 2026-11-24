using PSRemoteSub.Core.Pipeline;

namespace PSRemoteSub.Core.Tests.Pipeline;

public class TriggerGateTests
{
    [Fact] public void FirstMatch_TurnsOn()
    {
        var g = new TriggerGate();
        Assert.Equal(TriggerChange.On, g.Feed(true));
        Assert.True(g.Active);
    }

    [Fact] public void TurnsOffAfterTwoMissesInARow()
    {
        var g = new TriggerGate();
        g.Feed(true);
        Assert.Equal(TriggerChange.None, g.Feed(false));
        Assert.True(g.Active);
        Assert.Equal(TriggerChange.Off, g.Feed(false));
        Assert.False(g.Active);
    }

    [Fact] public void MatchBetweenMisses_ResetsTheCount()
    {
        var g = new TriggerGate();
        g.Feed(true);
        g.Feed(false);
        Assert.Equal(TriggerChange.None, g.Feed(true));
        Assert.Equal(TriggerChange.None, g.Feed(false));
        Assert.True(g.Active);
    }

    [Fact] public void MissesWhileInactive_DoNothing()
    {
        var g = new TriggerGate();
        Assert.Equal(TriggerChange.None, g.Feed(false));
        Assert.False(g.Active);
    }

    [Fact] public void Reset_GoesInactive()
    {
        var g = new TriggerGate();
        g.Feed(true);
        g.Reset();
        Assert.False(g.Active);
        Assert.Equal(TriggerChange.On, g.Feed(true));
    }
}
