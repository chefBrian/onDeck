using OnDeck.Core.Models;

namespace OnDeck.App.Views;

/// <summary>One base on the diamond. Highlighted means the row's own player is standing on it.</summary>
public enum BaseState
{
    Empty,
    Occupied,
    Highlighted,
}

/// <summary>
/// A row in ACTIVE NOW or IN GAME — the port of <c>LivePlayerRow</c>. Every field is already
/// resolved; the template binds and nothing more.
/// </summary>
public sealed record LiveRowViewModel
{
    public required int PlayerId { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; }
    public ProximityDot Dot { get; init; }
    public string? StatLine { get; init; }
    public string? DelayGlyph { get; init; }

    /// <summary>False until the first live feed lands; the row collapses to a name + "In Game".</summary>
    public bool HasFeed { get; init; }

    public Uri? StreamUrl { get; init; }

    public string? AwayLogoPath { get; init; }
    public string? HomeLogoPath { get; init; }
    public int AwayScore { get; init; }
    public int HomeScore { get; init; }

    public BaseState First { get; init; }
    public BaseState Second { get; init; }
    public BaseState Third { get; init; }

    public bool IsTopHalf { get; init; }
    public string InningText { get; init; } = "0";

    /// <summary>"balls-strikes", or a single space between at-bats so the row doesn't reflow.</summary>
    public string CountText { get; init; } = " ";

    public int Outs { get; init; }
}

/// <summary>A row in UPCOMING — the port of <c>UpcomingPlayerRow</c>. Not clickable on macOS either.</summary>
public sealed record UpcomingRowViewModel
{
    public required int PlayerId { get; init; }
    public required string Name { get; init; }
    public LineupBadge Badge { get; init; }
    public string? BadgeText { get; init; }
    public string? DelayGlyph { get; init; }
    public string TrailingText { get; init; } = "";
}

/// <summary>A row in DONE — the port of <c>DonePlayerRow</c>.</summary>
public sealed record DoneRowViewModel
{
    public required int PlayerId { get; init; }
    public required string Name { get; init; }
    public string? StatLine { get; init; }
}

/// <summary>Projects <see cref="PlayerDisplay"/> onto the three row shapes the templates bind to.</summary>
public static class RowViewModel
{
    /// <param name="logoPath">Team id to an on-disk logo, or null when it hasn't been fetched.</param>
    public static LiveRowViewModel Live(PlayerDisplay display, Func<int, string?> logoPath)
    {
        var feed = display.Feed;

        return new LiveRowViewModel
        {
            PlayerId = display.Id,
            Name = display.Name,
            IsActive = display.IsActive,
            Dot = DisplayFormatting.Dot(display),
            StatLine = display.StatLine,
            DelayGlyph = DisplayFormatting.DelayGlyph(display.Delay),
            HasFeed = feed is not null,
            StreamUrl = display.StreamUrl,

            AwayLogoPath = feed is null ? null : logoPath(feed.AwayTeamId),
            HomeLogoPath = feed is null ? null : logoPath(feed.HomeTeamId),
            AwayScore = feed?.AwayScore ?? 0,
            HomeScore = feed?.HomeScore ?? 0,

            First = BaseFor(feed?.RunnerOnFirst, display.Id),
            Second = BaseFor(feed?.RunnerOnSecond, display.Id),
            Third = BaseFor(feed?.RunnerOnThird, display.Id),

            IsTopHalf = feed?.InningHalf == "Top",
            InningText = (feed?.Inning ?? 0).ToString(),
            CountText = CountFor(feed),
            Outs = feed?.Outs ?? 0,
        };
    }

    public static UpcomingRowViewModel Upcoming(PlayerDisplay display) => new()
    {
        PlayerId = display.Id,
        Name = display.Name,
        Badge = DisplayFormatting.Badge(display),
        BadgeText = DisplayFormatting.LineupBadgeText(display),
        DelayGlyph = DisplayFormatting.DelayGlyph(display.Delay),
        TrailingText = DisplayFormatting.TrailingText(display),
    };

    public static DoneRowViewModel Done(PlayerDisplay display) => new()
    {
        PlayerId = display.Id,
        Name = display.Name,
        StatLine = display.StatLine,
    };

    private static BaseState BaseFor(int? runnerId, int playerId)
    {
        if (runnerId is not { } runner) return BaseState.Empty;
        return runner == playerId ? BaseState.Highlighted : BaseState.Occupied;
    }

    /// <summary>
    /// Blank between at-bats: MLB holds the previous count until the next pitch, so showing it
    /// would report a stale 3-2 while nobody is batting.
    /// </summary>
    private static string CountFor(LiveFeedData? feed)
    {
        if (feed is null) return " ";
        if (feed.IsPlayComplete) return " ";
        if (feed.Balls == 0 && feed.Strikes == 0) return " ";
        return $"{feed.Balls}-{feed.Strikes}";
    }
}
