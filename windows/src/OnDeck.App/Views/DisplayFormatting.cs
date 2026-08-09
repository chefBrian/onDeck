using OnDeck.Core.Models;

namespace OnDeck.App.Views;

/// <summary>Which dot precedes the player's name on a live row.</summary>
public enum ProximityDot
{
    None,
    Filled,      // at bat, or a pitcher currently on the mound
    Outlined,    // on deck
    Warning,     // in the hole
}

/// <summary>The badge in an upcoming row's leading column.</summary>
public enum LineupBadge
{
    None,
    Missing,     // red dot: this side's card was filed without them
    Present,     // green tick: on the card, spot unknown
    Order,       // their batting order number
}

/// <summary>
/// The presentation rules from <c>Views/MenuBarView.swift</c> that Core deliberately left to the
/// shell: which dot, which glyph, what trailing text. Everything they read was already resolved
/// onto <see cref="PlayerDisplay"/> — nothing here recomputes state.
/// </summary>
public static class DisplayFormatting
{
    // Segoe Fluent Icons: rain showers (day), clock with alert, blocked.
    private const string RainGlyph = "";
    private const string DelayedGlyph = "";
    private const string PostponedGlyph = "";

    public static ProximityDot Dot(PlayerDisplay display) => display.Proximity?.Kind switch
    {
        BattingProximityKind.AtBat => ProximityDot.Filled,
        BattingProximityKind.OnDeck => ProximityDot.Outlined,
        BattingProximityKind.DueUp => ProximityDot.Warning,
        BattingProximityKind.Order or BattingProximityKind.NotBatting => ProximityDot.None,

        // No proximity at all - a pitcher. Swift shows the filled dot when they're active.
        _ => display.IsActive ? ProximityDot.Filled : ProximityDot.None,
    };

    public static string? DelayGlyph(DelayIndicator delay) => delay switch
    {
        DelayIndicator.Rain => RainGlyph,
        DelayIndicator.Delayed => DelayedGlyph,
        DelayIndicator.Postponed => PostponedGlyph,
        _ => null,
    };

    /// <summary>Right-hand text on an upcoming row: "PPD" or the local first-pitch time.</summary>
    public static string TrailingText(PlayerDisplay display)
    {
        if (display.Delay == DelayIndicator.Postponed) return "PPD";
        return display.StartTime is { } start ? start.ToLocalTime().ToString("t") : "";
    }

    public static LineupBadge Badge(PlayerDisplay display) => display.Lineup.Kind switch
    {
        LineupInfoKind.NotInLineup => LineupBadge.Missing,
        LineupInfoKind.InLineup => LineupBadge.Present,
        LineupInfoKind.BattingOrder => LineupBadge.Order,
        _ => LineupBadge.None,
    };

    public static string? LineupBadgeText(PlayerDisplay display) =>
        display.Lineup.Kind == LineupInfoKind.BattingOrder
            ? display.Lineup.Spot.ToString()
            : null;
}
