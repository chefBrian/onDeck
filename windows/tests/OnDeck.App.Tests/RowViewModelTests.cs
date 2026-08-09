using OnDeck.App.Views;
using OnDeck.Core.Models;

namespace OnDeck.App.Tests;

public class RowViewModelTests
{
    private const int PlayerId = 605141;
    private const int TeammateId = 660271;

    private static Player Hitter(int id = PlayerId) =>
        new(id, "Mookie Betts", "Los Angeles Dodgers",
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static LiveFeedData Feed() => new()
    {
        GameState = "Live",
        DetailedState = "In Progress",
        Inning = 7,
        InningHalf = "Bottom",
        HomeTeamId = 119,
        AwayTeamId = 137,
        HomeScore = 4,
        AwayScore = 2,
        Balls = 2,
        Strikes = 1,
        Outs = 1,
    };

    private static PlayerDisplay LiveDisplay(
        LiveFeedData? feed = null,
        bool isActive = false,
        BattingProximity? proximity = null,
        string? statLine = null,
        DelayIndicator delay = DelayIndicator.None) =>
        new()
        {
            Player = Hitter(),
            GamePk = 745804,
            Feed = feed,
            IsActive = isActive,
            Proximity = proximity,
            StatLine = statLine,
            Delay = delay,
            StreamUrl = new Uri("https://www.mlb.com/tv"),
        };

    private static string? NoLogos(int teamId) => null;

    [Fact]
    public void Live_CarriesTheIdentityFields()
    {
        var row = RowViewModel.Live(
            LiveDisplay(Feed(), isActive: true, statLine: "1-3 · RBI"), NoLogos);

        Assert.Equal(PlayerId, row.PlayerId);
        Assert.Equal("Mookie Betts", row.Name);
        Assert.True(row.IsActive);
        Assert.Equal("1-3 · RBI", row.StatLine);
        Assert.Equal(new Uri("https://www.mlb.com/tv"), row.StreamUrl);
    }

    [Fact]
    public void Live_HasNoFeedBeforeTheFirstPoll()
    {
        var row = RowViewModel.Live(LiveDisplay(feed: null), NoLogos);

        Assert.False(row.HasFeed);
    }

    [Fact]
    public void Live_TakesItsDotFromTheProximity()
    {
        Assert.Equal(
            ProximityDot.Outlined,
            RowViewModel.Live(LiveDisplay(Feed(), proximity: BattingProximity.OnDeck), NoLogos).Dot);
    }

    [Fact]
    public void Live_ShowsTheCountDuringAnAtBat()
    {
        var row = RowViewModel.Live(LiveDisplay(Feed()), NoLogos);

        Assert.Equal("2-1", row.CountText);
        Assert.Equal(1, row.Outs);
    }

    [Fact]
    public void Live_BlanksTheCountWhenThePlayIsComplete()
    {
        var feed = Feed();
        feed.IsPlayComplete = true;

        Assert.Equal(" ", RowViewModel.Live(LiveDisplay(feed), NoLogos).CountText);
    }

    [Fact]
    public void Live_BlanksTheCountAtZeroAndZero()
    {
        var feed = Feed();
        feed.Balls = 0;
        feed.Strikes = 0;

        Assert.Equal(" ", RowViewModel.Live(LiveDisplay(feed), NoLogos).CountText);
    }

    [Fact]
    public void Live_PointsTheInningArrowByHalf()
    {
        var top = Feed();
        top.InningHalf = "Top";

        Assert.True(RowViewModel.Live(LiveDisplay(top), NoLogos).IsTopHalf);
        Assert.False(RowViewModel.Live(LiveDisplay(Feed()), NoLogos).IsTopHalf);
        Assert.Equal("7", RowViewModel.Live(LiveDisplay(Feed()), NoLogos).InningText);
    }

    [Fact]
    public void Live_ShowsZeroInningWhenTheFeedHasNone()
    {
        var feed = Feed();
        feed.Inning = null;

        Assert.Equal("0", RowViewModel.Live(LiveDisplay(feed), NoLogos).InningText);
    }

    [Fact]
    public void Live_HighlightsABaseThisPlayerIsStandingOn()
    {
        var feed = Feed();
        feed.RunnerOnFirst = PlayerId;
        feed.RunnerOnSecond = TeammateId;

        var row = RowViewModel.Live(LiveDisplay(feed), NoLogos);

        Assert.Equal(BaseState.Highlighted, row.First);
        Assert.Equal(BaseState.Occupied, row.Second);
        Assert.Equal(BaseState.Empty, row.Third);
    }

    [Fact]
    public void Live_CarriesTheScoreBlock()
    {
        var row = RowViewModel.Live(LiveDisplay(Feed()), teamId => $"C:\\logos\\{teamId}.png");

        Assert.Equal(2, row.AwayScore);
        Assert.Equal(4, row.HomeScore);
        Assert.Equal("C:\\logos\\137.png", row.AwayLogoPath);
        Assert.Equal("C:\\logos\\119.png", row.HomeLogoPath);
    }

    [Fact]
    public void Live_CarriesTheDelayGlyph()
    {
        var row = RowViewModel.Live(LiveDisplay(Feed(), delay: DelayIndicator.Rain), NoLogos);

        Assert.Equal(DisplayFormatting.DelayGlyph(DelayIndicator.Rain), row.DelayGlyph);
        Assert.Null(RowViewModel.Live(LiveDisplay(Feed()), NoLogos).DelayGlyph);
    }

    [Fact]
    public void Upcoming_CarriesBadgeAndTrailingText()
    {
        var start = new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero);
        var display = new PlayerDisplay
        {
            Player = Hitter(),
            Lineup = LineupInfo.BattingOrder(2),
            StartTime = start,
        };

        var row = RowViewModel.Upcoming(display);

        Assert.Equal(PlayerId, row.PlayerId);
        Assert.Equal(LineupBadge.Order, row.Badge);
        Assert.Equal("2", row.BadgeText);
        Assert.Equal(start.ToLocalTime().ToString("t"), row.TrailingText);
        Assert.Null(row.DelayGlyph);
    }

    [Fact]
    public void Upcoming_ShowsPpdInsteadOfAStartTime()
    {
        var display = new PlayerDisplay
        {
            Player = Hitter(),
            Lineup = LineupInfo.NotInLineup,
            Delay = DelayIndicator.Postponed,
            StartTime = DateTimeOffset.UtcNow.AddHours(3),
        };

        var row = RowViewModel.Upcoming(display);

        Assert.Equal("PPD", row.TrailingText);
        Assert.Equal(LineupBadge.Missing, row.Badge);
        Assert.Equal(DisplayFormatting.DelayGlyph(DelayIndicator.Postponed), row.DelayGlyph);
    }

    [Fact]
    public void Done_IsNameAndStatLine()
    {
        var display = new PlayerDisplay
        {
            Player = Hitter(),
            GamePk = 745804,
            StatLine = "2-4 · HR, 3 RBI",
        };

        var row = RowViewModel.Done(display);

        Assert.Equal(PlayerId, row.PlayerId);
        Assert.Equal("Mookie Betts", row.Name);
        Assert.Equal("2-4 · HR, 3 RBI", row.StatLine);
    }
}
