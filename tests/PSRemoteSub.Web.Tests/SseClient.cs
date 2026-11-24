namespace PSRemoteSub.Web.Tests;

/// <summary>Minimal Server-Sent Events reader for the web server tests.</summary>
internal sealed class SseClient : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private SseClient(HttpClient client, HttpResponseMessage response, StreamReader reader)
    {
        _client = client;
        _response = response;
        _reader = reader;
    }

    public static async Task<SseClient> ConnectAsync(string url)
    {
        // Drain size 0: disposing the response closes the socket at once instead of draining the endless stream for 2 s.
        var client = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(ReadTimeout);
        var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.StartsWith("text/event-stream", res.Content.Headers.ContentType?.MediaType ?? "");
        var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        return new SseClient(client, res, new StreamReader(stream, System.Text.Encoding.UTF8));
    }

    /// <summary>Next event; comment lines (pings) are skipped. Fails on timeout rather than hanging.</summary>
    public async Task<(string Event, string Data)> ReadEventAsync()
    {
        using var cts = new CancellationTokenSource(ReadTimeout);
        string? ev = null, data = null;
        while (true)
        {
            var line = await _reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
            if (line.Length == 0)
            {
                if (ev is not null)
                    return (ev, data ?? "");
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
                ev = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data = line[6..];
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
