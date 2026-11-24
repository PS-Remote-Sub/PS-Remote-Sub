using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Web;

namespace PSRemoteSub.Web.Tests;

public sealed class WebServerTests : IAsyncLifetime
{
    private readonly SubtitleHub _hub = new(new FakeTimeProvider());
    private readonly HttpClient _http = new();
    private SubtitleWebServer _server = null!;

    private string Url(string path) => $"http://127.0.0.1:{_server.Port}{path}";

    public async Task InitializeAsync() => _server = await SubtitleWebServer.StartAsync(_hub, 0, "127.0.0.1");

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Root_ServesHtml()
    {
        using var res = await _http.GetAsync(Url("/"));
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/html", res.Content.Headers.ContentType?.MediaType is { } m ? m : "");
        Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
        Assert.Contains("EventSource", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoSleep_IsServed()
    {
        using var res = await _http.GetAsync(Url("/nosleep.min.js"));
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("NoSleep", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_ReturnsEntriesCamelCase()
    {
        _hub.Publish("Hello", "สวัสดี", "en", null);
        _hub.Publish("World", "โลก", "en", null);
        _hub.SetStatus(PipelineStatus.Initial with { Paused = true });

        var json = await _http.GetStringAsync(Url("/api/history"));
        Assert.Contains("โลก", json); // not escaped to \uXXXX
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("โลก", entries[1].GetProperty("translated").GetString());
        Assert.True(doc.RootElement.GetProperty("status").GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task History_DoesNotSendOverlayBounds()
    {
        _hub.Publish("Hello", "สวัสดี", "en", null, bounds: new PSRemoteSub.Core.Imaging.RegionFraction(0.1, 0.8, 0.5, 0.1));

        var json = await _http.GetStringAsync(Url("/api/history"));
        Assert.DoesNotContain("bounds", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task History_StatusIncludesMinimizedAndOcrUnavailable()
    {
        _hub.SetStatus(PipelineStatus.Initial with { Minimized = true, OcrUnavailable = true });

        using var doc = JsonDocument.Parse(await _http.GetStringAsync(Url("/api/history")));
        var status = doc.RootElement.GetProperty("status");
        Assert.True(status.GetProperty("minimized").GetBoolean());
        Assert.True(status.GetProperty("ocrUnavailable").GetBoolean());
    }

    [Fact]
    public async Task Page_HasCopyForEveryStatusFlag()
    {
        var html = await _http.GetStringAsync(Url("/"));
        Assert.Contains("หน้าต่าง Remote Play ถูกย่ออยู่ — กรุณาเปิดไว้ (บังได้แต่ห้ามย่อ)", html);
        Assert.Contains("ยังไม่ได้ติดตั้งภาษา OCR นี้ — เปิด ตั้งค่า → ภาษา OCR", html);
        Assert.Contains("s.minimized", html);
        Assert.Contains("s.ocrUnavailable", html);
        // The banner shows the translator's error (AI or Google) and drops it once its backoff ends, like the tray.
        Assert.Contains("s.aiError", html);
        Assert.Contains("s.aiRetryAt", html);
        Assert.DoesNotContain("translatorError", html);
        Assert.DoesNotContain("translatorRetryAt", html);
    }

    /// <summary>The AI error keys the app localizes (SettingsWindow.AiCopyKeys); the page must say the same in Thai.</summary>
    [Theory]
    [InlineData("AiDown")]
    [InlineData("AiRateLimited")]
    [InlineData("AiAuth")]
    [InlineData("AiModelNotFound")]
    [InlineData("AiBilling")]
    [InlineData("AiKeyNeeded")]
    [InlineData("AiNoModel")]
    [InlineData("GoogleDown")]
    public async Task Page_HasAppCopyForAiError(string key)
    {
        var html = await _http.GetStringAsync(Url("/"));
        Assert.Contains($"{key}: \"{AppString(key)}\"", html);
    }

    /// <summary>The Thai text of <paramref name="key"/> in the app's <c>Strings.resx</c>.</summary>
    private static string AppString(string key)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PSRemoteSub.sln")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("PSRemoteSub.sln not found above " + AppContext.BaseDirectory);
        var resx = System.Xml.Linq.XDocument.Load(Path.Combine(dir.FullName, "src", "PSRemoteSub.App", "Strings.resx"));
        return resx.Root!.Elements("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
    }

    [Fact]
    public async Task Events_SendsStatusOnConnect_ThenSubtitle()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/events"));
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("status", ev);
        Assert.False(JsonDocument.Parse(data).RootElement.GetProperty("remotePlayFound").GetBoolean());

        _hub.Publish("Hi", "หวัดดี", "en", null);
        (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("subtitle", ev);
        var root = JsonDocument.Parse(data).RootElement;
        Assert.Equal("หวัดดี", root.GetProperty("translated").GetString());
        Assert.Equal("Hi", root.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Events_SendsRefined()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/events"));
        await sse.ReadEventAsync(); // status
        var entry = _hub.Publish("Hi", "หวัดดี", "en", null, awaitingAi: true);
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("subtitle", ev);
        Assert.Contains("\"awaitingAi\":true", data);

        _hub.Refine(entry.Id, "สวัสดี");
        (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("refined", ev);
        Assert.Contains("\"awaitingAi\":false", data);
        var root = JsonDocument.Parse(data).RootElement;
        Assert.Equal(entry.Id, root.GetProperty("id").GetInt64());
        Assert.Equal("สวัสดี", root.GetProperty("translated").GetString());
    }

    [Fact]
    public async Task History_IncludesAwaiting_NoEngine()
    {
        _hub.Publish("Hi", "หวัดดี", "en", null, awaitingAi: true);

        using var doc = JsonDocument.Parse(await _http.GetStringAsync(Url("/api/history")));
        var e = doc.RootElement.GetProperty("entries")[0];
        Assert.False(e.TryGetProperty("engine", out _));
        Assert.True(e.GetProperty("awaitingAi").GetBoolean());
    }

    [Fact]
    public async Task Page_HandlesRefinedAndProvisionalStyling()
    {
        var html = await _http.GetStringAsync(Url("/"));
        Assert.Contains("\"refined\"", html);
        Assert.Contains("awaitingAi", html);
    }

    [Fact]
    public async Task Events_SendsClear()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/events"));
        await sse.ReadEventAsync(); // status
        _hub.Clear();
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("clear", ev);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(data).RootElement.GetProperty("zoneId").ValueKind);
    }

    [Fact]
    public async Task Stream_ClearCarriesZone()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/events"));
        await sse.ReadEventAsync(); // status
        _hub.Clear("phone");
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("clear", ev);
        Assert.Equal("phone", JsonDocument.Parse(data).RootElement.GetProperty("zoneId").GetString());
    }

    [Fact]
    public async Task Stream_ZoneHiddenBecomesClear()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/events"));
        await sse.ReadEventAsync(); // status
        _hub.HideZone("phone");
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("clear", ev);
        Assert.Equal("phone", JsonDocument.Parse(data).RootElement.GetProperty("zoneId").GetString());
    }

    [Fact]
    public async Task Stream_SkipsEntriesNotForWeb()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/events"));
        await sse.ReadEventAsync(); // status
        var hidden = _hub.Publish("Secret", "ลับ", "en", null, awaitingAi: true, zoneId: "phone", zoneName: "มือถือ", sendToWeb: false);
        _hub.Refine(hidden.Id, "ลับมาก");
        _hub.SetTrigger("phone", true);
        _hub.Publish("Shown", "แสดง", "en", null);
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("subtitle", ev);
        Assert.Contains("แสดง", data);
    }

    [Fact]
    public async Task History_SkipsEntriesNotForWeb()
    {
        _hub.Publish("Secret", "ลับ", "en", null, zoneId: "phone", zoneName: "มือถือ", sendToWeb: false);
        _hub.Publish("Shown", "แสดง", "en", null, zoneId: "dialogue", zoneName: "บทสนทนา");

        using var doc = JsonDocument.Parse(await _http.GetStringAsync(Url("/api/history")));
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("Shown", entries[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task History_EntryHasZoneAndNoSendToWeb()
    {
        _hub.Publish("Shown", "แสดง", "en", null, zoneId: "dialogue", zoneName: "บทสนทนา");

        using var doc = JsonDocument.Parse(await _http.GetStringAsync(Url("/api/history")));
        var e = doc.RootElement.GetProperty("entries")[0];
        Assert.Equal("dialogue", e.GetProperty("zoneId").GetString());
        Assert.Equal("บทสนทนา", e.GetProperty("zoneName").GetString());
        Assert.False(e.TryGetProperty("sendToWeb", out _));
    }

    [Fact]
    public async Task Events_SendsStatusChange()
    {
        await using var sse = await SseClient.ConnectAsync(Url("/events"));
        await sse.ReadEventAsync();
        _hub.SetStatus(PipelineStatus.Initial with { AiError = "AiDown" });
        var (ev, data) = await sse.ReadEventAsync();
        Assert.Equal("status", ev);
        Assert.Equal("AiDown", JsonDocument.Parse(data).RootElement.GetProperty("aiError").GetString());
    }

    [Fact]
    public async Task Reconnect_ReceivesNewEvents_AndClientCountDrops()
    {
        var a = await SseClient.ConnectAsync(Url("/events"));
        await using var b = await SseClient.ConnectAsync(Url("/events"));
        await a.ReadEventAsync();
        await b.ReadEventAsync();
        await WaitForAsync(() => _hub.WebClients == 2);

        await a.DisposeAsync();
        await WaitForAsync(() => _hub.WebClients == 1);

        _hub.Publish("Again", "อีกครั้ง", "en", null);
        var (ev, data) = await b.ReadEventAsync();
        Assert.Equal("subtitle", ev);
        Assert.Contains("อีกครั้ง", data);
    }

    [Fact]
    public async Task StateChangingVerbs_AreNotExposed()
    {
        using var res = await _http.PostAsync(Url("/api/history"), new StringContent(""));
        Assert.NotEqual(System.Net.HttpStatusCode.OK, res.StatusCode);
        using var res2 = await _http.GetAsync(Url("/api/pause"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res2.StatusCode);
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
