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

            // --- linescore / offense - scalar runner IDs
            case ("replace", "/liveData/linescore/offense/first/id"):
            case ("add", "/liveData/linescore/offense/first/id"):
                feed.RunnerOnFirst = IntValue(value);
                return;
            case ("replace", "/liveData/linescore/offense/second/id"):
            case ("add", "/liveData/linescore/offense/second/id"):
                feed.RunnerOnSecond = IntValue(value);
                return;
            case ("replace", "/liveData/linescore/offense/third/id"):
            case ("add", "/liveData/linescore/offense/third/id"):
                feed.RunnerOnThird = IntValue(value);
                return;
            case ("remove", "/liveData/linescore/offense/first"):
            case ("remove", "/liveData/linescore/offense/first/id"):
                feed.RunnerOnFirst = null;
                return;
            case ("remove", "/liveData/linescore/offense/second"):
            case ("remove", "/liveData/linescore/offense/second/id"):
                feed.RunnerOnSecond = null;
                return;
            case ("remove", "/liveData/linescore/offense/third"):
            case ("remove", "/liveData/linescore/offense/third/id"):
                feed.RunnerOnThird = null;
                return;

            // --- linescore / offense - whole-object add when a runner advances into an empty slot
            // Server sends: {"op":"add","path":"/liveData/linescore/offense/second",
            //                "value":{"id":N,"fullName":"...","link":"..."}}
            case ("add", "/liveData/linescore/offense/first"):
                feed.RunnerOnFirst = RunnerIdFromObject(value);
                return;
            case ("add", "/liveData/linescore/offense/second"):
                feed.RunnerOnSecond = RunnerIdFromObject(value);
                return;
            case ("add", "/liveData/linescore/offense/third"):
                feed.RunnerOnThird = RunnerIdFromObject(value);
                return;

            // --- linescore / offense - batter-reaches-base copy ops
            // Server sends: {"op":"copy","path":"/liveData/linescore/offense/first",
            //                "from":"/liveData/plays/allPlays/N/matchup/batter"}
            // At this point in the patch batch, CurrentBatterId is still the batter who just
            // reached base (the replace for the NEXT batter arrives in a later entry).
            // Verified against real MLB diffPatch output on 2026-04-18.
            case ("copy", "/liveData/linescore/offense/first"):
                if (IsBatterFromPath(op.From)) feed.RunnerOnFirst = feed.CurrentBatterId;
                else feed.TimeStamp = null;
                return;
            case ("copy", "/liveData/linescore/offense/second"):
                if (IsBatterFromPath(op.From)) feed.RunnerOnSecond = feed.CurrentBatterId;
                else feed.TimeStamp = null;
                return;
            case ("copy", "/liveData/linescore/offense/third"):
                if (IsBatterFromPath(op.From)) feed.RunnerOnThird = feed.CurrentBatterId;
                else feed.TimeStamp = null;
                return;

            // --- linescore / offense - decorative sub-paths on base slots (runner name/link).
            // Explicit no-op cases rather than entries in the decorative table below, because
            // /liveData/linescore/offense/first itself IS modeled (id + the copy/add handlers
            // above) — only fullName and link under those slots get silenced.
            case ("replace", "/liveData/linescore/offense/first/fullName"):
            case ("add", "/liveData/linescore/offense/first/fullName"):
            case ("replace", "/liveData/linescore/offense/first/link"):
            case ("add", "/liveData/linescore/offense/first/link"):
            case ("replace", "/liveData/linescore/offense/second/fullName"):
            case ("add", "/liveData/linescore/offense/second/fullName"):
            case ("replace", "/liveData/linescore/offense/second/link"):
            case ("add", "/liveData/linescore/offense/second/link"):
            case ("replace", "/liveData/linescore/offense/third/fullName"):
            case ("add", "/liveData/linescore/offense/third/fullName"):
            case ("replace", "/liveData/linescore/offense/third/link"):
            case ("add", "/liveData/linescore/offense/third/link"):
                return;

            // --- linescore / offense - typed runner advance (move ops).
            // A move whose `from` matches neither slot falls through to the handlers below.
            case ("move", "/liveData/linescore/offense/second"):
                if (op.From == "/liveData/linescore/offense/first")
                {
                    feed.RunnerOnSecond = feed.RunnerOnFirst;
                    feed.RunnerOnFirst = null;
                    return;
                }
                break;
            case ("move", "/liveData/linescore/offense/third"):
                if (op.From == "/liveData/linescore/offense/first")
                {
                    feed.RunnerOnThird = feed.RunnerOnFirst;
                    feed.RunnerOnFirst = null;
                    return;
                }
                if (op.From == "/liveData/linescore/offense/second")
                {
                    feed.RunnerOnThird = feed.RunnerOnSecond;
                    feed.RunnerOnSecond = null;
                    return;
                }
                break;
        }

        // Prefix-dispatched handlers (lineup arrays, player boxscore)
        if (TryApplyBoxscoreArrayPatch(op, feed)) return;

        // Task 7 inserts the player-stats handler here.
        // Task 8 inserts the decorative-prefix check and the unknown fallthrough here.
    }

    // MARK: - Boxscore array patches (batting orders, pitcher lists)

    private static bool TryApplyBoxscoreArrayPatch(PatchOperation op, LiveFeedData feed)
    {
        (string Side, string Key, Func<LiveFeedData, List<int>> Select)[] targets =
        [
            ("home", "battingOrder", f => f.HomeBattingOrder),
            ("away", "battingOrder", f => f.AwayBattingOrder),
            ("home", "pitchers", f => f.HomePitchers),
            ("away", "pitchers", f => f.AwayPitchers),
        ];

        foreach (var (side, key, select) in targets)
        {
            var basePath = $"/liveData/boxscore/teams/{side}/{key}";

            if (op.Path == basePath)
            {
                if (op.Value is { ValueKind: JsonValueKind.Array } array)
                {
                    var list = select(feed);
                    list.Clear();
                    foreach (var element in array.EnumerateArray())
                    {
                        if (IntValue(element) is { } n) list.Add(n);
                    }
                }
                return true;
            }

            if (!op.Path.StartsWith(basePath + "/", StringComparison.Ordinal)) continue;

            var suffix = op.Path[(basePath.Length + 1)..];

            // An unparseable append is not a missing handler, so "-" always counts as handled.
            if (suffix == "-")
            {
                if (op.Op == "add" && IntValue(op.Value) is { } appended) select(feed).Add(appended);
                return true;
            }

            if (!int.TryParse(suffix, out var index)) continue;

            var target = select(feed);
            switch (op.Op)
            {
                case "replace":
                    if (index < target.Count && IntValue(op.Value) is { } replacement) target[index] = replacement;
                    return true;
                case "add":
                    if (IntValue(op.Value) is { } inserted)
                    {
                        if (index <= target.Count) target.Insert(index, inserted);
                        else target.Add(inserted);
                    }
                    return true;
                case "remove":
                    if (index < target.Count) target.RemoveAt(index);
                    return true;
                default:
                    return false;
            }
        }

        return false;
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

    /// <summary>
    /// Extracts an <c>id</c> field from a JSON object value. Used by the whole-object
    /// <c>add</c> handlers for <c>/liveData/linescore/offense/{first|second|third}</c>,
    /// whose value shape is <c>{"id": N, "fullName": "...", "link": "..."}</c>.
    /// </summary>
    private static int? RunnerIdFromObject(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty("id", out var id)
            ? IntValue(id)
            : null;

    /// <summary>
    /// True if <paramref name="from"/> points at a batter field — either
    /// <c>/plays/currentPlay/matchup/batter</c> or <c>/plays/allPlays/N/matchup/batter</c>.
    /// Guards the copy-op shortcut that resolves "batter reaches base" from CurrentBatterId.
    /// </summary>
    private static bool IsBatterFromPath(string? from) =>
        from is not null
        && from.EndsWith("/matchup/batter", StringComparison.Ordinal)
        && from.StartsWith("/liveData/plays/", StringComparison.Ordinal);
}
