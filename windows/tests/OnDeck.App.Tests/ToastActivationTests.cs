using OnDeck.App.Notifications;

namespace OnDeck.App.Tests;

public class ToastActivationTests
{
    [Fact]
    public void AUrlSurvivesTheRoundTrip()
    {
        var url = new Uri("https://www.mlb.com/tv/g776543");

        Assert.Equal(url, ToastActivation.UrlFrom(ToastActivation.Argument(url)));
    }

    [Fact]
    public void AUrlWithAQueryStringSurvivesTheRoundTrip()
    {
        // The argument format is itself key=value pairs joined by ';'. A stream link carrying
        // its own '=' and '&' is exactly the case that breaks a naive encoding, and the toast
        // is delivered hours before anyone clicks it.
        var url = new Uri("https://www.espn.com/watch?id=abc123&lang=en;x=1");

        Assert.Equal(url, ToastActivation.UrlFrom(ToastActivation.Argument(url)));
    }

    [Fact]
    public void NoUrlMeansNoArgument()
    {
        Assert.Null(ToastActivation.Argument(null));
    }

    [Fact]
    public void AnArgumentWithoutAUrlYieldsNothingToOpen()
    {
        Assert.Null(ToastActivation.UrlFrom("action=viewStream"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("=")]
    [InlineData("url=")]
    [InlineData("url=not a url")]
    [InlineData("url=/relative/path")]
    public void AnUnusableArgumentYieldsNothingToOpen(string? argument)
    {
        Assert.Null(ToastActivation.UrlFrom(argument));
    }

    [Theory]
    [InlineData("url=file:///C:/Windows/System32/calc.exe")]
    [InlineData("url=ms-settings:windowsupdate")]
    [InlineData("url=javascript:alert(1)")]
    public void OnlyWebSchemesAreFollowed(string argument)
    {
        // The argument comes back from outside the process and ends up at ShellExecute, which
        // launches any registered protocol handler. We only ever write http(s).
        Assert.Null(ToastActivation.UrlFrom(argument));
    }

    [Fact]
    public void PlainHttpIsFollowed()
    {
        var url = new Uri("http://example.com/game");

        Assert.Equal(url, ToastActivation.UrlFrom(ToastActivation.Argument(url)));
    }
}
