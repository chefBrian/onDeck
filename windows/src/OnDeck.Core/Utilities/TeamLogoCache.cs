namespace OnDeck.Core.Utilities;

/// <summary>
/// Port of <c>TeamLogoCache</c> from <c>Views/MenuBarView.swift</c>. Logos are fetched on demand
/// for the games on screen and kept on disk; the shell loads them from the returned path.
/// <para>
/// Swift's in-memory <c>NSImage</c> dictionary is not ported: WPF's <c>BitmapImage</c> already
/// caches decoded frames per URI, so a second memory cache would only duplicate it — which also
/// makes Swift's <c>evictMemoryCache</c> unnecessary.
/// </para>
/// </summary>
public sealed class TeamLogoCache(HttpClient http, string cacheDirectory)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string DefaultCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "onDeck",
            "TeamLogos");

    /// <summary>The on-disk path for a logo, or null if it hasn't been fetched.</summary>
    public string? FilePath(int teamId, int size)
    {
        var file = PathFor(teamId, size);
        return File.Exists(file) ? file : null;
    }

    /// <summary>Cached path, fetching it first if needed. Null when the logo can't be had.</summary>
    public async Task<string?> GetAsync(int teamId, int size, CancellationToken ct = default)
    {
        if (FilePath(teamId, size) is { } cached) return cached;

        var url = $"https://midfield.mlbstatic.com/v1/team/{teamId}/spots/{size}";

        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (!IsPng(bytes)) return null;

            Directory.CreateDirectory(cacheDirectory);
            var file = PathFor(teamId, size);
            await File.WriteAllBytesAsync(file, bytes, ct);
            return file;
        }
        catch (Exception)
        {
            // A missing logo is a blank square, not a failure worth surfacing.
            return null;
        }
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length > PngSignature.Length
        && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    private string PathFor(int teamId, int size) =>
        Path.Combine(cacheDirectory, $"{teamId}_{size}.png");
}
