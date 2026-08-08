using System.Net;
using System.Text;

namespace OnDeck.Core.Tests.Networking;

/// <summary>
/// Records every outgoing request and replays queued responses in order. Once the queue
/// drains, the last response repeats — convenient for the two-call Fantrax roster flow.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    private (HttpStatusCode Status, string Body)? _last;

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public Uri? LastUri => Requests.LastOrDefault()?.RequestUri;

    public void EnqueueJson(string json) => _responses.Enqueue((HttpStatusCode.OK, json));

    public void EnqueueStatus(HttpStatusCode status) => _responses.Enqueue((status, ""));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count > 0) _last = _responses.Dequeue();

        var (status, body) = _last ?? throw new InvalidOperationException("no response queued");

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    public HttpClient CreateClient() => new(this);
}
