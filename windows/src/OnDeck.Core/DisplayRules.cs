using OnDeck.Core.Models;

namespace OnDeck.Core;

/// <summary>
/// The display rules from <c>Views/MenuBarView.swift</c>, as pure functions over the feed and
/// lineup snapshots. <see cref="AppOrchestrator"/> calls these while building
/// <see cref="PlayerDisplay"/> rows.
/// </summary>
internal static class DisplayRules
{
    /// <summary>
    /// Port of <c>battingProximity(for:in:)</c>. Null for pitcher-only players, players in
    /// neither batting order, and before the first feed arrives.
    /// </summary>
    public static BattingProximity? ProximityFor(Player player, LiveFeedData? feed)
    {
        if (feed is null) return null;
        if (player.IsPitcher && !player.IsHitter) return null;

        bool isHome;
        if (feed.HomeBattingOrder.Contains(player.Id)) isHome = true;
        else if (feed.AwayBattingOrder.Contains(player.Id)) isHome = false;
        else return null;

        var battingOrder = isHome ? feed.HomeBattingOrder : feed.AwayBattingOrder;
        var playerIndex = battingOrder.IndexOf(player.Id);
        if (playerIndex < 0) return null;

        // Between half-innings MLB keeps currentBatter/inningHalf as a stale holdover from the
        // previous play, so the 3rd-out hitter would still look "at bat" until play resumes.
        var isBreak = feed.InningState is "Middle" or "End";
        var teamIsBatting = !isBreak
            && ((isHome && feed.InningHalf == "Bottom") || (!isHome && feed.InningHalf == "Top"));

        if (!teamIsBatting || feed.CurrentBatterId is not { } currentBatterId)
        {
            return BattingProximity.NotBatting(playerIndex);
        }

        var currentIndex = battingOrder.IndexOf(currentBatterId);
        if (currentIndex < 0) return BattingProximity.NotBatting(playerIndex);

        var distance = (playerIndex - currentIndex + battingOrder.Count) % battingOrder.Count;
        return distance switch
        {
            0 => BattingProximity.AtBat,
            1 => BattingProximity.OnDeck,
            2 => BattingProximity.DueUp,
            _ => BattingProximity.Order(distance),
        };
    }

    /// <summary>
    /// Port of <c>inGameSortKey(for:proximity:in:)</c>. Tiers stack on top of the proximity
    /// sort: 0 = normal proximity, +100 = mid-game delay, +200 = lineup card filed without
    /// this player. Pitchers have no proximity — base 0 if currently pitching (live action,
    /// like at bat) or 70 otherwise (above notBatting hitters, below the delay tier).
    /// </summary>
    public static int InGameSortKey(
        Player player, Game? game, LiveFeedData? feed, GameLineup? lineup, BattingProximity? proximity)
    {
        if (game is null) return proximity?.SortKey ?? 70;

        var baseKey = proximity?.SortKey ?? (feed?.CurrentPitcherId == player.Id ? 0 : 70);

        // Not in Lineup: own side's card is filed and this player isn't on it.
        if (game.SideFor(player) is { } side && lineup is not null && lineup.Excludes(player, side))
        {
            return 200 + baseKey;
        }

        if (feed?.DetailedState is { } detailed
            && (detailed.StartsWith("Delayed", StringComparison.Ordinal)
                || detailed.StartsWith("Suspended", StringComparison.Ordinal)))
        {
            return 100 + baseKey;
        }

        return baseKey;
    }

    /// <summary>
    /// Port of <c>LivePlayerRow.isInLineup</c>. Assumes the player is in until that side's
    /// card is filed; false only when the filed card omits them.
    /// </summary>
    public static bool IsInLineup(Player player, Game? game, GameLineup? lineup)
    {
        if (game is null) return false;
        if (game.SideFor(player) is not { } side || lineup is null) return true;
        return !lineup.Excludes(player, side);
    }

    /// <summary>The boxscore line for this player's role, or null when they have no stats yet.</summary>
    public static string? RawStatLine(Player player, LiveFeedData? feed)
    {
        if (feed is null) return null;
        if (!feed.PlayerStats.TryGetValue(player.Id, out var stats)) return null;

        return player.IsPitcher && !player.IsHitter ? stats.Pitching?.Formatted : stats.Batting?.Formatted;
    }

    /// <summary>Port of <c>LivePlayerRow.formattedStatLine(gamePk:)</c>.</summary>
    public static string? LiveStatLine(
        Player player, LiveFeedData? feed, bool isInLineup, BattingProximity? proximity)
    {
        if (!isInLineup) return "Not in Lineup";
        if (feed is null) return null;

        var statLine = RawStatLine(player, feed);

        if (DelayLabel(feed.DetailedState) is { } delay)
        {
            return statLine is null ? delay : $"{delay} · {statLine}";
        }

        if (player.IsPitcher && !player.IsHitter) return statLine;

        var prefix = proximity?.Kind switch
        {
            BattingProximityKind.OnDeck => "On Deck",
            BattingProximityKind.DueUp => "In Hole",
            _ => null,
        };

        return (prefix, statLine) switch
        {
            (not null, not null) => $"{prefix} · {statLine}",
            (not null, null) => prefix,
            (null, not null) => statLine,
            _ => null,
        };
    }

    /// <summary>
    /// Port of <c>LivePlayerRow.delayLabel(detailedState:)</c>. Mid-game pauses only —
    /// pre-game delays carry abstractGameState "Preview" and never reach this path.
    /// </summary>
    public static string? DelayLabel(string? detailedState)
    {
        if (detailedState is not { } detailed) return null;

        const string delayedPrefix = "Delayed: ";
        if (detailed.StartsWith(delayedPrefix, StringComparison.Ordinal))
        {
            return $"{detailed[delayedPrefix.Length..]} Delay";
        }

        const string suspendedPrefix = "Suspended: ";
        if (detailed.StartsWith(suspendedPrefix, StringComparison.Ordinal))
        {
            return $"Suspended: {detailed[suspendedPrefix.Length..]}";
        }

        if (detailed == "Delayed") return "Delayed";
        if (detailed == "Suspended") return "Suspended";

        return null;
    }

    /// <summary>Port of <c>delayIcon(detailedState:)</c>, classified rather than iconified.</summary>
    public static DelayIndicator DelayFor(string? detailedState)
    {
        if (detailedState is not { } detailed) return DelayIndicator.None;
        if (detailed.Contains("Rain", StringComparison.Ordinal)) return DelayIndicator.Rain;
        if (detailed.StartsWith("Delayed", StringComparison.Ordinal)
            || detailed.StartsWith("Suspended", StringComparison.Ordinal))
        {
            return DelayIndicator.Delayed;
        }

        return detailed == "Postponed" ? DelayIndicator.Postponed : DelayIndicator.None;
    }

    /// <summary>Port of <c>UpcomingPlayerRow.lineupInfo</c>.</summary>
    public static LineupInfo LineupInfoFor(
        Player player, Game? game, GameLineup? lineup, LiveFeedData? feed)
    {
        if (game is null
            || game.SideFor(player) is not { } side
            || lineup is null
            || !lineup.IsSubmitted(side))
        {
            return LineupInfo.Unknown;
        }

        if (lineup.Excludes(player, side)) return LineupInfo.NotInLineup;
        if (!lineup.Ids(side).Contains(player.Id)) return LineupInfo.Unknown;

        // Check the live feed first, then fall back to schedule lineup data.
        if (feed is not null)
        {
            var homeIndex = feed.HomeBattingOrder.IndexOf(player.Id);
            if (homeIndex >= 0) return LineupInfo.BattingOrder(homeIndex + 1);

            var awayIndex = feed.AwayBattingOrder.IndexOf(player.Id);
            if (awayIndex >= 0) return LineupInfo.BattingOrder(awayIndex + 1);
        }

        var scheduledHome = IndexOf(game.HomeLineup, player.Id);
        if (scheduledHome >= 0) return LineupInfo.BattingOrder(scheduledHome + 1);

        var scheduledAway = IndexOf(game.AwayLineup, player.Id);
        if (scheduledAway >= 0) return LineupInfo.BattingOrder(scheduledAway + 1);

        return LineupInfo.InLineup;
    }

    private static int IndexOf(IReadOnlyList<int> values, int value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value) return index;
        }

        return -1;
    }
}
