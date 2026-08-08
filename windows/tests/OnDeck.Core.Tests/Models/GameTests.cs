using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class GameTests
{
    private static Game Make(string homeTeam = "Los Angeles Dodgers", string awayTeam = "San Francisco Giants") =>
        new(776543, homeTeam, awayTeam, 119, 137,
            new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero),
            HomeProbablePitcherId: 605483, AwayProbablePitcherId: null,
            Broadcasts: [new Game.Broadcast("Peacock", IsExclusive: true)],
            HomeLineup: [660271, 605141],
            AwayLineup: []);

    private static Player PlayerOn(string team) =>
        new(660271, "Shohei Ohtani", team,
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "DH" },
            RosterStatus.Active);

    [Fact]
    public void SideFor_MatchesHomeWhenFullNameContainsAbbreviation()
    {
        Assert.Equal(Game.Side.Home, Make().SideFor(PlayerOn("Dodgers")));
    }

    [Fact]
    public void SideFor_MatchesAwayWhenFullNameContainsAbbreviation()
    {
        Assert.Equal(Game.Side.Away, Make().SideFor(PlayerOn("Giants")));
    }

    [Fact]
    public void SideFor_MatchesWhenPlayerTeamContainsGameTeam()
    {
        // "Athletics" (MLB short name) is contained in the player's longer team string.
        var game = Make(homeTeam: "Athletics", awayTeam: "Seattle Mariners");
        Assert.Equal(Game.Side.Home, game.SideFor(PlayerOn("Sacramento Athletics")));
    }

    [Fact]
    public void SideFor_ReturnsNullWhenNeitherSideMatches()
    {
        Assert.Null(Make().SideFor(PlayerOn("Red Sox")));
    }

    [Fact]
    public void SideFor_PrefersHomeWhenBothWouldMatch()
    {
        var game = Make(homeTeam: "New York Yankees", awayTeam: "New York Mets");
        Assert.Equal(Game.Side.Home, game.SideFor(PlayerOn("New York")));
    }

    [Fact]
    public void Equality_IsStructuralOverCollectionMembers()
    {
        Assert.Equal(Make(), Make());
        Assert.Equal(Make().GetHashCode(), Make().GetHashCode());
    }

    [Fact]
    public void Equality_DistinguishesDifferentLineups()
    {
        var a = Make();
        var b = a with { HomeLineup = [660271] };
        Assert.NotEqual(a, b);
    }
}
