using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace OnDeck.Core.Tests.Networking;

/// <summary>
/// Records every outgoing request and replays queued responses in order. Once the queue
/// drains, the last response repeats — convenient for the two-call Fantrax roster flow.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, byte[] Body)> _responses = new();
    private (HttpStatusCode Status, byte[] Body)? _last;

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public Uri? LastUri => Requests.LastOrDefault()?.RequestUri;

    public void EnqueueJson(string json) => _responses.Enqueue((HttpStatusCode.OK, Encoding.UTF8.GetBytes(json)));

    public void EnqueueBytes(byte[] body) => _responses.Enqueue((HttpStatusCode.OK, body));

    public void EnqueueStatus(HttpStatusCode status) => _responses.Enqueue((status, []));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count > 0) _last = _responses.Dequeue();

        var (status, body) = _last ?? throw new InvalidOperationException("no response queued");

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return new HttpResponseMessage(status) { Content = content };
    }

    public HttpClient CreateClient() => new(this);
}
