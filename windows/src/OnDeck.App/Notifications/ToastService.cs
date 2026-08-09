using OnDeck.Core;
using OnDeck.Core.Utilities;

namespace OnDeck.App.Notifications;

/// <summary>
/// The Windows implementation of <see cref="INotificationSink"/> — the port of
/// <c>Notifications/NotificationManager.swift</c>. Core calls every method unconditionally and
/// owns the race-guard purges; this decides what a toast says, whether the user wants it, and
/// which identifier it carries.
/// <para>
/// <c>requestPermission</c> has no counterpart: Windows has no per-app notification authorisation
/// to ask for, so there is no <c>authorizationStatus</c> to gate sends on.
/// </para>
/// </summary>
public sealed class ToastService(
    ISettingsStore settings, HeadshotCache headshots, IToastPresenter presenter)
    : INotificationSink
{
    private readonly ToastPlanner _planner = new(settings);

    public Task NotifyBattingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        Present(_planner.Batting(playerName, playerId, gamePk, game, inning, streamUrl));

    public Task NotifyPitchingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        Present(_planner.Pitching(playerName, playerId, gamePk, game, inning, streamUrl));

    public Task NotifyAtBatResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        Present(_planner.AtBatResult(playerName, playerId, description, streamUrl));

    public Task NotifyPitchingResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        Present(_planner.PitchingResult(playerName, playerId, description, streamUrl));

    public Task NotifyNotInLineupAsync(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl) =>
        Present(_planner.NotInLineup(playerName, playerId, gamePk, game, fantraxUrl));

    // Purges are deliberately ungated: a toast shown before the user turned its type off must
    // still be removable. Swift doesn't gate them either.
    public void PurgeBatting(int gamePk, int playerId) =>
        presenter.Remove(ToastIds.Batting(gamePk, playerId));

    public void PurgePitching(int gamePk, int playerId) =>
        presenter.Remove(ToastIds.Pitching(gamePk, playerId));

    public Task PurgeNotInLineupAsync(int gamePk)
    {
        presenter.RemoveGroup(ToastIds.NotInLineupGroup(gamePk));
        return Task.CompletedTask;
    }

    public Task PurgeAllAsync()
    {
        presenter.Clear();
        return Task.CompletedTask;
    }

    /// <summary>A null plan means the user has that notification type switched off.</summary>
    private Task Present(ToastPlan? plan)
    {
        if (plan is null) return Task.CompletedTask;

        presenter.Show(plan, headshots.FilePath(plan.PlayerId));
        return Task.CompletedTask;
    }
}
