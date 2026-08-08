namespace OnDeck.Core.Utilities;

/// <summary>
/// Port of <c>ScheduleManager.baseballDate()</c>. Lives here rather than on the manager
/// because <c>FantraxApi</c>'s period detection needs it too.
/// </summary>
public static class BaseballCalendar
{
    /// <summary>
    /// The "baseball date" — before 8 AM local, we're still on yesterday's schedule.
    /// </summary>
    public static DateTimeOffset Today(TimeProvider timeProvider)
    {
        var now = timeProvider.GetLocalNow();
        return now.Hour < 8 ? now.AddDays(-1) : now;
    }
}
