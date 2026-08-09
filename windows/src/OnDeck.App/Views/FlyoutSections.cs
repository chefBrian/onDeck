using OnDeck.Core.Models;

namespace OnDeck.App.Views;

/// <summary>Identifies a section so the floating panel knows which header owns its buttons.</summary>
public enum FlyoutSectionKind
{
    Active,
    InGame,
    Upcoming,
    Done,
    Empty,
}

/// <summary>
/// Everything <c>MenuBarView.swift</c> reads off <c>AppState</c> to lay the flyout out, as plain
/// values. Taking this rather than the orchestrator keeps the layout rules testable without
/// standing up the whole engine.
/// </summary>
public sealed record FlyoutInput
{
    public IReadOnlyList<PlayerDisplay> Active { get; init; } = [];
    public IReadOnlyList<PlayerDisplay> InGame { get; init; } = [];
    public IReadOnlyList<PlayerDisplay> Upcoming { get; init; } = [];
    public IReadOnlyList<PlayerDisplay> Done { get; init; } = [];
    public bool IsSyncing { get; init; }
    public bool HasRosterUrl { get; init; }
    public int LoadedPlayerCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The laid-out flyout: which sections appear, which dividers follow them, and the empty/error
/// copy. Port of the section structure in <c>Views/MenuBarView.swift</c>.
/// </summary>
public sealed record FlyoutSections
{
    public IReadOnlyList<LiveRowViewModel> Active { get; init; } = [];
    public IReadOnlyList<LiveRowViewModel> InGame { get; init; } = [];
    public IReadOnlyList<UpcomingRowViewModel> Upcoming { get; init; } = [];
    public IReadOnlyList<DoneRowViewModel> Done { get; init; } = [];

    public string? EmptyText { get; init; }
    public string? ErrorText { get; init; }

    public bool ShowsActive => Active.Count > 0;
    public bool ShowsInGame => InGame.Count > 0;
    public bool ShowsUpcoming => Upcoming.Count > 0;
    public bool ShowsDone => Done.Count > 0;
    public bool ShowsEmpty => EmptyText is not null;
    public bool ShowsError => ErrorText is not null;

    public bool ActiveDivider { get; init; }
    public bool InGameDivider { get; init; }
    public bool UpcomingDivider { get; init; }
    public bool DoneDivider { get; init; }
    public bool EmptyDivider { get; init; }
    public bool ErrorDivider { get; init; }

    /// <summary>Whose header carries the floating panel's refresh and close buttons.</summary>
    public FlyoutSectionKind HeaderControlsSection { get; init; }

    public static FlyoutSections Build(
        FlyoutInput input, bool isFloating, Func<int, string?> logoPath)
    {
        var active = input.Active.Select(row => RowViewModel.Live(row, logoPath)).ToList();
        var inGame = input.InGame.Select(row => RowViewModel.Live(row, logoPath)).ToList();
        var upcoming = input.Upcoming.Select(RowViewModel.Upcoming).ToList();
        var done = input.Done.Select(RowViewModel.Done).ToList();

        var isEmpty = active.Count == 0 && inGame.Count == 0
                      && upcoming.Count == 0 && done.Count == 0;

        return new FlyoutSections
        {
            Active = active,
            InGame = inGame,
            Upcoming = upcoming,
            Done = done,
            EmptyText = isEmpty ? EmptyTextFor(input) : null,
            ErrorText = string.IsNullOrEmpty(input.Error) ? null : input.Error,

            // Everything gets a divider; floating mode drops it on whatever ends up last so the
            // panel doesn't end in a hanging rule.
            ActiveDivider = true,
            InGameDivider = true,
            UpcomingDivider = !isFloating || done.Count > 0,
            DoneDivider = !isFloating,
            EmptyDivider = !isFloating,
            ErrorDivider = true,

            HeaderControlsSection =
                active.Count > 0 ? FlyoutSectionKind.Active
                : inGame.Count > 0 ? FlyoutSectionKind.InGame
                : upcoming.Count > 0 ? FlyoutSectionKind.Upcoming
                : done.Count > 0 ? FlyoutSectionKind.Done
                : FlyoutSectionKind.Empty,
        };
    }

    private static string EmptyTextFor(FlyoutInput input)
    {
        if (input.IsSyncing) return "Syncing roster...";
        if (!input.HasRosterUrl) return "Set roster URL in Settings";
        if (input.LoadedPlayerCount == 0) return "No players found";
        return "No games today";
    }
}

/// <summary>
/// Reads a <see cref="FlyoutInput"/> off the orchestrator. The one place the shell touches
/// Core's published state, so the layout rules stay testable on plain values.
/// </summary>
public static class FlyoutInputFactory
{
    public static FlyoutInput From(OnDeck.Core.AppOrchestrator orchestrator) => new()
    {
        Active = orchestrator.ActivePlayers,
        InGame = orchestrator.InGamePlayers,
        Upcoming = orchestrator.UpcomingPlayers,
        Done = orchestrator.DonePlayers,
        IsSyncing = orchestrator.IsSyncing,

        // An unparseable URL and no URL are the same thing to the empty-state copy, and the
        // orchestrator doesn't publish the raw value.
        HasRosterUrl = orchestrator.ParsedLeagueId is not null,
        LoadedPlayerCount = orchestrator.LoadedPlayerCount,
        Error = orchestrator.SyncError,
    };

    /// <summary>The team ids whose logos are on screen right now.</summary>
    public static IEnumerable<int> TeamIds(FlyoutInput input) =>
        input.Active.Concat(input.InGame)
            .Select(display => display.Feed)
            .OfType<LiveFeedData>()
            .SelectMany(feed => new[] { feed.AwayTeamId, feed.HomeTeamId })
            .Distinct();
}
