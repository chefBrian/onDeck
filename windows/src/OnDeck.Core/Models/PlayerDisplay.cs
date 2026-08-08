namespace OnDeck.Core.Models;

/// <summary>
/// Port of the private <c>BattingProximity</c> enum in <c>Views/MenuBarView.swift</c>.
/// Swift's associated values become <see cref="BattingProximity.Value"/>: the distance from
/// the current batter for <see cref="Order"/>, the lineup spot index for
/// <see cref="NotBatting"/>, unused otherwise.
/// </summary>
public enum BattingProximityKind
{
    AtBat,
    OnDeck,
    DueUp,
    Order,
    NotBatting,
}

/// <summary>
/// A hitter's distance from the plate. <c>default</c> is <see cref="AtBat"/> — callers use
/// <c>BattingProximity?</c> to mean "no proximity" (pitcher-only, not in the order, no feed).
/// </summary>
public readonly record struct BattingProximity
{
    private BattingProximity(BattingProximityKind kind, int value)
    {
        Kind = kind;
        Value = value;
    }

    public BattingProximityKind Kind { get; }

    public int Value { get; }

    public static readonly BattingProximity AtBat = new(BattingProximityKind.AtBat, 0);

    public static readonly BattingProximity OnDeck = new(BattingProximityKind.OnDeck, 0);

    public static readonly BattingProximity DueUp = new(BattingProximityKind.DueUp, 0);

    /// <param name="distance">Distance from the current batter, 3...8.</param>
    public static BattingProximity Order(int distance) => new(BattingProximityKind.Order, distance);

    /// <param name="spot">Lineup spot index; the other team is up.</param>
    public static BattingProximity NotBatting(int spot) => new(BattingProximityKind.NotBatting, spot);

    /// <summary>
    /// Distance-based while the team is batting (0 = at bat, 8 = just finished) so the player
    /// who just batted sinks and bubbles back up as the lineup cycles. <c>notBatting</c> bumps
    /// into a separate band so a leadoff hitter on a non-batting team doesn't tie with on-deck.
    /// </summary>
    public int SortKey => Kind switch
    {
        BattingProximityKind.AtBat => 0,
        BattingProximityKind.OnDeck => 1,
        BattingProximityKind.DueUp => 2,
        BattingProximityKind.Order => Value,
        _ => 50 + Value,
    };
}

/// <summary>Port of <c>UpcomingPlayerRow.LineupInfo</c> in <c>Views/MenuBarView.swift</c>.</summary>
public enum LineupInfoKind
{
    Unknown,
    NotInLineup,
    InLineup,
    BattingOrder,
}

/// <summary>The upcoming-row lineup badge. <c>default</c> is <see cref="Unknown"/>.</summary>
public readonly record struct LineupInfo
{
    private LineupInfo(LineupInfoKind kind, int spot)
    {
        Kind = kind;
        Spot = spot;
    }

    public LineupInfoKind Kind { get; }

    /// <summary>1-based batting order spot; 0 unless <see cref="Kind"/> is BattingOrder.</summary>
    public int Spot { get; }

    public static readonly LineupInfo Unknown = new(LineupInfoKind.Unknown, 0);

    public static readonly LineupInfo NotInLineup = new(LineupInfoKind.NotInLineup, 0);

    public static readonly LineupInfo InLineup = new(LineupInfoKind.InLineup, 0);

    public static LineupInfo BattingOrder(int spot) => new(LineupInfoKind.BattingOrder, spot);
}

/// <summary>
/// Port of <c>delayIcon(detailedState:)</c> in <c>Views/MenuBarView.swift</c>. The icon
/// choice itself is the shell's; Core only classifies. Shared by UPCOMING (pre-game
/// "Delayed Start: Rain") and IN GAME (mid-game "Delayed: Rain").
/// </summary>
public enum DelayIndicator
{
    None,
    Rain,
    Delayed,
    Postponed,
}

/// <summary>
/// One rendered row. Fields are exactly what <c>Views/MenuBarView.swift</c> reads out of
/// <c>AppState</c> for a player, resolved once on the Core context so the shell never has to
/// reach back into <c>GameMonitor</c> while rendering.
/// </summary>
public sealed record PlayerDisplay
{
    public required Player Player { get; init; }

    /// <summary>The game this player's team is in today, if any.</summary>
    public int? GamePk { get; init; }

    /// <summary>
    /// Latest feed for <see cref="GamePk"/>. The live row reads score, bases, count, outs,
    /// inning and half off it directly.
    /// </summary>
    public LiveFeedData? Feed { get; init; }

    /// <summary>True when the player's state is <c>Active</c> (at bat or on the mound).</summary>
    public bool IsActive { get; init; }

    public BattingProximity? Proximity { get; init; }

    /// <summary>False only when this player's own side filed a card without them.</summary>
    public bool IsInLineup { get; init; } = true;

    /// <summary>The UPCOMING row badge; <see cref="LineupInfo.Unknown"/> for other sections.</summary>
    public LineupInfo Lineup { get; init; }

    /// <summary>
    /// The secondary line: "Not in Lineup", a delay label, an "On Deck"/"In Hole" prefix and
    /// the boxscore stat line, composed per section.
    /// </summary>
    public string? StatLine { get; init; }

    public DelayIndicator Delay { get; init; }

    /// <summary>Scheduled first pitch; set on UPCOMING rows only.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Where a click on the row goes.</summary>
    public Uri? StreamUrl { get; init; }

    /// <summary>IN GAME ordering key; 0 for other sections.</summary>
    public int SortKey { get; init; }

    public int Id => Player.Id;

    public string Name => Player.Name;
}
