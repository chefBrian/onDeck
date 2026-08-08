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
}
