namespace OnDeck.Core.Models;

/// <summary>Port of <c>Models/Game.swift</c>.</summary>
public sealed record Game(
    int Id,                                     // gamePk
    string HomeTeam,
    string AwayTeam,
    int HomeTeamId,
    int AwayTeamId,
    DateTimeOffset StartTime,
    int? HomeProbablePitcherId,
    int? AwayProbablePitcherId,
    IReadOnlyList<Game.Broadcast> Broadcasts,
    IReadOnlyList<int> HomeLineup,              // batting order IDs from schedule (empty until filed)
    IReadOnlyList<int> AwayLineup)
{
    public enum Side
    {
        Home,
        Away,
    }

    public sealed record Broadcast(string CallSign, bool IsExclusive);

    /// <summary>
    /// Two-way containment so a Fantrax abbreviation ("Dodgers") matches an MLB full name
    /// ("Los Angeles Dodgers") and a short MLB name ("Athletics") matches a longer player
    /// team string. Home is tested first, mirroring the Swift order.
    /// </summary>
    public Side? SideFor(Player player)
    {
        if (HomeTeam.Contains(player.Team, StringComparison.Ordinal)
            || player.Team.Contains(HomeTeam, StringComparison.Ordinal))
        {
            return Side.Home;
        }

        if (AwayTeam.Contains(player.Team, StringComparison.Ordinal)
            || player.Team.Contains(AwayTeam, StringComparison.Ordinal))
        {
            return Side.Away;
        }

        return null;
    }

    // Record equality would compare the three list members by reference; Swift's arrays
    // have value semantics, so compare element-wise.
    public bool Equals(Game? other) =>
        other is not null
        && Id == other.Id
        && HomeTeam == other.HomeTeam
        && AwayTeam == other.AwayTeam
        && HomeTeamId == other.HomeTeamId
        && AwayTeamId == other.AwayTeamId
        && StartTime == other.StartTime
        && HomeProbablePitcherId == other.HomeProbablePitcherId
        && AwayProbablePitcherId == other.AwayProbablePitcherId
        && Broadcasts.SequenceEqual(other.Broadcasts)
        && HomeLineup.SequenceEqual(other.HomeLineup)
        && AwayLineup.SequenceEqual(other.AwayLineup);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(HomeTeam);
        hash.Add(AwayTeam);
        hash.Add(HomeTeamId);
        hash.Add(AwayTeamId);
        hash.Add(StartTime);
        hash.Add(HomeProbablePitcherId);
        hash.Add(AwayProbablePitcherId);
        foreach (var broadcast in Broadcasts) hash.Add(broadcast);
        foreach (var id in HomeLineup) hash.Add(id);
        foreach (var id in AwayLineup) hash.Add(id);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Lineup IDs tracked per side so consumers can tell whether a player's own team has
/// submitted yet (vs just the opponent). Batters and pitchers are tracked separately:
/// <see cref="IsSubmitted"/> reflects the batting card only (pitchers come from a
/// different source and shouldn't gate hitter checks), while <see cref="Ids"/> returns
/// the union so membership tests cover both roles.
/// </summary>
public sealed class GameLineup : IEquatable<GameLineup>
{
    public HashSet<int> Home { get; set; } = [];
    public HashSet<int> Away { get; set; } = [];
    public HashSet<int> HomePitchers { get; set; } = [];
    public HashSet<int> AwayPitchers { get; set; } = [];

    /// <summary>
    /// True when this side's batting card was filed without the player. Hitters only:
    /// relievers are never on the card so its contents say nothing about them, and
    /// SP-only players are handled separately by the probable-pitcher day-off pass.
    /// </summary>
    public bool Excludes(Player player, Game.Side side) =>
        player.IsHitter && IsSubmitted(side) && !Ids(side).Contains(player.Id);

    public IReadOnlySet<int> Ids(Game.Side side)
    {
        var batters = side == Game.Side.Home ? Home : Away;
        var pitchers = side == Game.Side.Home ? HomePitchers : AwayPitchers;
        var union = new HashSet<int>(batters);
        union.UnionWith(pitchers);
        return union;
    }

    public bool IsSubmitted(Game.Side side) =>
        (side == Game.Side.Home ? Home : Away).Count > 0;

    public bool Equals(GameLineup? other) =>
        other is not null
        && Home.SetEquals(other.Home)
        && Away.SetEquals(other.Away)
        && HomePitchers.SetEquals(other.HomePitchers)
        && AwayPitchers.SetEquals(other.AwayPitchers);

    public override bool Equals(object? obj) => Equals(obj as GameLineup);

    public override int GetHashCode()
    {
        static int SetHash(HashSet<int> set)
        {
            var hash = 0;
            foreach (var value in set) hash ^= value;
            return hash;
        }

        return HashCode.Combine(SetHash(Home), SetHash(Away), SetHash(HomePitchers), SetHash(AwayPitchers));
    }
}
