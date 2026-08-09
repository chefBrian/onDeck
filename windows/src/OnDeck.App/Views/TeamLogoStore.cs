using OnDeck.Core.Utilities;

namespace OnDeck.App.Views;

/// <summary>
/// The shell's view of <see cref="TeamLogoCache"/>: a synchronous path lookup for row building,
/// plus a background fetch for anything missing.
/// <para>
/// Rows are rebuilt wholesale on every <c>StateChanged</c> — every 10 s during a live game — so
/// the lookup has to be synchronous and the fetch has to de-duplicate. Without the in-flight set,
/// a missing logo would be re-requested on every rebuild for as long as it stayed missing.
/// </para>
/// </summary>
public sealed class TeamLogoStore(TeamLogoCache cache, int size = 32)
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _inFlight = [];
    private readonly List<Task> _pending = [];

    /// <summary>Raised once a new logo is on disk, on whichever thread the fetch completed on.</summary>
    public event Action? Changed;

    /// <summary>The cached file for a team, or null if it isn't there yet.</summary>
    public string? PathFor(int teamId) => cache.FilePath(teamId, size);

    /// <summary>Starts fetching any of these logos that aren't cached or already being fetched.</summary>
    public void Prefetch(IEnumerable<int> teamIds)
    {
        foreach (var teamId in teamIds)
        {
            if (teamId <= 0) continue;
            if (PathFor(teamId) is not null) continue;

            // In the app every call is on the Dispatcher, but the fetch continuations are not
            // guaranteed to be, so the two collections are guarded rather than assumed serial.
            lock (_gate)
            {
                if (!_inFlight.Add(teamId)) continue;
                _pending.Add(FetchAsync(teamId));
            }
        }
    }

    /// <summary>Awaits the in-flight fetches. Test seam — the app never needs to wait.</summary>
    internal async Task DrainAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = [.. _pending];
                _pending.Clear();
            }

            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    private async Task FetchAsync(int teamId)
    {
        var path = await cache.GetAsync(teamId, size);

        lock (_gate)
        {
            _inFlight.Remove(teamId);
        }

        // A missing logo is a blank square, not something to redraw for.
        if (path is not null) Changed?.Invoke();
    }
}
