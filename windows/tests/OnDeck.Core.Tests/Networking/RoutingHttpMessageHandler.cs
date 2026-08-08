using System.Net;
using System.Text;

namespace OnDeck.Core.Tests.Networking;

/// <summary>
/// Routes responses by URL substring instead of a FIFO queue, so concurrent requests (the
/// per-player MLB ID searches <see cref="OnDeck.Core.Managers.RosterManager"/> fans out with
/// <c>Task.WhenAll</c>) stay deterministic and one handler can serve Fantrax, search,
/// schedule and feed in the same test. Routes are matched in registration order; re-mapping
/// a key replaces the earlier route.
/// </summary>
internal sealed class RoutingHttpMessageHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];
    private readonly Lock _gate = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public void MapJson(string urlSubstring, string json) => Map(urlSubstring, (_, _) => (HttpStatusCode.OK, json));

    public void MapJson(string urlSubstring, Func<HttpRequestMessage, string, string> respond) =>
        Map(urlSubstring, (request, body) => (HttpStatusCode.OK, respond(request, body)));

    public void MapStatus(string urlSubstring, HttpStatusCode status) =>
        Map(urlSubstring, (_, _) => (status, ""));

    public int CountRequests(string urlSubstring)
    {
        lock (_gate)
        {
            return Requests.Count(
                request => request.RequestUri!.AbsoluteUri.Contains(urlSubstring, StringComparison.Ordinal));
        }
    }

    public HttpClient CreateClient() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        Route route;
        lock (_gate)
        {
            Requests.Add(request);
            RequestBodies.Add(body);

            var url = request.RequestUri!.AbsoluteUri;
            route = _routes.FirstOrDefault(candidate => url.Contains(candidate.Key, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"no route for {url}");
        }

        var (status, responseBody) = route.Respond(request, body);
        var content = new StringContent(responseBody, Encoding.UTF8, "application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    private void Map(string key, Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond)
    {
        lock (_gate)
        {
            _routes.RemoveAll(route => route.Key == key);
            _routes.Add(new Route(key, respond));
        }
    }

    private sealed record Route(
        string Key, Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> Respond);
}
