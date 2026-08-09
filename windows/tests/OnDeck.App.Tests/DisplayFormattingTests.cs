using OnDeck.App.Views;
using OnDeck.Core.Models;

namespace OnDeck.App.Tests;

public class DisplayFormattingTests
{
    private static Player Hitter(int id = 101) =>
        new(id, $"Player {id}", "Los Angeles Dodgers",
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static PlayerDisplay Row(
        BattingProximity? proximity = null,
        bool isActive = false,
        LineupInfo lineup = default,
        DelayIndicator delay = DelayIndicator.None,
        DateTimeOffset? startTime = null) =>
        new()
        {
            Player = Hitter(),
            Proximity = proximity,
            IsActive = isActive,
            Lineup = lineup,
            Delay = delay,
            StartTime = startTime,
        };

    [Fact]
    public void Dot_IsFilledAtBatAndOutlinedOnDeck()
    {
        Assert.Equal(ProximityDot.Filled, DisplayFormatting.Dot(Row(BattingProximity.AtBat)));
        Assert.Equal(ProximityDot.Outlined, DisplayFormatting.Dot(Row(BattingProximity.OnDeck)));
        Assert.Equal(ProximityDot.Warning, DisplayFormatting.Dot(Row(BattingProximity.DueUp)));
    }

    [Fact]
    public void Dot_IsAbsentDeeperInTheOrder()
    {
        Assert.Equal(ProximityDot.None, DisplayFormatting.Dot(Row(BattingProximity.Order(5))));
        Assert.Equal(ProximityDot.None, DisplayFormatting.Dot(Row(BattingProximity.NotBatting(2))));
    }

    [Fact]
    public void Dot_FallsBackToTheActiveFlagForPitchers()
    {
        // A pitcher has no proximity; the green dot is driven by being active instead.
        Assert.Equal(ProximityDot.Filled, DisplayFormatting.Dot(Row(isActive: true)));
        Assert.Equal(ProximityDot.None, DisplayFormatting.Dot(Row()));
    }

    [Fact]
    public void DelayGlyph_IsNullWhenPlayIsNormal()
    {
        Assert.Null(DisplayFormatting.DelayGlyph(DelayIndicator.None));
    }

    [Theory]
    [InlineData(DelayIndicator.Rain)]
    [InlineData(DelayIndicator.Delayed)]
    [InlineData(DelayIndicator.Postponed)]
    public void DelayGlyph_IsADistinctGlyphPerIndicator(DelayIndicator delay)
    {
        var glyph = DisplayFormatting.DelayGlyph(delay);

        Assert.NotNull(glyph);
        Assert.NotEmpty(glyph);
    }

    [Fact]
    public void DelayGlyph_UsesADifferentGlyphForEachCase()
    {
        var glyphs = new[]
        {
            DisplayFormatting.DelayGlyph(DelayIndicator.Rain),
            DisplayFormatting.DelayGlyph(DelayIndicator.Delayed),
            DisplayFormatting.DelayGlyph(DelayIndicator.Postponed),
        };

        Assert.Equal(3, glyphs.Distinct().Count());
    }

    [Fact]
    public void TrailingText_IsPpdForAPostponedGame()
    {
        var row = Row(delay: DelayIndicator.Postponed, startTime: DateTimeOffset.Now.AddHours(3));

        Assert.Equal("PPD", DisplayFormatting.TrailingText(row));
    }

    [Fact]
    public void TrailingText_IsTheLocalStartTime()
    {
        var start = new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero);

        var text = DisplayFormatting.TrailingText(Row(startTime: start));

        Assert.Equal(start.ToLocalTime().ToString("t"), text);
    }

    [Fact]
    public void TrailingText_IsEmptyWithoutAStartTime()
    {
        Assert.Equal("", DisplayFormatting.TrailingText(Row()));
    }

    [Fact]
    public void Badge_ReflectsTheLineupInfo()
    {
        Assert.Equal(LineupBadge.None, DisplayFormatting.Badge(Row(lineup: LineupInfo.Unknown)));
        Assert.Equal(LineupBadge.Missing, DisplayFormatting.Badge(Row(lineup: LineupInfo.NotInLineup)));
        Assert.Equal(LineupBadge.Present, DisplayFormatting.Badge(Row(lineup: LineupInfo.InLineup)));
        Assert.Equal(LineupBadge.Order, DisplayFormatting.Badge(Row(lineup: LineupInfo.BattingOrder(3))));
    }

    [Fact]
    public void LineupBadgeText_IsTheOrderNumberOnly()
    {
        Assert.Equal("3", DisplayFormatting.LineupBadgeText(Row(lineup: LineupInfo.BattingOrder(3))));
        Assert.Null(DisplayFormatting.LineupBadgeText(Row(lineup: LineupInfo.InLineup)));
        Assert.Null(DisplayFormatting.LineupBadgeText(Row(lineup: LineupInfo.Unknown)));
    }
}
