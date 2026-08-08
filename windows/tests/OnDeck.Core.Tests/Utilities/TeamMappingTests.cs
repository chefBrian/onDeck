using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class TeamMappingTests
{
    [Theory]
    [InlineData("ATH", "Athletics")]
    [InlineData("OAK", "Athletics")]      // legacy abbreviation, same club
    [InlineData("LAD", "Los Angeles Dodgers")]
    [InlineData("KC", "Kansas City Royals")]
    [InlineData("STL", "St. Louis Cardinals")]
    [InlineData("WAS", "Washington Nationals")]
    public void MlbTeamName_MapsFantraxAbbreviations(string abbreviation, string expected)
    {
        Assert.Equal(expected, TeamMapping.MlbTeamName(abbreviation));
    }

    [Fact]
    public void MlbTeamName_IsCaseInsensitiveOnInput()
    {
        Assert.Equal("Athletics", TeamMapping.MlbTeamName("ath"));
        Assert.Equal("New York Mets", TeamMapping.MlbTeamName("nym"));
    }

    [Fact]
    public void MlbTeamName_ReturnsNullForUnknownAbbreviation()
    {
        Assert.Null(TeamMapping.MlbTeamName("XYZ"));
    }

    [Fact]
    public void FantraxToMlb_CoversAllThirtyClubsPlusLegacyOak()
    {
        Assert.Equal(31, TeamMapping.FantraxToMlb.Count);
        Assert.Equal(30, TeamMapping.FantraxToMlb.Values.Distinct().Count());
    }

    [Theory]
    [InlineData("Los Angeles Dodgers", "LAD")]
    [InlineData("Kansas City Royals", "KC")]
    [InlineData("Athletics", "ATH")]                  // ATH wins over legacy OAK, deterministically
    [InlineData("Sacramento Athletics", "ATH")]       // partial-match fallback
    public void Abbreviation_ReversesToShortCode(string mlbTeamName, string expected)
    {
        Assert.Equal(expected, TeamMapping.Abbreviation(mlbTeamName));
    }

    [Fact]
    public void Abbreviation_FallsBackToLastWordForUnknownTeam()
    {
        Assert.Equal("Bananas", TeamMapping.Abbreviation("Savannah Bananas"));
    }

    [Fact]
    public void Abbreviation_ReturnsInputWhenThereIsNoLastWord()
    {
        Assert.Equal("", TeamMapping.Abbreviation(""));
    }

    [Theory]
    [InlineData("Athletics", "ATH", true)]
    [InlineData("Athletics", "OAK", true)]
    [InlineData("Sacramento Athletics", "ATH", true)]   // partial match on MLB API name
    [InlineData("Los Angeles Dodgers", "LAA", false)]
    [InlineData("Los Angeles Dodgers", "XYZ", false)]   // unknown abbreviation never matches
    public void Matches_ComparesMlbNameAgainstAbbreviation(
        string mlbTeamName, string abbreviation, bool expected)
    {
        Assert.Equal(expected, TeamMapping.Matches(mlbTeamName, abbreviation));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveOnAbbreviation()
    {
        Assert.True(TeamMapping.Matches("Athletics", "ath"));
    }
}
