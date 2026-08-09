using OnDeck.App.Views;

namespace OnDeck.App.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 19, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(45, "45 seconds")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "1 minute")]              // truncates to the largest whole unit
    [InlineData(300, "5 minutes")]
    [InlineData(3599, "59 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(5400, "1 hour")]
    [InlineData(86_399, "23 hours")]
    [InlineData(86_400, "1 day")]
    [InlineData(259_200, "3 days")]
    public void DescribesTheLargestWholeUnit(int secondsAgo, string expected)
    {
        var result = RelativeTime.Describe(Now.AddSeconds(-secondsAgo), Now);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void AFutureStampNeverReadsAsNegative()
    {
        // A clock change between the sync and the render should not produce
        // "Last synced: -30 seconds ago".
        var result = RelativeTime.Describe(Now.AddMinutes(5), Now);

        Assert.Equal("0 seconds", result);
    }

    [Fact]
    public void InstantsAreComparedNotWallClockDigits()
    {
        // RosterManager stamps LastSyncDate in whatever offset the machine is in; the render
        // clock is DateTimeOffset.Now. Subtracting two DateTimeOffsets compares instants, and
        // this test fails loudly if someone "simplifies" that to DateTime.
        var sameInstantElsewhere = Now.ToOffset(TimeSpan.FromHours(-5)).AddMinutes(-2);

        Assert.Equal("2 minutes", RelativeTime.Describe(sameInstantElsewhere, Now));
    }
}
