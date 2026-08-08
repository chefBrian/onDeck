using System.Net;

namespace OnDeck.Core.Tests.Networking;

public class RoutingHttpMessageHandlerTests
{
    [Fact]
    public async Task MapJson_RoutesByUrlSubstring()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapJson("/alpha", """{"which":"alpha"}""");
        handler.MapJson("/beta", """{"which":"beta"}""");
        var client = handler.CreateClient();

        Assert.Equal("""{"which":"beta"}""", await client.GetStringAsync("https://example.com/beta"));
        Assert.Equal("""{"which":"alpha"}""", await client.GetStringAsync("https://example.com/alpha"));
    }

    [Fact]
    public async Task MapJson_RespondersSeeTheRequestAndItsBody()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapJson("/echo", (request, body) => $$"""{"path":"{{request.RequestUri!.AbsolutePath}}","body":{{body}}}""");
        var client = handler.CreateClient();

        var response = await client.PostAsync("https://example.com/echo", new StringContent("""{"n":1}"""));

        Assert.Equal("""{"path":"/echo","body":{"n":1}}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MapJson_ReplacesAnEarlierRouteWithTheSameKey()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapJson("/thing", """{"v":1}""");
        handler.MapJson("/thing", """{"v":2}""");

        Assert.Equal("""{"v":2}""", await handler.CreateClient().GetStringAsync("https://example.com/thing"));
    }

    [Fact]
    public async Task MapStatus_ReturnsTheStatusWithoutABody()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapStatus("/down", HttpStatusCode.ServiceUnavailable);

        var response = await handler.CreateClient().GetAsync("https://example.com/down");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task CountRequests_CountsMatchingUrls()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapJson("/x", "{}");
        var client = handler.CreateClient();

        await client.GetAsync("https://example.com/x?a=1");
        await client.GetAsync("https://example.com/x?a=2");

        Assert.Equal(2, handler.CountRequests("/x"));
        Assert.Equal(1, handler.CountRequests("a=2"));
    }

    [Fact]
    public async Task SendAsync_ThrowsForAnUnroutedUrl()
    {
        var handler = new RoutingHttpMessageHandler();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.CreateClient().GetAsync("https://example.com/missing"));

        Assert.Contains("no route", thrown.Message);
    }
}
