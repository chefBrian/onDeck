namespace OnDeck.Core.Models;

/// <summary>
/// Port of <c>Models/PlayerState.swift</c>. Swift's enum-with-associated-values becomes a
/// closed record hierarchy: the private constructor keeps the case list to the nested types.
/// </summary>
public abstract record PlayerState
{
    private PlayerState() { }

    public sealed record Active(GameContext Context) : PlayerState;

    public sealed record Upcoming(DateTimeOffset StartTime) : PlayerState;

    public sealed record Inactive(InactiveReason Reason) : PlayerState;

    public enum ActiveRole
    {
        Batting,
        Pitching,
    }

    public sealed record GameContext(
        int GamePk,
        ActiveRole Role,
        string Inning,
        string HomeTeam,
        string AwayTeam,
        int HomeTeamId,
        int AwayTeamId,
        int HomeScore,
        int AwayScore,
        int Balls,
        int Strikes,
        int Outs,
        bool RunnerOnFirst,
        bool RunnerOnSecond,
        bool RunnerOnThird);

    public abstract record InactiveReason
    {
        private InactiveReason() { }

        public sealed record GameOver(int GamePk) : InactiveReason;

        public sealed record DayOff : InactiveReason;

        public sealed record Substituted(int GamePk) : InactiveReason;
    }
}
