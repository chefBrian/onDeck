namespace OnDeck.Core.Models;

/// <summary>
/// Port of <c>LiveFeedData</c> from <c>Networking/MLBStatsAPI.swift</c>. Swift models this
/// as a struct patched through <c>inout</c>; C# uses a mutable class plus an explicit deep
/// <see cref="Clone"/> so <c>LiveFeedPatcher.Apply</c> can keep the same guarantee that
/// partial state never escapes.
/// </summary>
public sealed class LiveFeedData : IEquatable<LiveFeedData>
{
    public string? TimeStamp { get; set; }          // /metaData/timeStamp - diffPatch startTimecode
    public string GameState { get; set; } = "";     // "Preview", "Live", "Final"
    public string? DetailedState { get; set; }      // "Pre-Game", "Warmup", "In Progress", ...
    public int? CurrentBatterId { get; set; }
    public string? CurrentBatterName { get; set; }
    public int? CurrentPitcherId { get; set; }
    public string? CurrentPitcherName { get; set; }
    public int? Inning { get; set; }
    public string? InningHalf { get; set; }
    public string? InningState { get; set; }
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int HomeTeamId { get; set; }
    public int AwayTeamId { get; set; }
    public int Balls { get; set; }
    public int Strikes { get; set; }
    public int Outs { get; set; }
    public int? RunnerOnFirst { get; set; }
    public int? RunnerOnSecond { get; set; }
    public int? RunnerOnThird { get; set; }
    public bool IsPlayComplete { get; set; }
    public string? LastPlayEvent { get; set; }
    public string? LastPlayDescription { get; set; }
    public List<int> HomeBattingOrder { get; set; } = [];
    public List<int> AwayBattingOrder { get; set; } = [];
    public List<int> HomePitchers { get; set; } = [];
    public List<int> AwayPitchers { get; set; } = [];
    public Dictionary<int, PlayerGameStats> PlayerStats { get; set; } = [];

    public LiveFeedData Clone()
    {
        var clone = (LiveFeedData)MemberwiseClone();
        clone.HomeBattingOrder = [.. HomeBattingOrder];
        clone.AwayBattingOrder = [.. AwayBattingOrder];
        clone.HomePitchers = [.. HomePitchers];
        clone.AwayPitchers = [.. AwayPitchers];
        clone.PlayerStats = PlayerStats.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        return clone;
    }

    public bool Equals(LiveFeedData? other) =>
        other is not null
        && TimeStamp == other.TimeStamp
        && GameState == other.GameState
        && DetailedState == other.DetailedState
        && CurrentBatterId == other.CurrentBatterId
        && CurrentBatterName == other.CurrentBatterName
        && CurrentPitcherId == other.CurrentPitcherId
        && CurrentPitcherName == other.CurrentPitcherName
        && Inning == other.Inning
        && InningHalf == other.InningHalf
        && InningState == other.InningState
        && HomeScore == other.HomeScore
        && AwayScore == other.AwayScore
        && HomeTeam == other.HomeTeam
        && AwayTeam == other.AwayTeam
        && HomeTeamId == other.HomeTeamId
        && AwayTeamId == other.AwayTeamId
        && Balls == other.Balls
        && Strikes == other.Strikes
        && Outs == other.Outs
        && RunnerOnFirst == other.RunnerOnFirst
        && RunnerOnSecond == other.RunnerOnSecond
        && RunnerOnThird == other.RunnerOnThird
        && IsPlayComplete == other.IsPlayComplete
        && LastPlayEvent == other.LastPlayEvent
        && LastPlayDescription == other.LastPlayDescription
        && HomeBattingOrder.SequenceEqual(other.HomeBattingOrder)
        && AwayBattingOrder.SequenceEqual(other.AwayBattingOrder)
        && HomePitchers.SequenceEqual(other.HomePitchers)
        && AwayPitchers.SequenceEqual(other.AwayPitchers)
        && PlayerStatsEqual(other.PlayerStats);

    private bool PlayerStatsEqual(Dictionary<int, PlayerGameStats> other)
    {
        if (PlayerStats.Count != other.Count) return false;

        foreach (var (id, stats) in PlayerStats)
        {
            if (!other.TryGetValue(id, out var otherStats) || !stats.Equals(otherStats)) return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as LiveFeedData);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TimeStamp);
        hash.Add(GameState);
        hash.Add(DetailedState);
        hash.Add(CurrentBatterId);
        hash.Add(CurrentPitcherId);
        hash.Add(Inning);
        hash.Add(HomeScore);
        hash.Add(AwayScore);
        hash.Add(Balls);
        hash.Add(Strikes);
        hash.Add(Outs);
        hash.Add(RunnerOnFirst);
        hash.Add(RunnerOnSecond);
        hash.Add(RunnerOnThird);
        hash.Add(IsPlayComplete);
        foreach (var id in HomeBattingOrder) hash.Add(id);
        foreach (var id in AwayBattingOrder) hash.Add(id);
        foreach (var id in HomePitchers) hash.Add(id);
        foreach (var id in AwayPitchers) hash.Add(id);
        hash.Add(PlayerStats.Count);
        return hash.ToHashCode();
    }
}

public sealed class PlayerGameStats : IEquatable<PlayerGameStats>
{
    public PlayerBattingStats? Batting { get; set; }
    public PlayerPitchingStats? Pitching { get; set; }

    public PlayerGameStats Clone() => new()
    {
        Batting = Batting?.Clone(),
        Pitching = Pitching?.Clone(),
    };

    public bool Equals(PlayerGameStats? other) =>
        other is not null
        && Equals(Batting, other.Batting)
        && Equals(Pitching, other.Pitching);

    public override bool Equals(object? obj) => Equals(obj as PlayerGameStats);

    public override int GetHashCode() => HashCode.Combine(Batting, Pitching);
}

public sealed class PlayerBattingStats : IEquatable<PlayerBattingStats>
{
    public int? AtBats { get; set; }
    public int? Hits { get; set; }
    public int? Runs { get; set; }
    public int? Doubles { get; set; }
    public int? Triples { get; set; }
    public int? HomeRuns { get; set; }
    public int? Rbi { get; set; }
    public int? BaseOnBalls { get; set; }
    public int? StrikeOuts { get; set; }
    public int? StolenBases { get; set; }

    public string? Formatted
    {
        get
        {
            if (AtBats is not { } atBats) return null;

            var hasActivity = atBats > 0 || (BaseOnBalls ?? 0) > 0 || (StolenBases ?? 0) > 0;
            if (!hasActivity) return null;

            var line = $"{Hits ?? 0}-{atBats}";
            var extras = new List<string>();
            if (Doubles is > 0 and var doubles) extras.Add(doubles > 1 ? $"{doubles} 2B" : "2B");
            if (Triples is > 0 and var triples) extras.Add(triples > 1 ? $"{triples} 3B" : "3B");
            if (HomeRuns is > 0 and var homeRuns) extras.Add(homeRuns > 1 ? $"{homeRuns} HR" : "HR");
            if (Rbi is > 0 and var rbi) extras.Add($"{rbi} RBI");
            if (Runs is > 0 and var runs) extras.Add($"{runs} R");
            if (BaseOnBalls is > 0 and var walks) extras.Add(walks > 1 ? $"{walks} BB" : "BB");
            if (StolenBases is > 0 and var steals) extras.Add(steals > 1 ? $"{steals} SB" : "SB");

            return extras.Count > 0 ? line + " · " + string.Join(", ", extras) : line;
        }
    }

    public PlayerBattingStats Clone() => (PlayerBattingStats)MemberwiseClone();

    public bool Equals(PlayerBattingStats? other) =>
        other is not null
        && AtBats == other.AtBats
        && Hits == other.Hits
        && Runs == other.Runs
        && Doubles == other.Doubles
        && Triples == other.Triples
        && HomeRuns == other.HomeRuns
        && Rbi == other.Rbi
        && BaseOnBalls == other.BaseOnBalls
        && StrikeOuts == other.StrikeOuts
        && StolenBases == other.StolenBases;

    public override bool Equals(object? obj) => Equals(obj as PlayerBattingStats);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(AtBats);
        hash.Add(Hits);
        hash.Add(Runs);
        hash.Add(Doubles);
        hash.Add(Triples);
        hash.Add(HomeRuns);
        hash.Add(Rbi);
        hash.Add(BaseOnBalls);
        hash.Add(StrikeOuts);
        hash.Add(StolenBases);
        return hash.ToHashCode();
    }
}

public sealed class PlayerPitchingStats : IEquatable<PlayerPitchingStats>
{
    public string? InningsPitched { get; set; }
    public int? Hits { get; set; }
    public int? EarnedRuns { get; set; }
    public int? StrikeOuts { get; set; }
    public int? BaseOnBalls { get; set; }
    public int? NumberOfPitches { get; set; }

    public string? Formatted
    {
        get
        {
            if (InningsPitched is not { } ip || ip == "0.0") return null;

            var parts = new List<string> { $"{ip} IP" };
            if (StrikeOuts is > 0 and var strikeOuts) parts.Add($"{strikeOuts}K");
            if (EarnedRuns is { } earnedRuns) parts.Add($"{earnedRuns}ER");
            if (NumberOfPitches is > 0 and var pitches) parts.Add($"{pitches}P");
            return string.Join(", ", parts);
        }
    }

    public PlayerPitchingStats Clone() => (PlayerPitchingStats)MemberwiseClone();

    public bool Equals(PlayerPitchingStats? other) =>
        other is not null
        && InningsPitched == other.InningsPitched
        && Hits == other.Hits
        && EarnedRuns == other.EarnedRuns
        && StrikeOuts == other.StrikeOuts
        && BaseOnBalls == other.BaseOnBalls
        && NumberOfPitches == other.NumberOfPitches;

    public override bool Equals(object? obj) => Equals(obj as PlayerPitchingStats);

    public override int GetHashCode() =>
        HashCode.Combine(InningsPitched, Hits, EarnedRuns, StrikeOuts, BaseOnBalls, NumberOfPitches);
}
