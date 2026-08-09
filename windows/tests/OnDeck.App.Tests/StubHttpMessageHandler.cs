using System.Net;
using System.Net.Http;

namespace OnDeck.App.Tests;

/// <summary>Replays queued responses and records the requests it saw.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<Uri> Requests { get; } = [];

    public HttpClient CreateClient() => new(this);

    public void EnqueueBytes(byte[] body) =>
        _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        });

    public void EnqueueStatus(HttpStatusCode status) =>
        _responses.Enqueue(new HttpResponseMessage(status));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);

        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        return Task.FromResult(response);
    }
}
