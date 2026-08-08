namespace OnDeck.Core.Utilities;

/// <summary>
/// Port of <c>Utilities/HeadshotCache.swift</c>. Swift caches <c>NSImage</c>-validated PNGs;
/// this keeps a raw file cache so WPF and toasts can load straight from the path.
/// </summary>
public sealed class HeadshotCache(HttpClient http, string cacheDirectory)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string DefaultCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "onDeck",
            "Headshots");

    /// <summary>Returns the on-disk path for a player's headshot, or null if not cached.</summary>
    public string? FilePath(int playerId)
    {
        var file = PathFor(playerId);
        return File.Exists(file) ? file : null;
    }

    /// <summary>Prefetch headshots for all players, skipping any already on disk.</summary>
    public async Task PrefetchAsync(IReadOnlyList<int> playerIds, CancellationToken ct = default)
    {
        var pending = playerIds.Where(id => !File.Exists(PathFor(id))).ToArray();
        if (pending.Length == 0) return;

        await Task.WhenAll(pending.Select(id => DownloadAsync(id, ct)));
    }

    private async Task DownloadAsync(int playerId, CancellationToken ct)
    {
        var url = "https://img.mlbstatic.com/mlb-photos/image/upload/"
                  + $"d_people:generic:headshot:67:current.png/w_128/q_auto:best/v1/people/{playerId}/headshot/67/current";

        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (!IsPng(bytes)) return;

            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllBytesAsync(PathFor(playerId), bytes, ct);
        }
        catch (Exception)
        {
            // Silently skip - the notification will just have no image.
        }
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length > PngSignature.Length
        && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    private string PathFor(int playerId) => Path.Combine(cacheDirectory, $"{playerId}.png");
}
