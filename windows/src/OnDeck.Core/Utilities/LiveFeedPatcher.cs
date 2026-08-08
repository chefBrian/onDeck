using System.Text.Json;
using OnDeck.Core.Models;

namespace OnDeck.Core.Utilities;

/// <summary>
/// Applies RFC 6902 JSON patches produced by MLB's <c>/feed/live/diffPatch</c> endpoint
/// directly to a <see cref="LiveFeedData"/> — no JSON object graph, no reserialize.
/// Port of <c>Utilities/LiveFeedPatcher.swift</c>.
///
/// Two-tier dispatch: registered (op, path) pairs mutate typed fields; any other op is
/// recorded via <see cref="UnknownPatchLogger"/> and silently skipped. Decorative paths
/// under <c>/currentPlay</c> outnumber modeled ones ~30:1, so reseed-on-unknown-under-
/// relevant-prefix would fire every cycle.
/// </summary>
public static class LiveFeedPatcher
{
    /// <summary>
    /// Applies <paramref name="ops"/> to a working copy of <paramref name="feed"/> and returns it.
    /// Partial state never escapes — callers get either the fully patched feed or the original
    /// on any handler-internal error (there are none currently). Pass <paramref name="logger"/>
    /// to capture unhandled ops; when it is <c>null</c> the decorative-prefix scan is skipped
    /// too, since nothing would consume the result.
    /// </summary>
    public static LiveFeedData Apply(
        IReadOnlyList<PatchOperation> ops, LiveFeedData feed, UnknownPatchLogger? logger = null)
    {
        var working = feed.Clone();
        foreach (var op in ops) ApplyOne(op, working, logger);
        return working;
    }

    private static void ApplyOne(PatchOperation op, LiveFeedData feed, UnknownPatchLogger? logger)
    {
        var value = op.Value;

        // Registered scalar leaves (replace-only unless noted)
        switch (op.Op, op.Path)
        {
            // --- metaData
            case ("replace", "/metaData/timeStamp"):
            case ("add", "/metaData/timeStamp"):
                feed.TimeStamp = StringValue(value);
                return;

            // --- gameData/status
            case ("replace", "/gameData/status/abstractGameState"):
                if (StringValue(value) is { } gameState) feed.GameState = gameState;
                return;
            case ("replace", "/gameData/status/detailedState"):
            case ("add", "/gameData/status/detailedState"):
                feed.DetailedState = StringValue(value);
                return;
            case ("remove", "/gameData/status/detailedState"):
                feed.DetailedState = null;
                return;

            // --- gameData/teams (rare - name/id shouldn't change mid-game, but register defensively)
            case ("replace", "/gameData/teams/home/name"):
                if (StringValue(value) is { } homeName) feed.HomeTeam = homeName;
                return;
            case ("replace", "/gameData/teams/home/id"):
                if (IntValue(value) is { } homeId) feed.HomeTeamId = homeId;
                return;
            case ("replace", "/gameData/teams/away/name"):
                if (StringValue(value) is { } awayName) feed.AwayTeam = awayName;
                return;
            case ("replace", "/gameData/teams/away/id"):
                if (IntValue(value) is { } awayId) feed.AwayTeamId = awayId;
                return;

            // --- currentPlay / matchup
            case ("replace", "/liveData/plays/currentPlay/matchup/batter/id"):
            case ("add", "/liveData/plays/currentPlay/matchup/batter/id"):
                feed.CurrentBatterId = IntValue(value);
                return;
            case ("replace", "/liveData/plays/currentPlay/matchup/batter/fullName"):
            case ("add", "/liveData/plays/currentPlay/matchup/batter/fullName"):
                feed.CurrentBatterName = StringValue(value);
                return;
            case ("replace", "/liveData/plays/currentPlay/matchup/pitcher/id"):
            case ("add", "/liveData/plays/currentPlay/matchup/pitcher/id"):
                feed.CurrentPitcherId = IntValue(value);
                return;
            case ("replace", "/liveData/plays/currentPlay/matchup/pitcher/fullName"):
            case ("add", "/liveData/plays/currentPlay/matchup/pitcher/fullName"):
                feed.CurrentPitcherName = StringValue(value);
                return;

            // --- currentPlay / about
            case ("replace", "/liveData/plays/currentPlay/about/isComplete"):
            case ("add", "/liveData/plays/currentPlay/about/isComplete"):
                feed.IsPlayComplete = BoolValue(value) ?? feed.IsPlayComplete;
                return;

            // --- currentPlay / result
            case ("replace", "/liveData/plays/currentPlay/result/event"):
            case ("add", "/liveData/plays/currentPlay/result/event"):
                feed.LastPlayEvent = StringValue(value);
                return;
            case ("remove", "/liveData/plays/currentPlay/result/event"):
                feed.LastPlayEvent = null;
                return;
            case ("replace", "/liveData/plays/currentPlay/result/description"):
            case ("add", "/liveData/plays/currentPlay/result/description"):
                feed.LastPlayDescription = StringValue(value);
                return;
            case ("remove", "/liveData/plays/currentPlay/result/description"):
                feed.LastPlayDescription = null;
                return;

            // --- currentPlay / count
            case ("replace", "/liveData/plays/currentPlay/count/balls"):
            case ("add", "/liveData/plays/currentPlay/count/balls"):
                feed.Balls = IntValue(value) ?? feed.Balls;
                return;
            case ("replace", "/liveData/plays/currentPlay/count/strikes"):
            case ("add", "/liveData/plays/currentPlay/count/strikes"):
                feed.Strikes = IntValue(value) ?? feed.Strikes;
                return;
            case ("replace", "/liveData/plays/currentPlay/count/outs"):
            case ("add", "/liveData/plays/currentPlay/count/outs"):
                feed.Outs = IntValue(value) ?? feed.Outs;
                return;

            // --- linescore
            case ("replace", "/liveData/linescore/currentInning"):
            case ("add", "/liveData/linescore/currentInning"):
                feed.Inning = IntValue(value);
                return;
            case ("replace", "/liveData/linescore/inningHalf"):
            case ("add", "/liveData/linescore/inningHalf"):
                feed.InningHalf = StringValue(value);
                return;
            case ("replace", "/liveData/linescore/inningState"):
            case ("add", "/liveData/linescore/inningState"):
                feed.InningState = StringValue(value);
                return;
            case ("replace", "/liveData/linescore/teams/home/runs"):
            case ("add", "/liveData/linescore/teams/home/runs"):
                feed.HomeScore = IntValue(value) ?? feed.HomeScore;
                return;
            case ("replace", "/liveData/linescore/teams/away/runs"):
            case ("add", "/liveData/linescore/teams/away/runs"):
                feed.AwayScore = IntValue(value) ?? feed.AwayScore;
                return;

            // --- linescore mirrors of currentPlay count (MLB emits both; linescore fires more often)
            case ("replace", "/liveData/linescore/balls"):
            case ("add", "/liveData/linescore/balls"):
                feed.Balls = IntValue(value) ?? feed.Balls;
                return;
            case ("replace", "/liveData/linescore/strikes"):
            case ("add", "/liveData/linescore/strikes"):
                feed.Strikes = IntValue(value) ?? feed.Strikes;
                return;
            case ("replace", "/liveData/linescore/outs"):
            case ("add", "/liveData/linescore/outs"):
                feed.Outs = IntValue(value) ?? feed.Outs;
                return;
        }

        // Task 5 inserts the offense/runner cases into the switch above.
        // Task 6 and 7 insert the prefix-dispatched handlers here.
        // Task 8 inserts the decorative-prefix check and the unknown fallthrough here.
    }

    // MARK: - Helpers

    internal static int? IntValue(JsonElement? value)
    {
        if (value is not { } element) return null;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var n)
                ? n
                : element.TryGetDouble(out var d) ? (int)d : null,
            JsonValueKind.String => int.TryParse(element.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    internal static string? StringValue(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

    internal static bool? BoolValue(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
