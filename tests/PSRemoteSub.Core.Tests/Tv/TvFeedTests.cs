using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class TvFeedTests
{
    private static TvZoneView View(string text) => new(TvPlacement.Custom, new RegionFraction(0.1, 0.8, 0.8, 0.1), null, null, 0.04,
        false, [new TvRow(null, text, null)]);

    private static (TvFeed Feed, List<TvFeedEvent> Events) Create()
    {
        var feed = new TvFeed();
        var events = new List<TvFeedEvent>();
        feed.Changed += (_, e) => events.Add(e);
        return (feed, events);
    }

    [Fact]
    public void SetZone_RaisesOnceForEqualView_AndNullRemoves()
    {
        var (feed, events) = Create();
        feed.SetZone("z", View("a"));
        feed.SetZone("z", View("a"));
        Assert.Single(events);
        Assert.Single(feed.Zones());

        feed.SetZone("z", null);
        Assert.Equal(2, events.Count);
        Assert.Equal(new TvZoneChanged("z", null), events[1]);
        Assert.Empty(feed.Zones());
    }

    [Fact]
    public void SetZone_DifferentView_Raises()
    {
        var (feed, events) = Create();
        feed.SetZone("z", View("a"));
        feed.SetZone("z", View("b"));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void SetZoneNullForUnknown_RaisesNothing()
    {
        var (feed, events) = Create();
        feed.SetZone("nope", null);
        Assert.Empty(events);
    }

    [Fact]
    public void Clients_TrackConnectAndDisconnect()
    {
        var (feed, events) = Create();
        feed.ClientConnected("192.168.0.33");
        feed.ClientDisconnected("192.168.0.33");
        Assert.Equal(2, events.Count);
        Assert.Equal(["192.168.0.33"], ((TvClientsChanged)events[0]).Clients);
        Assert.Empty(((TvClientsChanged)events[1]).Clients);
        Assert.Empty(feed.Clients());
    }

    [Fact]
    public void SetConfig_RaisesOnlyWhenChanged()
    {
        var (feed, events) = Create();
        var config = feed.Config with { Enabled = true };
        feed.SetConfig(config);
        feed.SetConfig(config with { });
        Assert.Single(events);
        Assert.Equal(new TvConfigChanged(config), events[0]);
        Assert.Equal(config, feed.Config);
    }
}
