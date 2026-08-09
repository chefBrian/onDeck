namespace OnDeck.App.Views;

/// <summary>
/// The <c>"5 minutes"</c> half of Swift's <c>Text(date, style: .relative)</c>
/// (<c>Views/SettingsView.swift:55</c>) — the caller supplies the surrounding
/// <c>"Last synced: … ago"</c>. Largest whole unit, singular at one.
/// </summary>
public static class RelativeTime
{
    public static string Describe(DateTimeOffset date, DateTimeOffset now)
    {
        var elapsed = now - date;

        // A clock adjustment between the sync and this render can put the stamp in the future;
        // "-30 seconds ago" is worse than "0 seconds ago".
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (elapsed.TotalSeconds < 60) return Quantity((int)elapsed.TotalSeconds, "second");
        if (elapsed.TotalMinutes < 60) return Quantity((int)elapsed.TotalMinutes, "minute");
        if (elapsed.TotalHours < 24) return Quantity((int)elapsed.TotalHours, "hour");
        return Quantity((int)elapsed.TotalDays, "day");
    }

    private static string Quantity(int count, string unit) =>
        count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
