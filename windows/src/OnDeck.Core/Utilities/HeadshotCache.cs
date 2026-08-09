namespace OnDeck.Core.Utilities;

/// <summary>
/// Port of <c>Utilities/HeadshotCache.swift</c>. Swift caches <c>NSImage</c>-validated PNGs;
/// this keeps a raw file cache so WPF and toasts can load straight from the path.
/// </summary>
public sealed class HeadshotCache(HttpClient http, string cacheDirectory)
{
    /// <summary>
    /// The formats <c>img.mlbstatic.com</c> serves. It answers the headshot URL with a **JPEG**
    /// despite the <c>.png</c> in the path — that segment is the
    /// <c>d_people:generic:headshot:67:current.png</c> *default image* parameter, not an output
    /// format. Swift validates with <c>NSImage(data:) != nil</c>, which accepts any of these;
    /// checking the PNG signature alone discarded every headshot the endpoint returned.
    /// </summary>
    private static readonly byte[][] ImageSignatures =
    [
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],   // PNG
        [0xFF, 0xD8, 0xFF],                                 // JPEG
        [0x47, 0x49, 0x46, 0x38],                           // GIF87a / GIF89a
    ];

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
            if (!IsImage(bytes)) return;

            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllBytesAsync(PathFor(playerId), bytes, ct);
        }
        catch (Exception)
        {
            // Silently skip - the notification will just have no image.
        }
    }

    /// <summary>
    /// The dependency-free stand-in for Swift's <c>NSImage(data:)</c> decode. It still has a job:
    /// MLB answers an unknown player with an HTML error page, and caching that would leave a
    /// permanently broken image on disk.
    /// </summary>
    private static bool IsImage(byte[] bytes) =>
        ImageSignatures.Any(signature =>
            bytes.Length > signature.Length
            && bytes.AsSpan(0, signature.Length).SequenceEqual(signature));

    /// <summary>
    /// Always <c>.png</c>, whatever the bytes turn out to be — Swift writes the same filename
    /// (<c>HeadshotCache.swift:37</c>), and every consumer (WPF's image loader, the toast
    /// renderer) decodes by content rather than extension.
    /// </summary>
    private string PathFor(int playerId) => Path.Combine(cacheDirectory, $"{playerId}.png");
}
