using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class FantraxUrlParserTests
{
    [Fact]
    public void Parse_ReadsQueryParameters()
    {
        var parsed = FantraxUrlParser.Parse(
            "https://www.fantrax.com/fantasy/league/abc123/team/roster?leagueId=qry456&teamId=tm789");

        Assert.NotNull(parsed);
        Assert.Equal("qry456", parsed.LeagueId);   // query wins over the path segment
        Assert.Equal("tm789", parsed.TeamId);
    }

    [Fact]
    public void Parse_FallsBackToPathSegmentForLeagueId()
    {
        var parsed = FantraxUrlParser.Parse("https://www.fantrax.com/fantasy/league/abc123/team/roster");

        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed.LeagueId);
        Assert.Null(parsed.TeamId);
    }

    [Fact]
    public void Parse_ReadsMatrixTeamIdParameter()
    {
        var parsed = FantraxUrlParser.Parse(
            "https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=tm789");

        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed.LeagueId);
        Assert.Equal("tm789", parsed.TeamId);
    }

    [Theory]
    [InlineData("https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=tm789/more", "tm789")]
    [InlineData("https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=tm789;view=stats", "tm789")]
    [InlineData("https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=tm789&x=1", "tm789")]
    public void Parse_TerminatesMatrixTeamIdAtDelimiter(string url, string expectedTeamId)
    {
        Assert.Equal(expectedTeamId, FantraxUrlParser.Parse(url)?.TeamId);
    }

    [Fact]
    public void Parse_ReturnsNullTeamIdWhenMatrixValueIsEmpty()
    {
        var parsed = FantraxUrlParser.Parse(
            "https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=");

        Assert.NotNull(parsed);
        Assert.Null(parsed.TeamId);
    }

    [Fact]
    public void Parse_ReturnsNullWhenNoLeagueIdAnywhere()
    {
        Assert.Null(FantraxUrlParser.Parse("https://www.fantrax.com/fantasy/team/roster?teamId=tm789"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("league/abc123")]      // relative, not absolute
    public void Parse_ReturnsNullForUnusableInput(string input)
    {
        Assert.Null(FantraxUrlParser.Parse(input));
    }

    [Fact]
    public void Parse_HandlesTrailingSlashAfterLeagueSegment()
    {
        Assert.Equal("abc123", FantraxUrlParser.Parse("https://www.fantrax.com/fantasy/league/abc123/")?.LeagueId);
    }

    [Fact]
    public void Parse_ReturnsNullWhenLeagueSegmentIsLast()
    {
        Assert.Null(FantraxUrlParser.Parse("https://www.fantrax.com/fantasy/league"));
    }
}
