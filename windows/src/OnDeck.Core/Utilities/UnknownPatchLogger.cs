using System.Diagnostics;
using System.Text.Json;

namespace OnDeck.Core.Utilities;

/// <summary>
/// Record of RFC 6902 ops the typed patcher has no handler for. Port of
/// <c>Utilities/UnknownPatchLogger.swift</c>, with the CSV file and 10 MB rotation replaced
/// by in-memory entries plus <see cref="Debug"/> output (PORT_PLAN: log target becomes
/// <c>ILogger</c>/Debug).
///
/// Per-key sampling: each unique <c>(op, path)</c> pair is retained up to
/// <see cref="MaxPerKey"/> times, after which occurrences are only counted. This keeps the
/// logger from allocating ~500 rows/min on decorative paths while still surfacing new
/// handlers to register.
/// </summary>
public sealed class UnknownPatchLogger
{
    public const int MaxPerKey = 3;
    private const int PreviewLength = 120;

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, int> _counts = [];

    public IReadOnlyList<Entry> Entries => _entries;

    public IReadOnlyDictionary<string, int> Counts => _counts;

    public void Record(string op, string path, string? from, JsonElement? value)
    {
        var key = $"{op}|{path}";
        var count = _counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
        _counts[key] = count;

        if (count > MaxPerKey) return;

        Debug.WriteLine($"[LiveFeedPatcher] unknown: {op} {path}{(from is null ? "" : $" from={from}")}");
        _entries.Add(new Entry(op, path, from, PreviewValue(value)));
    }

    private static string PreviewValue(JsonElement? value)
    {
        if (value is not { } element) return "";

        var rendered = element.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.String => $"\"{element.GetString()}\"",
            _ => element.GetRawText(),
        };

        return rendered.Length <= PreviewLength ? rendered : rendered[..PreviewLength];
    }

    public sealed record Entry(string Op, string Path, string? From, string ValuePreview);
}
