using System.Net;

namespace PSRemoteSub.Core.Tests.Translation;

internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync) : HttpMessageHandler
{
    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((req, _) => Task.FromResult(respond(req)))
    {
    }

    public List<Uri> Requests { get; } = [];

    public static StubHttpHandler Ok(string body) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        ct.ThrowIfCancellationRequested();
        return respondAsync(request, ct);
    }
}
