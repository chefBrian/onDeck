namespace OnDeck.Core.Models;

/// <summary>
/// Swift nests these in <c>Player</c>, which C# cannot do: the record already has a
/// <c>RosterStatus</c> property and CS0102 forbids a nested type of the same name
/// (Swift gets away with it because <c>rosterStatus</c> differs from <c>RosterStatus</c>
/// by case). The property names win, since those are what the rest of the port reads.
/// </summary>
public enum PlayerPosition
{
    Hitter,
    Pitcher,
}

/// <summary>Values are Fantrax <c>statusId</c> wire values — do not renumber.</summary>
public enum RosterStatus
{
    Active = 1,
    Reserve = 2,
    InjuredReserve = 3,
    Minors = 9,
}

/// <summary>Port of <c>Models/Player.swift</c>.</summary>
public sealed record Player(
    int Id,                                     // MLB player ID
    string Name,
    string Team,
    IReadOnlySet<PlayerPosition> Positions,
    IReadOnlySet<string> FantraxPositions,      // original Fantrax codes, e.g. "SP", "RP", "C"
    RosterStatus RosterStatus)
{
    public bool IsPitcher => Positions.Contains(PlayerPosition.Pitcher);
    public bool IsHitter => Positions.Contains(PlayerPosition.Hitter);
    public bool IsOnBench => RosterStatus == RosterStatus.Reserve;
    public bool IsUnavailable => RosterStatus is RosterStatus.InjuredReserve or RosterStatus.Minors;
    public bool IsStartingPitcherOnly =>
        FantraxPositions.Contains("SP") && !FantraxPositions.Contains("RP") && !IsHitter;

    // The compiler-generated record equality would compare the two set members by
    // reference. Swift's Set has value semantics, so compare contents instead.
    public bool Equals(Player? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Team == other.Team
        && RosterStatus == other.RosterStatus
        && Positions.SetEquals(other.Positions)
        && FantraxPositions.SetEquals(other.FantraxPositions);

    public override int GetHashCode()
    {
        var positionsHash = 0;
        foreach (var position in Positions) positionsHash ^= position.GetHashCode();

        var fantraxHash = 0;
        foreach (var position in FantraxPositions) fantraxHash ^= position.GetHashCode();

        return HashCode.Combine(Id, Name, Team, RosterStatus, positionsHash, fantraxHash);
    }
}
