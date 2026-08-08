namespace OnDeck.Core;

/// <summary>
/// The notification surface Core calls, implemented by the shell (Phase 9's
/// <c>ToastService</c>). Mirrors <c>Notifications/NotificationManager.swift</c> 1:1 —
/// Core drives it directly from its transition and reconcile logic exactly as
/// <c>AppState</c> does on macOS, so the <c>isStillActive</c> race-guard purges stay
/// inside Core. Implementations check the per-type <see cref="ISettingsStore"/> toggles
/// (as the Mac's <c>NotificationManager</c> does) and no-op when disabled.
/// </summary>
public interface INotificationSink
{
    Task NotifyBattingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl);

    Task NotifyPitchingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl);

    Task NotifyAtBatResultAsync(string playerName, int playerId, string description, Uri? streamUrl);

    Task NotifyPitchingResultAsync(string playerName, int playerId, string description, Uri? streamUrl);

    Task NotifyNotInLineupAsync(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl);

    void PurgeBatting(int gamePk, int playerId);

    void PurgePitching(int gamePk, int playerId);

    /// <summary>Game-scoped: players never in the lineup have no transition to hang this on.</summary>
    Task PurgeNotInLineupAsync(int gamePk);

    /// <summary>Schedule refresh / day rollover.</summary>
    Task PurgeAllAsync();
}
