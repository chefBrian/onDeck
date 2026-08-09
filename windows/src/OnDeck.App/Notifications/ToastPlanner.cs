using OnDeck.Core;

namespace OnDeck.App.Notifications;

/// <summary>
/// One toast, as plain data. Everything the user reads and everything the purge path matches on,
/// resolved before anything Windows-specific is touched.
/// </summary>
public sealed record ToastPlan
{
    public required string Title { get; init; }

    public required string Body { get; init; }

    /// <summary>The stable identifier a purge matches. Null for results, which have none.</summary>
    public string? Tag { get; init; }

    /// <summary>Set only for not-in-lineup, whose purge is game-scoped.</summary>
    public string? Group { get; init; }

    public Uri? ClickUrl { get; init; }

    /// <summary>Drives the headshot lookup; the toast shows no image when it isn't cached.</summary>
    public int PlayerId { get; init; }

    /// <summary>Swift's <c>autoDismissAfter</c>. Null means the toast sits until dismissed.</summary>
    public TimeSpan? Expiry { get; init; }
}

/// <summary>
/// The stable notification identifiers, byte-for-byte as CLAUDE.md documents them and
/// <c>NotificationManager.swift:129-143</c> builds them. Core's purge calls match on these, so a
/// drift here shows up only as a live toast that won't clear.
/// </summary>
public static class ToastIds
{
    public static string Batting(int gamePk, int playerId) => $"batting-{gamePk}-{playerId}";

    public static string Pitching(int gamePk, int playerId) => $"pitching-{gamePk}-{playerId}";

    public static string NotInLineup(int gamePk, int playerId) =>
        $"notInLineup-{gamePk}-{playerId}";

    /// <summary>
    /// The group every not-in-lineup toast for a game shares. macOS sweeps delivered ids by
    /// prefix; <c>History.Remove</c> is exact-match, so Windows needs a real group to sweep.
    /// </summary>
    public static string NotInLineupGroup(int gamePk) => $"notInLineup-{gamePk}";
}

/// <summary>
/// Turns a Core notification call into a <see cref="ToastPlan"/>, or into <c>null</c> when that
/// type's toggle is off — the port of the <c>guard UserDefaults…</c> line that opens each of
/// <c>NotificationManager.swift:147-199</c>. Core calls the sink unconditionally; this is where
/// the user's preference is applied.
/// </summary>
public sealed class ToastPlanner(ISettingsStore settings)
{
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromSeconds(30);

    public ToastPlan? Batting(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        !settings.NotifyBatting
            ? null
            : new ToastPlan
            {
                Title = $"{playerName} is batting",
                Body = $"{game}, {inning}",
                Tag = ToastIds.Batting(gamePk, playerId),
                ClickUrl = streamUrl,
                PlayerId = playerId,
            };

    public ToastPlan? Pitching(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        !settings.NotifyPitching
            ? null
            : new ToastPlan
            {
                Title = $"{playerName} is taking the mound",
                Body = $"{game}, {inning}",
                Tag = ToastIds.Pitching(gamePk, playerId),
                ClickUrl = streamUrl,
                PlayerId = playerId,
            };

    public ToastPlan? AtBatResult(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        !settings.NotifyAtBatResult
            ? null
            : Result(playerName, playerId, description, streamUrl);

    public ToastPlan? PitchingResult(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        !settings.NotifyPitchingResult
            ? null
            : Result(playerName, playerId, description, streamUrl);

    public ToastPlan? NotInLineup(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl) =>
        !settings.NotifyNotInLineup
            ? null
            : new ToastPlan
            {
                Title = $"{playerName} is not in the lineup",
                Body = game,
                Tag = ToastIds.NotInLineup(gamePk, playerId),
                Group = ToastIds.NotInLineupGroup(gamePk),
                ClickUrl = fantraxUrl,
                PlayerId = playerId,
            };

    /// <summary>
    /// Both result types are identical but for the toggle gating them — Swift passes no
    /// identifier, so consecutive results stack instead of replacing each other.
    /// </summary>
    private static ToastPlan Result(
        string playerName, int playerId, string description, Uri? streamUrl) => new()
    {
        Title = playerName,
        Body = description,
        ClickUrl = streamUrl,
        PlayerId = playerId,
        Expiry = ResultLifetime,
    };
}
