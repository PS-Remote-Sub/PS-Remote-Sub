using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Web.Tests;

public sealed class TvEventsTests : IAsyncLifetime
{
    private readonly SubtitleHub _hub = new(new FakeTimeProvider());
    private readonly TvFeed _feed = new();
    private SubtitleWebServer _server = null!;

    private string Url(string path) => $"http://127.0.0.1:{_server.Port}{path}";

    public async Task InitializeAsync() => _server = await SubtitleWebServer.StartAsync(_hub, 0, "127.0.0.1", _feed);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static TvZoneView View(string text) => new(TvPlacement.Fit, new RegionFraction(0.1, 0.7, 0.8, 0.2),
        new RegionFraction(0.1, 0.72, 0.8, 0.1), null, 30, false,
        [new TvRow(new SpeakerTag("Lucia", "#FFD54F"), text, new RegionFraction(0.1, 0.72, 0.8, 0.1))]);

    private static TvConfig Config(TvMode mode = TvMode.Cover, string pc = "PC") =>
        new(true, pc, mode, 100, 0.75, 8, new TvCalibration(1.0, 0.01, -0.02), false);

    [Fact]
    public async Task TvEvents_SendsConfigThenCurrentZones()
    {
        _feed.SetConfig(Config());
        _feed.SetZone("dialogue", View("สวัสดี"));

        await using var sse = await SseClient.ConnectAsync(Url("/tv/events"));
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("config", ev);
        var cfg = JsonDocument.Parse(data).RootElement;
        Assert.Equal(1, cfg.GetProperty("v").GetInt32());
        Assert.True(cfg.GetProperty("enabled").GetBoolean());
        Assert.Equal("cover", cfg.GetProperty("mode").GetString());
        Assert.Equal("PC", cfg.GetProperty("pcName").GetString());
        Assert.Equal(100, cfg.GetProperty("textScale").GetInt32());
        Assert.Equal(8, cfg.GetProperty("stripBottom").GetInt32());
        Assert.False(cfg.GetProperty("calibrating").GetBoolean());
        var cal = cfg.GetProperty("calibration");
        Assert.Equal(1.0, cal.GetProperty("scale").GetDouble());
        Assert.Equal(0.01, cal.GetProperty("offsetX").GetDouble());
        Assert.Equal(-0.02, cal.GetProperty("offsetY").GetDouble());

        (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("zone", ev);
        Assert.Contains("สวัสดี", data); // not escaped
        var zone = JsonDocument.Parse(data).RootElement;
        Assert.Equal("dialogue", zone.GetProperty("zoneId").GetString());
        var view = zone.GetProperty("view");
        Assert.Equal("fit", view.GetProperty("placement").GetString());
        Assert.Equal(0.7, view.GetProperty("captionBox").GetProperty("y").GetDouble());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("bounds").ValueKind);
        var row = view.GetProperty("rows")[0];
        Assert.Equal("สวัสดี", row.GetProperty("text").GetString());
        Assert.Equal("Lucia", row.GetProperty("speaker").GetProperty("name").GetString());
        Assert.Equal("#FFD54F", row.GetProperty("speaker").GetProperty("color").GetString());
        Assert.Equal(0.8, row.GetProperty("cover").GetProperty("width").GetDouble());
    }

    [Fact]
    public async Task TvEvents_StreamsZoneChangeAndHide()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/tv/events"));
        Assert.Equal("config", (await sse.ReadEventAsync()).Event);

        _feed.SetZone("z", View("one"));
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("zone", ev);
        Assert.Equal("one", JsonDocument.Parse(data).RootElement.GetProperty("view").GetProperty("rows")[0].GetProperty("text").GetString());

        _feed.SetZone("z", null);
        (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("zone", ev);
        Assert.Contains("\"view\":null", data);
        Assert.Equal("z", JsonDocument.Parse(data).RootElement.GetProperty("zoneId").GetString());
    }

    [Fact]
    public async Task TvEvents_ConfigChange_IsStreamed()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/tv/events"));
        await sse.ReadEventAsync();

        _feed.SetConfig(Config(TvMode.Strip, "Other"));
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("config", ev);
        var cfg = JsonDocument.Parse(data).RootElement;
        Assert.Equal("strip", cfg.GetProperty("mode").GetString());
        Assert.Equal("Other", cfg.GetProperty("pcName").GetString());
    }

    [Fact]
    public async Task TvEvents_TracksClientAddress()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/tv/events"));
        await sse.ReadEventAsync();
        await WaitForAsync(() => _feed.Clients().Contains("127.0.0.1"));

        await sse.DisposeAsync(); // idempotent: the using disposes again
        await WaitForAsync(() => _feed.Clients().Count == 0);
    }

    [Fact]
    public async Task TvEvents_WithoutFeed_Returns404()
    {
        await using var server = await SubtitleWebServer.StartAsync(_hub, 0, "127.0.0.1");
        using var http = new HttpClient();
        using var res = await http.GetAsync($"http://127.0.0.1:{server.Port}/tv/events");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ServePhoneFalse_PhoneRoutes404_TvEventsStillWorks()
    {
        await using var server = await SubtitleWebServer.StartAsync(_hub, 0, "127.0.0.1", _feed, servePhone: false);
        using var http = new HttpClient();
        var root = $"http://127.0.0.1:{server.Port}";
        foreach (var path in new[] { "/", "/nosleep.min.js", "/api/history", "/events" })
        {
            using var res = await http.GetAsync(root + path, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
        }

        await using var sse = await SseClient.ConnectAsync(root + "/tv/events");
        Assert.Equal("config", (await sse.ReadEventAsync()).Event);
    }

    [Fact]
    public async Task Events_PhonePayloadUnchanged()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/events"));
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("status", ev);
        Assert.DoesNotContain("\"v\":", data);

        _hub.Publish("Hi", "หวัดดี", "en", null);
        (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("subtitle", ev);
        Assert.Equal("หวัดดี", JsonDocument.Parse(data).RootElement.GetProperty("translated").GetString());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 2 s");
            await Task.Delay(10);
        }
    }
}
