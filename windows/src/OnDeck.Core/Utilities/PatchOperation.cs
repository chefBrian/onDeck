using System.Text.Json;

namespace OnDeck.Core.Utilities;

/// <summary>
/// One RFC 6902 operation from MLB's <c>/feed/live/diffPatch</c> response. Swift passes these
/// around as untyped <c>[String: Any]</c> and guards <c>op</c>/<c>path</c> inside
/// <c>LiveFeedPatcher.apply</c>; here the guard lives in <see cref="TryParse"/>.
/// </summary>
public sealed record PatchOperation(string Op, string Path, JsonElement? Value, string? From)
{
    public static PatchOperation? TryParse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (!element.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String) return null;
        if (!element.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) return null;

        // Clone detaches the value from its JsonDocument: callers routinely dispose the
        // document (see MlbStatsApi.FetchDiffPatchAsync) while the ops outlive it.
        var value = element.TryGetProperty("value", out var rawValue) ? rawValue.Clone() : (JsonElement?)null;
        var from = element.TryGetProperty("from", out var rawFrom) && rawFrom.ValueKind == JsonValueKind.String
            ? rawFrom.GetString()
            : null;

        return new PatchOperation(op.GetString()!, path.GetString()!, value, from);
    }

    /// <summary>Parses an array of ops, skipping any entry missing <c>op</c> or <c>path</c>.</summary>
    public static IReadOnlyList<PatchOperation> ParseArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) return [];

        var ops = new List<PatchOperation>();
        foreach (var element in array.EnumerateArray())
        {
            if (TryParse(element) is { } op) ops.Add(op);
        }

        return ops;
    }
}
