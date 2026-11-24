using Microsoft.Extensions.Time.Testing;
using PSRemoteSub.Core.Hub;

namespace PSRemoteSub.Core.Tests.Hub;

public class SubtitleHubTests
{
    private static (SubtitleHub Hub, List<HubEvent> Events) NewHub(FakeTimeProvider? time = null)
    {
        var hub = new SubtitleHub(time ?? new FakeTimeProvider());
        var events = new List<HubEvent>();
        hub.Changed += (_, e) => events.Add(e);
        return (hub, events);
    }

    [Fact]
    public void Publish_AssignsIncreasingIds_AndCapsHistoryAt100()
    {
        var (hub, _) = NewHub();
        for (var i = 1; i <= 105; i++)
            Assert.Equal(i, hub.Publish($"line {i}", null, "en", null).Id);

        var history = hub.GetHistory();
        Assert.Equal(100, history.Count);
        Assert.Equal(6, history[0].Id);
        Assert.Equal(105, history[^1].Id);
        Assert.Equal("line 105", history[^1].Source);
    }

    [Fact]
    public void Publish_KeepsBounds_RefineKeepsThem()
    {
        var (hub, _) = NewHub();
        var bounds = new PSRemoteSub.Core.Imaging.RegionFraction(0.1, 0.8, 0.5, 0.1);

        var entry = hub.Publish("Hello", "สวัสดี", "en", null, awaitingAi: true, bounds: bounds);
        var refined = hub.Refine(entry.Id, "สวัสดีครับ");

        Assert.Equal(bounds, entry.Bounds);
        Assert.Equal(bounds, refined!.Bounds);
    }

    [Fact]
    public void Publish_StampsTime_AndRaisesSubtitlePublished()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var (hub, events) = NewHub(time);

        var entry = hub.Publish("Hello", "สวัสดี", "en", null);

        Assert.Equal(new SubtitleEntry(1, time.GetUtcNow(), "Hello", "สวัสดี", "en", null), entry);
        Assert.Equal(entry, Assert.IsType<SubtitlePublished>(Assert.Single(events)).Entry);
    }

    [Fact]
    public void Clear_RaisesSubtitleCleared_AndKeepsHistory()
    {
        var (hub, events) = NewHub();
        hub.Publish("Hello", null, "en", null);
        events.Clear();

        hub.Clear();

        Assert.IsType<SubtitleCleared>(Assert.Single(events));
        Assert.Single(hub.GetHistory());
    }

    [Fact]
    public void SetStatus_SameValue_NoEvent()
    {
        var (hub, events) = NewHub();
        Assert.Equal(PipelineStatus.Initial, hub.Status);

        hub.SetStatus(PipelineStatus.Initial);
        Assert.Empty(events);

        var s = PipelineStatus.Initial with { RemotePlayFound = true };
        hub.SetStatus(s);
        hub.SetStatus(s with { });
        Assert.Equal(s, Assert.IsType<StatusChanged>(Assert.Single(events)).Status);
        Assert.Equal(s, hub.Status);
    }

    [Fact]
    public void ClientCount_RaisesClientsChanged()
    {
        var (hub, events) = NewHub();

        hub.ClientConnected();
        hub.ClientConnected();
        hub.ClientDisconnected();
        hub.ClientDisconnected();
        hub.ClientDisconnected(); // already zero: ignored

        Assert.Equal(0, hub.WebClients);
        Assert.Equal([1, 2, 1, 0], events.Select(e => Assert.IsType<ClientsChanged>(e).Count));
    }

    [Fact]
    public void Events_AreRaisedOutsideTheLock()
    {
        var hub = new SubtitleHub(new FakeTimeProvider());
        var reentered = false;
        hub.Changed += (_, e) =>
        {
            if (e is not SubtitlePublished)
                return;
            // A handler on another thread touching the hub must not deadlock.
            reentered = Task.Run(() => hub.GetHistory().Count).Wait(TimeSpan.FromSeconds(5));
        };

        hub.Publish("Hello", null, "en", null);

        Assert.True(reentered);
    }

    [Fact]
    public async Task ConcurrentPublish_AssignsUniqueIds()
    {
        var hub = new SubtitleHub(new FakeTimeProvider(), capacity: 1000);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                hub.Publish("x", null, "en", null);
        })));

        var ids = hub.GetHistory().Select(e => e.Id).ToList();
        Assert.Equal(800, ids.Distinct().Count());
        Assert.Equal(ids.Order(), ids);
    }

    [Fact]
    public void Refine_ReplacesEntryKeepsTimeAndPosition()
    {
        var time = new FakeTimeProvider();
        var (hub, events) = NewHub(time);
        var a = hub.Publish("A", "a", "en", null);
        time.Advance(TimeSpan.FromSeconds(1));
        var b = hub.Publish("B", "b", "en", "AiDown", awaitingAi: true);
        hub.Publish("C", "c", "en", null);
        events.Clear();

        var refined = hub.Refine(b.Id, "ดี");

        var h = hub.GetHistory();
        Assert.Equal([a.Id, b.Id, 3], h.Select(e => e.Id));
        Assert.Equal("ดี", h[1].Translated);
        Assert.Null(h[1].Error);
        Assert.False(h[1].AwaitingAi);
        Assert.Equal(b.Time, h[1].Time);
        Assert.Equal(h[1], refined);
        var ev = Assert.Single(events);
        Assert.Equal(h[1], Assert.IsType<SubtitleRefined>(ev).Entry);
    }

    [Fact]
    public void Refine_NullOnlyClearsAwaiting()
    {
        var (hub, _) = NewHub();
        var e = hub.Publish("A", "a", "en", null, awaitingAi: true);
        var r = hub.Refine(e.Id, null);
        Assert.NotNull(r);
        Assert.False(r.AwaitingAi);
        Assert.Equal("a", r.Translated);
    }

    [Fact]
    public void Refine_UnknownId_ReturnsNullNoEvent()
    {
        var hub = new SubtitleHub(new FakeTimeProvider(), capacity: 2);
        var events = new List<HubEvent>();
        var first = hub.Publish("1", null, "en", null);
        hub.Publish("2", null, "en", null);
        hub.Publish("3", null, "en", null);
        hub.Changed += (_, e) => events.Add(e);

        Assert.Null(hub.Refine(first.Id, "x"));
        Assert.Empty(events);
    }

    [Fact]
    public void Refine_ClearsError()
    {
        var (hub, _) = NewHub();
        var e = hub.Publish("A", null, "en", "AiDown");
        Assert.Null(hub.Refine(e.Id, "ข้อความ")!.Error);
    }

    [Fact]
    public void Entry_Json_HasNoEngine_AndAwaitingIsCamelCase()
    {
        var entry = new SubtitleEntry(1, DateTimeOffset.UnixEpoch, "a", "b", "en", null, AwaitingAi: true);
        var json = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("engine", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"awaitingAi\":true", json);
    }

    [Fact]
    public void Publish_KeepsZoneFields()
    {
        var (hub, events) = NewHub();

        var entry = hub.Publish("Hello", null, "en", null, zoneId: "z1", zoneName: "Caption", sendToWeb: false);

        Assert.Equal("z1", entry.ZoneId);
        Assert.Equal("Caption", entry.ZoneName);
        Assert.False(entry.SendToWeb);
        Assert.False(hub.GetHistory()[0].SendToWeb);
        Assert.Equal("z1", hub.GetHistory()[0].ZoneId);
        Assert.Equal(entry, Assert.IsType<SubtitlePublished>(Assert.Single(events)).Entry);
    }

    [Fact]
    public void Refine_KeepsZoneFields()
    {
        var (hub, _) = NewHub();
        var entry = hub.Publish("Hello", null, "en", null, awaitingAi: true, zoneId: "z1", zoneName: "Caption", sendToWeb: false);

        var refined = hub.Refine(entry.Id, "สวัสดี");

        Assert.Equal("z1", refined!.ZoneId);
        Assert.Equal("Caption", refined.ZoneName);
        Assert.False(refined.SendToWeb);
    }

    [Fact]
    public void Clear_WithZone_RaisesClearedWithThatZone()
    {
        var (hub, events) = NewHub();

        hub.Clear("z1");

        Assert.Equal("z1", Assert.IsType<SubtitleCleared>(Assert.Single(events)).ZoneId);
    }

    [Fact]
    public void Clear_WithoutZone_HasNullZone()
    {
        var (hub, events) = NewHub();

        hub.Clear();

        Assert.Null(Assert.IsType<SubtitleCleared>(Assert.Single(events)).ZoneId);
    }

    [Fact]
    public void HideZone_RaisesZoneHidden()
    {
        var (hub, events) = NewHub();

        hub.HideZone("z1");

        Assert.Equal("z1", Assert.IsType<ZoneHidden>(Assert.Single(events)).ZoneId);
    }

    [Fact]
    public void SetTrigger_RaisesTriggerChanged()
    {
        var (hub, events) = NewHub();

        hub.SetTrigger("z1", true);

        var e = Assert.IsType<TriggerChanged>(Assert.Single(events));
        Assert.Equal("z1", e.ZoneId);
        Assert.True(e.Active);
    }
}
