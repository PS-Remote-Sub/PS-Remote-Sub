using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Web;

/// <summary>
/// Read-only LAN web server: the phone page, a history snapshot and a Server-Sent Events stream of hub events.
/// No endpoint changes application state.
/// </summary>
public sealed class SubtitleWebServer : IAsyncDisposable
{
    private const int ChannelCapacity = 50;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Thai goes out as UTF-8, not \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Wire format of <c>/tv/events</c>: camelCase, enums as camelCase strings. Separate from <see cref="Json"/> so the phone payloads stay as they are.</summary>
    private static readonly JsonSerializerOptions TvJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WebApplication _app;

    private SubtitleWebServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>The port actually bound (differs from the requested one when 0 was asked for).</summary>
    public int Port { get; }

    public static async Task<SubtitleWebServer> StartAsync(
        SubtitleHub hub, int port, string bindAddress = "0.0.0.0",
        TvFeed? tv = null, bool servePhone = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(bindAddress);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://{bindAddress}:{port}");
        builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
        var app = builder.Build();

        if (servePhone)
        {
            app.MapGet("/", (HttpContext ctx) => Embedded(ctx, "index.html", "text/html; charset=utf-8"));
            app.MapGet("/nosleep.min.js", (HttpContext ctx) => Embedded(ctx, "nosleep.min.js", "text/javascript; charset=utf-8"));
            app.MapGet("/icon.svg", (HttpContext ctx) => Embedded(ctx, "icon.svg", "image/svg+xml"));
            app.MapGet("/icon-192.png", (HttpContext ctx) => Embedded(ctx, "icon-192.png", "image/png"));
            app.MapGet("/apple-touch-icon.png", (HttpContext ctx) => Embedded(ctx, "apple-touch-icon.png", "image/png"));
            app.MapGet("/api/history", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { entries = hub.GetHistory().Where(e => e.SendToWeb), status = hub.Status }, Json);
            });
            app.MapGet("/events", (HttpContext ctx) => StreamEventsAsync(ctx, hub, app.Lifetime.ApplicationStopping));
        }
        if (tv is not null)
            app.MapGet("/tv/events", (HttpContext ctx) => StreamTvEventsAsync(ctx, tv, app.Lifetime.ApplicationStopping));

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            return new SubtitleWebServer(app, new Uri(address).Port);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static IResult Embedded(HttpContext ctx, string name, string contentType)
    {
        var stream = typeof(SubtitleWebServer).Assembly.GetManifestResourceStream($"PSRemoteSub.Web.wwwroot.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        ctx.Response.Headers.CacheControl = "no-cache";
        return Results.Stream(stream, contentType);
    }

    private static async Task StreamEventsAsync(HttpContext ctx, SubtitleHub hub, CancellationToken stopping)
    {
        var res = ctx.Response;
        res.ContentType = "text/event-stream";
        res.Headers.CacheControl = "no-cache";
        res.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        var ct = linked.Token;

        // The hub raises Changed synchronously on the publisher's thread: never throw, never block here.
        var channel = Channel.CreateBounded<HubEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        void OnChanged(object? sender, HubEvent e)
        {
            try
            {
                if (e is not ClientsChanged)
                    channel.Writer.TryWrite(e);
            }
            catch
            {
                // Subscriber isolation: a dying connection must not break the publisher.
            }
        }

        hub.Changed += OnChanged;
        var connected = false;
        try
        {
            hub.ClientConnected();
            connected = true;

            await WriteEventAsync(res, "status", hub.Status, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(PingInterval);
                HubEvent e;
                try
                {
                    e = await channel.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await res.WriteAsync(": ping\n\n", ct).ConfigureAwait(false);
                    await res.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                switch (e)
                {
                    case SubtitlePublished p when p.Entry.SendToWeb:
                        await WriteEventAsync(res, "subtitle", p.Entry, ct).ConfigureAwait(false);
                        break;
                    case SubtitleRefined r when r.Entry.SendToWeb:
                        await WriteEventAsync(res, "refined", r.Entry, ct).ConfigureAwait(false);
                        break;
                    case SubtitleCleared c:
                        await WriteEventAsync(res, "clear", new { zoneId = c.ZoneId }, ct).ConfigureAwait(false);
                        break;
                    case ZoneHidden h:
                        await WriteEventAsync(res, "clear", new { zoneId = h.ZoneId }, ct).ConfigureAwait(false);
                        break;
                    case StatusChanged s:
                        await WriteEventAsync(res, "status", s.Status, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away or the server is stopping.
        }
        catch (IOException)
        {
            // Connection reset mid-write.
        }
        finally
        {
            hub.Changed -= OnChanged;
            if (connected)
                hub.ClientDisconnected();
        }
    }

    /// <summary>
    /// The TV overlay stream: <c>config</c>, then one <c>zone</c> per current zone, then every change. Same isolation
    /// rules as <see cref="StreamEventsAsync"/>.
    /// </summary>
    private static async Task StreamTvEventsAsync(HttpContext ctx, TvFeed feed, CancellationToken stopping)
    {
        var res = ctx.Response;
        res.ContentType = "text/event-stream";
        res.Headers.CacheControl = "no-cache";
        res.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        var ct = linked.Token;

        var remote = ctx.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : "unknown";

        // The feed raises Changed synchronously on the overlay's UI thread: never throw, never block here.
        var channel = Channel.CreateBounded<TvFeedEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        void OnChanged(object? sender, TvFeedEvent e)
        {
            try
            {
                if (e is not TvClientsChanged)
                    channel.Writer.TryWrite(e);
            }
            catch
            {
                // Subscriber isolation: a dying connection must not break the overlay.
            }
        }

        // Subscribe before taking the snapshot: a change in between shows up twice (harmless), never not at all.
        feed.Changed += OnChanged;
        var connected = false;
        try
        {
            feed.ClientConnected(remote);
            connected = true;

            await WriteTvEventAsync(res, "config", TvConfigPayload(feed.Config), ct).ConfigureAwait(false);
            foreach (var (zoneId, view) in feed.Zones())
                await WriteTvEventAsync(res, "zone", new { zoneId, view }, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(PingInterval);
                TvFeedEvent e;
                try
                {
                    e = await channel.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await res.WriteAsync(": ping\n\n", ct).ConfigureAwait(false);
                    await res.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                switch (e)
                {
                    case TvConfigChanged c:
                        await WriteTvEventAsync(res, "config", TvConfigPayload(c.Config), ct).ConfigureAwait(false);
                        break;
                    case TvZoneChanged z:
                        await WriteTvEventAsync(res, "zone", new { zoneId = z.ZoneId, view = z.View }, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away or the server is stopping.
        }
        catch (IOException)
        {
            // Connection reset mid-write.
        }
        finally
        {
            feed.Changed -= OnChanged;
            if (connected)
                feed.ClientDisconnected(remote);
        }
    }

    /// <summary>The <c>config</c> event body: the feed's config plus the protocol version.</summary>
    private static object TvConfigPayload(TvConfig c) => new
    {
        v = 1,
        enabled = c.Enabled,
        pcName = c.PcName,
        mode = c.Mode,
        textScale = c.TextScale,
        boxOpacity = c.BoxOpacity,
        stripBottom = c.StripBottom,
        calibration = c.Calibration,
        calibrating = c.Calibrating,
    };

    private static async Task WriteTvEventAsync<T>(HttpResponse res, string name, T payload, CancellationToken ct)
    {
        await res.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, TvJson)}\n\n", ct)
            .ConfigureAwait(false);
        await res.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteEventAsync<T>(HttpResponse res, string name, T payload, CancellationToken ct)
    {
        await res.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n", ct)
            .ConfigureAwait(false);
        await res.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
