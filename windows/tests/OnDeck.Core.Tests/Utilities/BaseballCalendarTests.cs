using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class BaseballCalendarTests
{
    private static FakeTimeProvider At(int year, int month, int day, int hour)
    {
        var provider = new FakeTimeProvider(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
        provider.SetLocalTimeZone(TimeZoneInfo.Utc);
        return provider;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void Today_BeforeEightAmIsYesterday(int hour)
    {
        var today = BaseballCalendar.Today(At(2026, 8, 8, hour));
        Assert.Equal(new DateOnly(2026, 8, 7), DateOnly.FromDateTime(today.Date));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(23)]
    public void Today_FromEightAmIsToday(int hour)
    {
        var today = BaseballCalendar.Today(At(2026, 8, 8, hour));
        Assert.Equal(new DateOnly(2026, 8, 8), DateOnly.FromDateTime(today.Date));
    }

    [Fact]
    public void Today_RollsBackAcrossMonthBoundary()
    {
        var today = BaseballCalendar.Today(At(2026, 8, 1, 2));
        Assert.Equal(new DateOnly(2026, 7, 31), DateOnly.FromDateTime(today.Date));
    }
}
