using System.Diagnostics;
using OnDeck.Core;

namespace OnDeck.App.Notifications;

/// <summary>
/// Stands in for Phase 9's <c>ToastService</c> so the engine can run end to end now. Every call
/// is logged; nothing is shown.
/// </summary>
public sealed class LoggingNotificationSink : INotificationSink
{
    public Task NotifyBattingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        Log($"BATTING {playerName} - {game}, {inning} ({streamUrl})");

    public Task NotifyPitchingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        Log($"PITCHING {playerName} - {game}, {inning} ({streamUrl})");

    public Task NotifyAtBatResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        Log($"AT-BAT RESULT {playerName} - {description}");

    public Task NotifyPitchingResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        Log($"PITCHING RESULT {playerName} - {description}");

    public Task NotifyNotInLineupAsync(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl) =>
        Log($"NOT IN LINEUP {playerName} - {game}");

    public void PurgeBatting(int gamePk, int playerId) =>
        Debug.WriteLine($"[Notifications] purge batting {playerId} in {gamePk}");

    public void PurgePitching(int gamePk, int playerId) =>
        Debug.WriteLine($"[Notifications] purge pitching {playerId} in {gamePk}");

    public Task PurgeNotInLineupAsync(int gamePk) => Log($"purge not-in-lineup for {gamePk}");

    public Task PurgeAllAsync() => Log("purge all");

    private static Task Log(string message)
    {
        Debug.WriteLine($"[Notifications] {message}");
        return Task.CompletedTask;
    }
}
