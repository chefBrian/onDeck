# Phase 4: Managers — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the four managers — `HeadshotCache`, `StateManager`, `RosterManager`, `ScheduleManager`, and the `GameMonitor` polling state machine — completing `OnDeck.Core` except for `AppOrchestrator`.

**Architecture:** Everything runs on one logical thread, mirroring Swift's `@MainActor`. `GameMonitor`'s coordinator is a `Task` loop driven by an injected `TimeProvider`, so tests advance the clock instead of sleeping. Swift's `withTaskGroup` fan-outs become `Task.WhenAll`. No `ConfigureAwait(false)` anywhere.

**Tech Stack:** .NET 10, `TimeProvider` + `FakeTimeProvider`, `HttpClient` with the Phase 3 stub handler, xunit.

## Global Constraints

- `OnDeck.Core` must have **zero** Windows-specific dependencies and zero package references.
- **No `ConfigureAwait(false)` anywhere in Core.** The race guards depend on continuations returning to the captured context.
- Single-file publish must stay green.
- Mirror Swift names 1:1 where possible.
- macOS-only `MemoryPressureRelief.releaseReclaimablePages()` calls are **not ported** — drop them, don't stub them.
- Commands run from `windows/`.

## Load-bearing behaviours (from CLAUDE.md and the Swift source)

These are the things that break silently. Each has a dedicated test.

1. **Seed-after-`StartMonitoring`.** `StartMonitoring` calls `StopMonitoring` internally, which clears all state — so seed data (`LineupPlayerIds`) must be set *after* calling it, never before.
2. **Pre-game milestones are one-shots.** 2h/1h/30m before start, tracked per game in `CompletedMilestones`, and only while the game is still >15 min out.
3. **`StopMonitoring(gamePk)` retains `LatestFeeds[gamePk]`** so the Done section can keep reading stats for finished games. Full `StopMonitoring()` drops them.
4. **Postponed games keep `.Upcoming`.** A postponed game reports `GameState == "Final"` with no stats; marking players `GameOver` would filter them out of the UI entirely (Done requires a stat line). Stop polling but leave state alone.
5. **The `isPlayable` allowlist.** `GameState == "Live"` alone is not enough — it also covers "Warmup" and briefly "Game Over". Only `In Progress`, `Delayed*`, `Suspended*`, and `Manager challenge` count. Pre-game "Delayed Start: Rain" carries `GameState == "Preview"`, so the `Delayed` prefix here only matches the mid-game form — **do not tighten it** or rain-delay detection breaks.
6. **Empty lineup sides never overwrite.** An empty batting side means that team hasn't filed yet, not that we should drop what we had. Pitchers live in separate sets so a filed batting card can't flag the probable starter as missing.
7. **Transient poll errors null `TimeStamp`**, preserving the rest of the feed for UI continuity while forcing a full fetch next cycle.
8. **Roster dedupe merges two-way players** (Ohtani) by MLB ID, unioning positions and keeping the *most active* status (lowest `statusId`).

## File Structure

| File | Responsibility |
|---|---|
| `src/OnDeck.Core/ISettingsStore.cs` | Persisted-settings surface Core reads (implemented by the shell) |
| `src/OnDeck.Core/Utilities/HeadshotCache.cs` | On-disk headshot cache + prefetch |
| `src/OnDeck.Core/Managers/StateManager.cs` | Player state map + change callback |
| `src/OnDeck.Core/Managers/RosterManager.cs` | Fantrax → MLB ID resolution, dedupe, cache round-trip |
| `src/OnDeck.Core/Managers/ScheduleManager.cs` | Today's games filtered to roster teams |
| `src/OnDeck.Core/Managers/GameMonitor.cs` | Polling coordinator + feed processing |
| `tests/OnDeck.Core.Tests/Managers/*.cs`, `Utilities/HeadshotCacheTests.cs` | One test file per manager; `GameMonitor` gets four |

---

## Task 1: HeadshotCache

**Files:**
- Create: `src/OnDeck.Core/Utilities/HeadshotCache.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/HeadshotCacheTests.cs`

**Spec:** `Utilities/HeadshotCache.swift`

**Interfaces:**
- Produces: `sealed class HeadshotCache(HttpClient http, string cacheDirectory)` with
  `static string DefaultCacheDirectory()` (→ `{LocalApplicationData}/onDeck/Headshots`),
  `string? FilePath(int playerId)` — path when the file exists, else `null`,
  `Task PrefetchAsync(IReadOnlyList<int> playerIds, CancellationToken ct = default)`.

**Behaviour:** files are `{cacheDirectory}/{playerId}.png`. Prefetch skips IDs already on disk, downloads the rest concurrently from
`https://img.mlbstatic.com/mlb-photos/image/upload/d_people:generic:headshot:67:current.png/w_128/q_auto:best/v1/people/{id}/headshot/67/current`,
and writes only when the response succeeds and the body is a non-empty PNG (starts with the 8-byte PNG signature). Swift validates by constructing an `NSImage`; the signature check is the portable equivalent. Any failure is swallowed — a missing headshot just means no image on the toast.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Utilities/HeadshotCacheTests.cs`:

```csharp
using System.Net;
using OnDeck.Core.Tests.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class HeadshotCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ondeck-headshot-tests", Guid.NewGuid().ToString("N"));

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task PrefetchAsync_WritesDownloadedHeadshots()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([660271]);

        var path = cache.FilePath(660271);
        Assert.NotNull(path);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task PrefetchAsync_RequestsTheMlbStaticHeadshotUrl()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([660271]);

        Assert.Equal(
            "https://img.mlbstatic.com/mlb-photos/image/upload/"
                + "d_people:generic:headshot:67:current.png/w_128/q_auto:best/v1/people/660271/headshot/67/current",
            handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PrefetchAsync_SkipsIdsAlreadyOnDisk()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "660271.png"), PngBytes);

        var handler = new StubHttpMessageHandler();
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([660271]);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PrefetchAsync_DoesNotWriteNonPngPayloads()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes([0x3C, 0x68, 0x74, 0x6D, 0x6C]);   // "<html"
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([1]);

        Assert.Null(cache.FilePath(1));
    }

    [Fact]
    public async Task PrefetchAsync_SwallowsHttpErrors()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([1]);

        Assert.Null(cache.FilePath(1));
    }

    [Fact]
    public void FilePath_ReturnsNullWhenNotCached()
    {
        var cache = new HeadshotCache(new StubHttpMessageHandler().CreateClient(), _directory);
        Assert.Null(cache.FilePath(999));
    }

    [Fact]
    public void DefaultCacheDirectory_LivesUnderLocalAppData()
    {
        var path = HeadshotCache.DefaultCacheDirectory();

        Assert.Contains("onDeck", path);
        Assert.EndsWith("Headshots", path);
    }
}
```

- [ ] **Step 2: Add `EnqueueBytes` to the stub**

In `tests/OnDeck.Core.Tests/Networking/StubHttpMessageHandler.cs`, change the queue to carry bytes:

```csharp
    private readonly Queue<(HttpStatusCode Status, byte[] Body)> _responses = new();
    private (HttpStatusCode Status, byte[] Body)? _last;

    public void EnqueueJson(string json) => _responses.Enqueue((HttpStatusCode.OK, Encoding.UTF8.GetBytes(json)));

    public void EnqueueBytes(byte[] body) => _responses.Enqueue((HttpStatusCode.OK, body));

    public void EnqueueStatus(HttpStatusCode status) => _responses.Enqueue((status, []));
```

and build the response with `new ByteArrayContent(body)`:

```csharp
        var (status, body) = _last ?? throw new InvalidOperationException("no response queued");

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return new HttpResponseMessage(status) { Content = content };
```

Add `using System.Net.Http.Headers;`. Existing JSON tests keep working — `ReadAsStringAsync` decodes the bytes as UTF-8.

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~HeadshotCacheTests`
Expected: compile error — `HeadshotCache` does not exist.

- [ ] **Step 4: Write `src/OnDeck.Core/Utilities/HeadshotCache.cs`**

```csharp
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
        bytes.Length > PngSignature.Length && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    private string PathFor(int playerId) => Path.Combine(cacheDirectory, $"{playerId}.png");
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~HeadshotCacheTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add windows/
git commit -m "phase 4: port HeadshotCache as a raw file cache"
```

---

## Task 2: StateManager

**Files:**
- Create: `src/OnDeck.Core/Managers/StateManager.cs`
- Create: `tests/OnDeck.Core.Tests/Managers/StateManagerTests.cs`

**Spec:** `Managers/StateManager.swift`

**Interfaces:**
- Produces: `sealed class StateManager` with
  `Dictionary<int, PlayerState> PlayerStates { get; }`,
  `Action<int, PlayerState?, PlayerState>? OnStateChange { get; set; }` — `(playerId, oldState, newState)`,
  `void Update(int playerId, PlayerState state)`,
  `DateTimeOffset? StartTimeFor(int playerId)`,
  `void SetUpcoming(IReadOnlyList<int> playerIds, DateTimeOffset startTime)`,
  `void SetGameOver(IReadOnlyList<int> playerIds, int gamePk)`,
  `void Reset()`.

**Behaviour:** `Update` always fires `OnStateChange`, even when the new state equals the old. `SetUpcoming` only fills players with **no** existing state and does **not** fire the callback (Swift assigns the dictionary directly). `SetGameOver` goes through `Update`, so it does fire.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Managers/StateManagerTests.cs`:

```csharp
using OnDeck.Core.Managers;
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Managers;

public class StateManagerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 8, 23, 10, 0, TimeSpan.Zero);

    private static PlayerState Upcoming() => new PlayerState.Upcoming(Start);

    [Fact]
    public void Update_StoresStateAndFiresCallbackWithOldAndNew()
    {
        var manager = new StateManager();
        var changes = new List<(int Id, PlayerState? Old, PlayerState New)>();
        manager.OnStateChange = (id, oldState, newState) => changes.Add((id, oldState, newState));

        manager.Update(1, Upcoming());
        var active = new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff());
        manager.Update(1, active);

        Assert.Equal(active, manager.PlayerStates[1]);
        Assert.Equal(2, changes.Count);
        Assert.Null(changes[0].Old);
        Assert.Equal(Upcoming(), changes[1].Old);
        Assert.Equal(active, changes[1].New);
    }

    [Fact]
    public void Update_FiresEvenWhenStateIsUnchanged()
    {
        var manager = new StateManager();
        var count = 0;
        manager.OnStateChange = (_, _, _) => count++;

        manager.Update(1, Upcoming());
        manager.Update(1, Upcoming());

        Assert.Equal(2, count);
    }

    [Fact]
    public void StartTimeFor_ReturnsTimeOnlyForUpcoming()
    {
        var manager = new StateManager();
        manager.Update(1, Upcoming());
        manager.Update(2, new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff()));

        Assert.Equal(Start, manager.StartTimeFor(1));
        Assert.Null(manager.StartTimeFor(2));
        Assert.Null(manager.StartTimeFor(3));
    }

    [Fact]
    public void SetUpcoming_OnlyFillsPlayersWithNoExistingState()
    {
        var manager = new StateManager();
        var existing = new PlayerState.Inactive(new PlayerState.InactiveReason.GameOver(1));
        manager.Update(1, existing);

        manager.SetUpcoming([1, 2], Start);

        Assert.Equal(existing, manager.PlayerStates[1]);
        Assert.Equal(Upcoming(), manager.PlayerStates[2]);
    }

    [Fact]
    public void SetUpcoming_DoesNotFireTheChangeCallback()
    {
        var manager = new StateManager();
        var count = 0;
        manager.OnStateChange = (_, _, _) => count++;

        manager.SetUpcoming([1, 2], Start);

        Assert.Equal(0, count);
    }

    [Fact]
    public void SetGameOver_MarksEveryPlayerAndFiresTheCallback()
    {
        var manager = new StateManager();
        var count = 0;
        manager.OnStateChange = (_, _, _) => count++;

        manager.SetGameOver([1, 2], 776543);

        foreach (var id in new[] { 1, 2 })
        {
            var inactive = Assert.IsType<PlayerState.Inactive>(manager.PlayerStates[id]);
            Assert.Equal(776543, Assert.IsType<PlayerState.InactiveReason.GameOver>(inactive.Reason).GamePk);
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void Reset_ClearsEveryState()
    {
        var manager = new StateManager();
        manager.Update(1, Upcoming());

        manager.Reset();

        Assert.Empty(manager.PlayerStates);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~StateManagerTests`
Expected: compile error — `StateManager` does not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Managers/StateManager.cs`**

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Managers;

/// <summary>Port of <c>Managers/StateManager.swift</c>.</summary>
public sealed class StateManager
{
    /// <summary>Keyed by MLB player ID.</summary>
    public Dictionary<int, PlayerState> PlayerStates { get; } = [];

    /// <summary>Fired when a player's state changes. Args: (playerId, oldState, newState).</summary>
    public Action<int, PlayerState?, PlayerState>? OnStateChange { get; set; }

    public void Update(int playerId, PlayerState state)
    {
        PlayerStates.TryGetValue(playerId, out var oldState);
        PlayerStates[playerId] = state;
        OnStateChange?.Invoke(playerId, oldState, state);
    }

    public DateTimeOffset? StartTimeFor(int playerId) =>
        PlayerStates.TryGetValue(playerId, out var state) && state is PlayerState.Upcoming upcoming
            ? upcoming.StartTime
            : null;

    /// <summary>Sets players to upcoming with a given start time (used when the schedule is fetched).</summary>
    public void SetUpcoming(IReadOnlyList<int> playerIds, DateTimeOffset startTime)
    {
        foreach (var id in playerIds)
        {
            if (!PlayerStates.ContainsKey(id)) PlayerStates[id] = new PlayerState.Upcoming(startTime);
        }
    }

    /// <summary>Sets all players in a game to inactive (game over).</summary>
    public void SetGameOver(IReadOnlyList<int> playerIds, int gamePk)
    {
        foreach (var id in playerIds)
        {
            Update(id, new PlayerState.Inactive(new PlayerState.InactiveReason.GameOver(gamePk)));
        }
    }

    /// <summary>Clears all state (e.g. on a new day).</summary>
    public void Reset() => PlayerStates.Clear();
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~StateManagerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "phase 4: port StateManager"
```

---

## Task 3: ISettingsStore and RosterManager

**Files:**
- Create: `src/OnDeck.Core/ISettingsStore.cs`
- Create: `src/OnDeck.Core/Managers/RosterManager.cs`
- Create: `tests/OnDeck.Core.Tests/InMemorySettingsStore.cs`
- Create: `tests/OnDeck.Core.Tests/Managers/RosterManagerTests.cs`

**Spec:** `Managers/RosterManager.swift`

**Interfaces:**
- Produces:
  - `interface ISettingsStore` — the complete persisted surface from `PORT_PLAN.md`, verbatim:
    `string? RosterUrl`, `string? SelectedTeamId`, `bool HideBenchPlayers`, `bool AlwaysOpenPopout`,
    `bool NotifyBatting`, `bool NotifyPitching`, `bool NotifyAtBatResult`, `bool NotifyPitchingResult`,
    `bool NotifyNotInLineup`, `string? RosterCacheJson` — all `{ get; set; }`. The five notify flags default to `true` in implementations.
  - `sealed class RosterManager(FantraxApi fantrax, MlbStatsApi mlb, ISettingsStore settings, HeadshotCache? headshots = null, TimeProvider? timeProvider = null)` with
    `IReadOnlyList<Player> Players { get; }`, `DateTimeOffset? LastSyncDate { get; }`, `string? Error { get; }`, `bool IsSyncing { get; }`,
    `void LoadCachedRoster()`, `Task SyncRosterAsync(string leagueId, string teamId, CancellationToken ct = default)`.
  - `sealed class InMemorySettingsStore : ISettingsStore` in the test project.

**Sync pipeline (preserve order):**
1. `IsSyncing = true`, `Error = null`.
2. `fantrax.FetchRosterAsync(leagueId, teamId)`.
3. Resolve MLB IDs **concurrently** (`Task.WhenAll`), each via `mlb.SearchPlayerAsync(NameCleaner.Clean(name), teamShortName)`. A throw or a `null` ID drops that player silently.
4. Fold into a dictionary keyed by MLB ID. On a **duplicate** (two-way players like Ohtani): union `Positions` and `FantraxPositions`, keep the **existing** name and team, and take the **lower** `statusId` as the more-active status.
5. On first insert: `Name = NameCleaner.Clean(fp.Name)`, `Team = TeamMapping.MlbTeamName(fp.TeamShortName) ?? fp.TeamShortName`.
6. Sort by name (ordinal), set `LastSyncDate`, write the cache, prefetch headshots.
7. On any exception: `Error = $"Roster sync failed: {ex.Message}"` and **keep the previous roster**.
8. `IsSyncing = false` in all paths.

**`ParsePositions`:** `SP`, `RP`, `P` (trimmed, uppercased) → `Pitcher`; anything else → `Hitter`; empty input → `{ Hitter }`.

**Cache:** JSON array of `{id, name, team, positions, fantraxPositions, rosterStatus}` in `ISettingsStore.RosterCacheJson`. `fantraxPositions` and `rosterStatus` are optional on read (Swift's `CachedPlayer` has them optional for backward compatibility) — default to empty and `Active`. `LoadCachedRoster` swallows malformed JSON.

- [ ] **Step 1: Write the in-memory settings store**

Create `tests/OnDeck.Core.Tests/InMemorySettingsStore.cs`:

```csharp
using OnDeck.Core;

namespace OnDeck.Core.Tests;

public sealed class InMemorySettingsStore : ISettingsStore
{
    public string? RosterUrl { get; set; }
    public string? SelectedTeamId { get; set; }
    public bool HideBenchPlayers { get; set; }
    public bool AlwaysOpenPopout { get; set; }
    public bool NotifyBatting { get; set; } = true;
    public bool NotifyPitching { get; set; } = true;
    public bool NotifyAtBatResult { get; set; } = true;
    public bool NotifyPitchingResult { get; set; } = true;
    public bool NotifyNotInLineup { get; set; } = true;
    public string? RosterCacheJson { get; set; }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/OnDeck.Core.Tests/Managers/RosterManagerTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Networking;

namespace OnDeck.Core.Tests.Managers;

public class RosterManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);

    /// <summary>One Fantrax roster row per entry, then one MLB search response per row.</summary>
    private static (RosterManager Manager, InMemorySettingsStore Settings) Create(
        string rosterJson, params string[] searchJson)
    {
        var fantraxHandler = new StubHttpMessageHandler();
        fantraxHandler.EnqueueJson(rosterJson);

        var mlbHandler = new StubHttpMessageHandler();
        foreach (var json in searchJson) mlbHandler.EnqueueJson(json);

        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        var settings = new InMemorySettingsStore();
        var manager = new RosterManager(
            new FantraxApi(fantraxHandler.CreateClient(), time),
            new MlbStatsApi(mlbHandler.CreateClient(), time),
            settings,
            headshots: null,
            timeProvider: time);

        return (manager, settings);
    }

    private static string Roster(params string[] rows) => $$"""
        {"responses": [{"data": {"tables": [{"rows": [{{string.Join(",", rows)}}]}]}}]}
        """;

    private static string Row(string name, string team, string positions, int statusId = 1) => $$"""
        {"statusId": {{statusId}}, "scorer": {"scorerId": "s", "name": "{{name}}",
          "teamShortName": "{{team}}", "posShortNames": "{{positions}}"}}
        """;

    private static string Person(int id) => $$"""
        {"people": [{"id": {{id}}, "fullName": "x", "currentTeam": {"id": 1, "name": "Los Angeles Dodgers"}}]}
        """;

    [Fact]
    public async Task SyncRosterAsync_ResolvesMlbIdsAndCleansNames()
    {
        var (manager, _) = Create(Roster(Row("T.J. Rumfield-P", "LAD", "SP")), Person(500));

        await manager.SyncRosterAsync("lg", "tm");

        var player = Assert.Single(manager.Players);
        Assert.Equal(500, player.Id);
        Assert.Equal("TJ Rumfield", player.Name);
        Assert.Equal("Los Angeles Dodgers", player.Team);
        Assert.Contains(PlayerPosition.Pitcher, player.Positions);
        Assert.Null(manager.Error);
        Assert.Equal(Now, manager.LastSyncDate);
        Assert.False(manager.IsSyncing);
    }

    [Theory]
    [InlineData("SP", PlayerPosition.Pitcher)]
    [InlineData("RP", PlayerPosition.Pitcher)]
    [InlineData("P", PlayerPosition.Pitcher)]
    [InlineData("OF", PlayerPosition.Hitter)]
    [InlineData("C", PlayerPosition.Hitter)]
    public async Task SyncRosterAsync_ClassifiesPositions(string code, PlayerPosition expected)
    {
        var (manager, _) = Create(Roster(Row("Guy", "LAD", code)), Person(1));

        await manager.SyncRosterAsync("lg", "tm");

        Assert.Equal([expected], Assert.Single(manager.Players).Positions);
    }

    [Fact]
    public async Task SyncRosterAsync_MergesTwoWayPlayersByMlbId()
    {
        // Ohtani appears twice - once as -P, once as -DH - and both resolve to the same MLB ID.
        var (manager, _) = Create(
            Roster(Row("Shohei Ohtani-P", "LAD", "SP", statusId: 2),
                   Row("Shohei Ohtani-DH", "LAD", "DH", statusId: 1)),
            Person(660271), Person(660271));

        await manager.SyncRosterAsync("lg", "tm");

        var ohtani = Assert.Single(manager.Players);
        Assert.True(ohtani.IsPitcher);
        Assert.True(ohtani.IsHitter);
        Assert.Equal(new HashSet<string> { "SP", "DH" }, ohtani.FantraxPositions.ToHashSet());
        Assert.Equal(RosterStatus.Active, ohtani.RosterStatus);   // lower statusId wins
    }

    [Fact]
    public async Task SyncRosterAsync_SortsPlayersByName()
    {
        var (manager, _) = Create(
            Roster(Row("Zeta Guy", "LAD", "OF"), Row("Alpha Guy", "LAD", "OF")),
            Person(2), Person(1));

        await manager.SyncRosterAsync("lg", "tm");

        Assert.Equal(["Alpha Guy", "Zeta Guy"], manager.Players.Select(p => p.Name));
    }

    [Fact]
    public async Task SyncRosterAsync_MapsStatusIdToRosterStatus()
    {
        var (manager, _) = Create(Roster(Row("Guy", "LAD", "OF", statusId: 9)), Person(1));

        await manager.SyncRosterAsync("lg", "tm");

        Assert.Equal(RosterStatus.Minors, Assert.Single(manager.Players).RosterStatus);
        Assert.True(Assert.Single(manager.Players).IsUnavailable);
    }

    [Fact]
    public async Task SyncRosterAsync_KeepsPreviousRosterOnFailure()
    {
        var (manager, _) = Create(Roster(Row("Guy", "LAD", "OF")), Person(1));
        await manager.SyncRosterAsync("lg", "tm");

        // A second manager over a failing Fantrax endpoint, seeded from the same cache.
        var fantraxHandler = new StubHttpMessageHandler();
        fantraxHandler.EnqueueJson("[]");   // not a JSON object -> InvalidResponse
        var failing = new RosterManager(
            new FantraxApi(fantraxHandler.CreateClient()),
            new MlbStatsApi(new StubHttpMessageHandler().CreateClient()),
            new InMemorySettingsStore(),
            headshots: null);

        await failing.SyncRosterAsync("lg", "tm");

        Assert.Empty(failing.Players);
        Assert.NotNull(failing.Error);
        Assert.StartsWith("Roster sync failed:", failing.Error);
        Assert.False(failing.IsSyncing);
    }

    [Fact]
    public async Task SyncRosterAsync_WritesAndReloadsTheCache()
    {
        var (manager, settings) = Create(
            Roster(Row("Shohei Ohtani-P", "LAD", "SP")), Person(660271));

        await manager.SyncRosterAsync("lg", "tm");
        Assert.NotNull(settings.RosterCacheJson);

        var reloaded = new RosterManager(
            new FantraxApi(new StubHttpMessageHandler().CreateClient()),
            new MlbStatsApi(new StubHttpMessageHandler().CreateClient()),
            settings,
            headshots: null);
        reloaded.LoadCachedRoster();

        Assert.Equal(manager.Players, reloaded.Players);
    }

    [Fact]
    public void LoadCachedRoster_IgnoresMalformedJson()
    {
        var settings = new InMemorySettingsStore { RosterCacheJson = "{not json" };
        var manager = new RosterManager(
            new FantraxApi(new StubHttpMessageHandler().CreateClient()),
            new MlbStatsApi(new StubHttpMessageHandler().CreateClient()),
            settings,
            headshots: null);

        manager.LoadCachedRoster();

        Assert.Empty(manager.Players);
    }

    [Fact]
    public void LoadCachedRoster_DefaultsOptionalCacheFields()
    {
        var settings = new InMemorySettingsStore
        {
            RosterCacheJson = """[{"id": 1, "name": "Guy", "team": "LAD", "positions": ["Hitter"]}]""",
        };
        var manager = new RosterManager(
            new FantraxApi(new StubHttpMessageHandler().CreateClient()),
            new MlbStatsApi(new StubHttpMessageHandler().CreateClient()),
            settings,
            headshots: null);

        manager.LoadCachedRoster();

        var player = Assert.Single(manager.Players);
        Assert.Empty(player.FantraxPositions);
        Assert.Equal(RosterStatus.Active, player.RosterStatus);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~RosterManagerTests`
Expected: compile error — `ISettingsStore` / `RosterManager` do not exist.

- [ ] **Step 4: Write `src/OnDeck.Core/ISettingsStore.cs`**

```csharp
namespace OnDeck.Core;

/// <summary>
/// The complete persisted surface Core reads, implemented by the shell. Swift keys are in
/// comments so the two codebases stay cross-referenceable. Floating-panel frame and
/// launch-at-login are shell-only and deliberately absent.
/// </summary>
public interface ISettingsStore
{
    string? RosterUrl { get; set; }             // rosterURL
    string? SelectedTeamId { get; set; }        // selectedTeamID
    bool HideBenchPlayers { get; set; }         // hideBenchPlayers
    bool AlwaysOpenPopout { get; set; }         // alwaysOpenPopout
    bool NotifyBatting { get; set; }            // notifyBatting, default true
    bool NotifyPitching { get; set; }           // notifyPitching, default true
    bool NotifyAtBatResult { get; set; }        // notifyAtBatResult, default true
    bool NotifyPitchingResult { get; set; }     // notifyPitchingResult, default true
    bool NotifyNotInLineup { get; set; }        // notifyNotInLineup, default true
    string? RosterCacheJson { get; set; }       // RosterManager cache blob
}
```

- [ ] **Step 5: Write `src/OnDeck.Core/Managers/RosterManager.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Managers;

/// <summary>Port of <c>Managers/RosterManager.swift</c>.</summary>
public sealed class RosterManager(
    FantraxApi fantrax,
    MlbStatsApi mlb,
    ISettingsStore settings,
    HeadshotCache? headshots = null,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions CacheOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<Player> Players { get; private set; } = [];

    public DateTimeOffset? LastSyncDate { get; private set; }

    public string? Error { get; private set; }

    public bool IsSyncing { get; private set; }

    public async Task SyncRosterAsync(string leagueId, string teamId, CancellationToken ct = default)
    {
        IsSyncing = true;
        Error = null;

        try
        {
            var fantraxPlayers = await fantrax.FetchRosterAsync(leagueId, teamId, ct);

            // Resolve all MLB IDs concurrently.
            var resolved = await Task.WhenAll(fantraxPlayers.Select(async fp =>
            {
                try
                {
                    var id = await mlb.SearchPlayerAsync(NameCleaner.Clean(fp.Name), fp.TeamShortName, ct);
                    return id is { } mlbId ? (Player: fp, MlbId: mlbId) : ((FantraxPlayer, int)?)null;
                }
                catch (Exception)
                {
                    return null;
                }
            }));

            var byMlbId = new Dictionary<int, Player>();   // keyed by MLB ID for dedup

            foreach (var entry in resolved)
            {
                if (entry is not var (fp, mlbId)) continue;

                var positions = ParsePositions(fp.Positions);
                var rawPositions = fp.Positions
                    .Select(p => p.Trim().ToUpperInvariant())
                    .ToHashSet(StringComparer.Ordinal);
                var rosterStatus = Enum.IsDefined(typeof(RosterStatus), fp.StatusId)
                    ? (RosterStatus)fp.StatusId
                    : RosterStatus.Active;

                if (byMlbId.TryGetValue(mlbId, out var existing))
                {
                    // Merge positions for two-way players (e.g. Ohtani).
                    var merged = existing.Positions.ToHashSet();
                    merged.UnionWith(positions);

                    var mergedRaw = existing.FantraxPositions.ToHashSet(StringComparer.Ordinal);
                    mergedRaw.UnionWith(rawPositions);

                    // Use the most active status when merging.
                    var bestStatus = (int)existing.RosterStatus < (int)rosterStatus
                        ? existing.RosterStatus
                        : rosterStatus;

                    byMlbId[mlbId] = existing with
                    {
                        Positions = merged,
                        FantraxPositions = mergedRaw,
                        RosterStatus = bestStatus,
                    };
                }
                else
                {
                    var teamName = TeamMapping.MlbTeamName(fp.TeamShortName) ?? fp.TeamShortName;
                    byMlbId[mlbId] = new Player(
                        mlbId,
                        NameCleaner.Clean(fp.Name),
                        teamName,
                        positions,
                        rawPositions,
                        rosterStatus);
                }
            }

            Players = [.. byMlbId.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];
            LastSyncDate = _time.GetUtcNow();
            CacheRoster();

            if (headshots is not null) await headshots.PrefetchAsync([.. Players.Select(p => p.Id)], ct);
        }
        catch (Exception ex)
        {
            Error = $"Roster sync failed: {ex.Message}";
            // Keep the last cached roster if available.
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Determines pitcher vs hitter from Fantrax position strings.
    /// SP, RP, P = pitcher. Everything else = hitter.
    /// </summary>
    private static HashSet<PlayerPosition> ParsePositions(IReadOnlyList<string> positions)
    {
        string[] pitcherCodes = ["SP", "RP", "P"];
        var result = new HashSet<PlayerPosition>();

        foreach (var position in positions)
        {
            var trimmed = position.Trim().ToUpperInvariant();
            result.Add(pitcherCodes.Contains(trimmed) ? PlayerPosition.Pitcher : PlayerPosition.Hitter);
        }

        if (result.Count == 0) result.Add(PlayerPosition.Hitter);   // default to hitter

        return result;
    }

    // MARK: - Caching

    private void CacheRoster()
    {
        var cached = Players.Select(p => new CachedPlayer
        {
            Id = p.Id,
            Name = p.Name,
            Team = p.Team,
            Positions = [.. p.Positions],
            FantraxPositions = [.. p.FantraxPositions],
            RosterStatus = p.RosterStatus,
        });

        settings.RosterCacheJson = JsonSerializer.Serialize(cached, CacheOptions);
    }

    public void LoadCachedRoster()
    {
        if (settings.RosterCacheJson is not { Length: > 0 } json) return;

        List<CachedPlayer>? cached;
        try
        {
            cached = JsonSerializer.Deserialize<List<CachedPlayer>>(json, CacheOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (cached is null) return;

        Players =
        [
            .. cached.Select(c => new Player(
                c.Id,
                c.Name,
                c.Team,
                c.Positions.ToHashSet(),
                (c.FantraxPositions ?? []).ToHashSet(StringComparer.Ordinal),
                c.RosterStatus ?? RosterStatus.Active))
        ];
    }

    private sealed class CachedPlayer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Team { get; set; } = "";
        public List<PlayerPosition> Positions { get; set; } = [];
        public List<string>? FantraxPositions { get; set; }
        public RosterStatus? RosterStatus { get; set; }
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~RosterManagerTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add windows/
git commit -m "phase 4: add ISettingsStore and port RosterManager"
```

---

## Task 4: ScheduleManager

**Files:**
- Create: `src/OnDeck.Core/Managers/ScheduleManager.cs`
- Create: `tests/OnDeck.Core.Tests/Managers/ScheduleManagerTests.cs`

**Spec:** `Managers/ScheduleManager.swift:13-23` (`baseballDate` already lives in `BaseballCalendar`)

**Interfaces:**
- Produces: `sealed class ScheduleManager(MlbStatsApi mlb, TimeProvider? timeProvider = null)` with
  `IReadOnlyList<Game> TodaysGames { get; }`, `string? Error { get; }`,
  `Task FetchScheduleAsync(IReadOnlySet<string> teamNames, CancellationToken ct = default)`.

**Behaviour:** clears `Error`, fetches the schedule for `BaseballCalendar.Today(timeProvider)`, keeps games whose `HomeTeam` **or** `AwayTeam` is in `teamNames` (exact set membership — these are MLB full names as produced by `TeamMapping.MlbTeamName`). On throw: `Error = $"Schedule fetch failed: {ex.Message}"` and `TodaysGames` is left untouched.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Managers/ScheduleManagerTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Managers;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Networking;

namespace OnDeck.Core.Tests.Managers;

public class ScheduleManagerTests
{
    private const string TwoGamesJson = """
    {
      "dates": [{"games": [
        {"gamePk": 1, "gameDate": "2026-08-08T23:10:00Z", "status": {"abstractGameState": "Preview"},
         "teams": {"away": {"team": {"id": 137, "name": "San Francisco Giants"}},
                   "home": {"team": {"id": 119, "name": "Los Angeles Dodgers"}}}},
        {"gamePk": 2, "gameDate": "2026-08-08T23:10:00Z", "status": {"abstractGameState": "Preview"},
         "teams": {"away": {"team": {"id": 111, "name": "Boston Red Sox"}},
                   "home": {"team": {"id": 147, "name": "New York Yankees"}}}}
      ]}]
    }
    """;

    private static (ScheduleManager Manager, StubHttpMessageHandler Handler) Create(
        string json, DateTimeOffset? now = null)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);

        var time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        return (new ScheduleManager(new MlbStatsApi(handler.CreateClient(), time), time), handler);
    }

    [Fact]
    public async Task FetchScheduleAsync_KeepsOnlyGamesInvolvingTheGivenTeams()
    {
        var (manager, _) = Create(TwoGamesJson);

        await manager.FetchScheduleAsync(new HashSet<string> { "Los Angeles Dodgers" });

        Assert.Equal([1], manager.TodaysGames.Select(g => g.Id));
        Assert.Null(manager.Error);
    }

    [Fact]
    public async Task FetchScheduleAsync_MatchesTheAwaySideToo()
    {
        var (manager, _) = Create(TwoGamesJson);

        await manager.FetchScheduleAsync(new HashSet<string> { "Boston Red Sox" });

        Assert.Equal([2], manager.TodaysGames.Select(g => g.Id));
    }

    [Fact]
    public async Task FetchScheduleAsync_ReturnsNothingWhenNoTeamsMatch()
    {
        var (manager, _) = Create(TwoGamesJson);

        await manager.FetchScheduleAsync(new HashSet<string> { "Seattle Mariners" });

        Assert.Empty(manager.TodaysGames);
    }

    [Fact]
    public async Task FetchScheduleAsync_UsesTheBaseballDateBeforeEightAm()
    {
        var (manager, handler) = Create(
            TwoGamesJson, new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero));

        await manager.FetchScheduleAsync(new HashSet<string>());

        Assert.Contains("date=2026-08-07", handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task FetchScheduleAsync_RecordsErrorsAndKeepsPreviousGames()
    {
        var (manager, _) = Create(TwoGamesJson);
        await manager.FetchScheduleAsync(new HashSet<string> { "Los Angeles Dodgers" });

        var failingHandler = new StubHttpMessageHandler();
        failingHandler.EnqueueStatus(System.Net.HttpStatusCode.InternalServerError);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero));
        var failing = new ScheduleManager(new MlbStatsApi(failingHandler.CreateClient(), time), time);

        await failing.FetchScheduleAsync(new HashSet<string> { "Los Angeles Dodgers" });

        Assert.Empty(failing.TodaysGames);
        Assert.StartsWith("Schedule fetch failed:", failing.Error);
    }

    [Fact]
    public async Task FetchScheduleAsync_ClearsAPreviousError()
    {
        var (manager, _) = Create(TwoGamesJson);

        await manager.FetchScheduleAsync(new HashSet<string> { "Los Angeles Dodgers" });

        Assert.Null(manager.Error);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~ScheduleManagerTests`
Expected: compile error — `ScheduleManager` does not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Managers/ScheduleManager.cs`**

```csharp
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Managers;

/// <summary>Port of <c>Managers/ScheduleManager.swift</c>.</summary>
public sealed class ScheduleManager(MlbStatsApi mlb, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<Game> TodaysGames { get; private set; } = [];

    public string? Error { get; private set; }

    /// <summary>
    /// Fetches today's schedule and filters to games involving the given team names.
    /// Uses the "baseball day" — before 8 AM counts as the previous day.
    /// </summary>
    public async Task FetchScheduleAsync(IReadOnlySet<string> teamNames, CancellationToken ct = default)
    {
        Error = null;

        try
        {
            var allGames = await mlb.FetchScheduleAsync(BaseballCalendar.Today(_time), ct);
            TodaysGames =
            [
                .. allGames.Where(game =>
                    teamNames.Contains(game.HomeTeam) || teamNames.Contains(game.AwayTeam))
            ];
        }
        catch (Exception ex)
        {
            Error = $"Schedule fetch failed: {ex.Message}";
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~ScheduleManagerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "phase 4: port ScheduleManager"
```

---

## Task 5: GameMonitor — lifecycle

**Files:**
- Create: `src/OnDeck.Core/Managers/GameMonitor.cs`
- Create: `tests/OnDeck.Core.Tests/Managers/GameMonitorLifecycleTests.cs`

**Spec:** `Managers/GameMonitor.swift:8-135`

**Interfaces:**
- Produces: `sealed class GameMonitor(MlbStatsApi mlb, TimeProvider? timeProvider = null)` with
  - `bool IsMonitoring { get; }`
  - `Dictionary<int, string> LastPlayDescriptions { get; }` — playerId → last completed play description
  - `Dictionary<int, LiveFeedData> LatestFeeds { get; }` — gamePk → feed
  - `Dictionary<int, GameLineup> LineupPlayerIds { get; }` — gamePk → per-side lineup IDs
  - `Action<int>? OnLineupUpdate { get; set; }`, `Action<int>? OnGameStart { get; set; }`
  - `bool IsLive(int gamePk)`
  - `void Configure(StateManager stateManager)`
  - `void StartMonitoring(IReadOnlyList<Game> games, IReadOnlyList<Player> players)`
  - `void StopMonitoring()`, `void StopMonitoring(int gamePk)`
  - `void InvalidateTimecodes()`

**Behaviour to pin:**
- `StartMonitoring` calls `StopMonitoring()` first, then repopulates and launches the coordinator. **Seed data must be assigned after this call** — the plan's tests assert `LineupPlayerIds` set before `StartMonitoring` is wiped.
- Full `StopMonitoring()` clears everything **including** `LatestFeeds` and `LastPlayDescriptions`.
- `StopMonitoring(gamePk)` clears the per-game tracking maps but **retains** `LatestFeeds[gamePk]`; when no games remain it cancels the coordinator and sets `IsMonitoring = false`.
- `InvalidateTimecodes` nulls each cached feed's `TimeStamp`, preserving the rest so the UI keeps rendering during the next round trip.
- `MemoryPressureRelief` calls are dropped entirely.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Managers/GameMonitorLifecycleTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Networking;

namespace OnDeck.Core.Tests.Managers;

public class GameMonitorLifecycleTests
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);

    internal static Game GameAt(int id, DateTimeOffset start, string home = "Los Angeles Dodgers",
        string away = "San Francisco Giants", int? homePitcher = null, int? awayPitcher = null) =>
        new(id, home, away, 119, 137, start, homePitcher, awayPitcher, [], [], []);

    internal static Player Hitter(int id, string team = "Los Angeles Dodgers") =>
        new(id, $"Player {id}", team,
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    internal static (GameMonitor Monitor, FakeTimeProvider Time, StubHttpMessageHandler Handler) Create()
    {
        var handler = new StubHttpMessageHandler();
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return (new GameMonitor(new MlbStatsApi(handler.CreateClient(), time), time), time, handler);
    }

    [Fact]
    public void StartMonitoring_SetsIsMonitoring()
    {
        var (monitor, _, _) = Create();

        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);

        Assert.True(monitor.IsMonitoring);
        monitor.StopMonitoring();
    }

    [Fact]
    public void StartMonitoring_ClearsSeedDataSetBeforehand()
    {
        // The seed-after-start rule: StartMonitoring calls StopMonitoring internally.
        var (monitor, _, _) = Create();
        monitor.LineupPlayerIds[1] = new GameLineup { Home = [10] };

        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);

        Assert.Empty(monitor.LineupPlayerIds);
        monitor.StopMonitoring();
    }

    [Fact]
    public void StopMonitoring_ClearsEverythingIncludingFeeds()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);
        monitor.LatestFeeds[1] = new LiveFeedData { GameState = "Live" };
        monitor.LastPlayDescriptions[10] = "single";
        monitor.LineupPlayerIds[1] = new GameLineup { Home = [10] };

        monitor.StopMonitoring();

        Assert.False(monitor.IsMonitoring);
        Assert.Empty(monitor.LatestFeeds);
        Assert.Empty(monitor.LastPlayDescriptions);
        Assert.Empty(monitor.LineupPlayerIds);
    }

    [Fact]
    public void StopMonitoringGame_RetainsThatGamesFeed()
    {
        // AppState's Done section reads feed.playerStats for finished games.
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(5)), GameAt(2, Now.AddHours(6))], [Hitter(10)]);
        monitor.LatestFeeds[1] = new LiveFeedData { GameState = "Final" };
        monitor.LineupPlayerIds[1] = new GameLineup { Home = [10] };

        monitor.StopMonitoring(1);

        Assert.True(monitor.LatestFeeds.ContainsKey(1));
        Assert.False(monitor.LineupPlayerIds.ContainsKey(1));
        Assert.True(monitor.IsMonitoring);      // game 2 is still monitored
        monitor.StopMonitoring();
    }

    [Fact]
    public void StopMonitoringGame_StopsAltogetherWhenNoGamesRemain()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);

        monitor.StopMonitoring(1);

        Assert.False(monitor.IsMonitoring);
    }

    [Fact]
    public void InvalidateTimecodes_NullsTimestampsButKeepsTheRest()
    {
        var (monitor, _, _) = Create();
        monitor.LatestFeeds[1] = new LiveFeedData
        {
            TimeStamp = "20260808_140000",
            GameState = "Live",
            HomeScore = 3,
        };

        monitor.InvalidateTimecodes();

        Assert.Null(monitor.LatestFeeds[1].TimeStamp);
        Assert.Equal("Live", monitor.LatestFeeds[1].GameState);
        Assert.Equal(3, monitor.LatestFeeds[1].HomeScore);
    }

    [Fact]
    public void IsLive_IsFalseUntilTheFeedReportsInProgress()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);

        Assert.False(monitor.IsLive(1));
        monitor.StopMonitoring();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~GameMonitorLifecycleTests`
Expected: compile error — `GameMonitor` does not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Managers/GameMonitor.cs`**

```csharp
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Managers;

/// <summary>
/// Port of <c>Managers/GameMonitor.swift</c>. Centralised polling coordinator: one loop
/// sleeps to the next event (pre-game milestone or the 15-min-before-start active window),
/// then polls every game in range at 10s using diffPatch, falling back to a full feed fetch
/// on error or transition.
/// </summary>
public sealed class GameMonitor(MlbStatsApi mlb, TimeProvider? timeProvider = null)
{
    /// <summary>Pre-game milestone times (before game start) for one-shot lineup checks.</summary>
    private static readonly TimeSpan[] PreGameMilestones =
        [TimeSpan.FromHours(2), TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)];

    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private CancellationTokenSource? _coordinator;
    private Task? _coordinatorTask;
    private readonly Dictionary<int, Game> _monitoredGames = [];
    private HashSet<int> _rosterPlayerIds = [];
    private Dictionary<int, Player> _rosterPlayers = [];
    private StateManager? _stateManager;

    // Tracks previously seen batter/pitcher per game to detect transitions.
    private readonly Dictionary<int, int?> _lastBatterId = [];
    private readonly Dictionary<int, int?> _lastPitcherId = [];
    private readonly Dictionary<int, int> _lastHomePitcherId = [];
    private readonly Dictionary<int, int> _lastAwayPitcherId = [];

    /// <summary>Tracks which pre-game milestones have been fetched per game.</summary>
    private readonly Dictionary<int, HashSet<TimeSpan>> _completedMilestones = [];

    /// <summary>Games observed Live/In Progress at least once (for one-shot start detection).</summary>
    private readonly HashSet<int> _liveGamesSeen = [];

    public bool IsMonitoring { get; private set; }

    /// <summary>Last completed play description per player (for result notifications).</summary>
    public Dictionary<int, string> LastPlayDescriptions { get; } = [];

    /// <summary>Latest feed data per game (for In Game player display).</summary>
    public Dictionary<int, LiveFeedData> LatestFeeds { get; } = [];

    /// <summary>
    /// Lineup player IDs per game, tracked per side so consumers can tell whether a player's
    /// own team has submitted yet (vs just the opponent).
    /// </summary>
    public Dictionary<int, GameLineup> LineupPlayerIds { get; } = [];

    /// <summary>Fired when <see cref="LineupPlayerIds"/> for a game is populated or changes.</summary>
    public Action<int>? OnLineupUpdate { get; set; }

    /// <summary>Fired once per game the first time it transitions to Live/In Progress.</summary>
    public Action<int>? OnGameStart { get; set; }

    /// <summary>
    /// Whether the feed has observed this game as Live/In Progress. Driven by the feed, not
    /// the clock, so late-starting games aren't misclassified.
    /// </summary>
    public bool IsLive(int gamePk) => _liveGamesSeen.Contains(gamePk);

    public void Configure(StateManager stateManager) => _stateManager = stateManager;

    /// <summary>
    /// Resets all state and starts the coordinator. Callers seeding data (e.g.
    /// <see cref="LineupPlayerIds"/>) must do so <em>after</em> this returns — it calls
    /// <see cref="StopMonitoring()"/> internally, which clears everything.
    /// </summary>
    public void StartMonitoring(IReadOnlyList<Game> games, IReadOnlyList<Player> players)
    {
        StopMonitoring();

        _rosterPlayerIds = [.. players.Select(p => p.Id)];
        _rosterPlayers = players.ToDictionary(p => p.Id);
        foreach (var game in games) _monitoredGames[game.Id] = game;
        IsMonitoring = true;

        _coordinator = new CancellationTokenSource();
        _coordinatorTask = CoordinatePollingAsync(_coordinator.Token);
    }

    public void StopMonitoring()
    {
        _coordinator?.Cancel();
        _coordinator?.Dispose();
        _coordinator = null;
        _coordinatorTask = null;

        _monitoredGames.Clear();
        _lastBatterId.Clear();
        _lastPitcherId.Clear();
        _lastHomePitcherId.Clear();
        _lastAwayPitcherId.Clear();
        LineupPlayerIds.Clear();
        _liveGamesSeen.Clear();
        _completedMilestones.Clear();

        // A full stop (e.g. midnight refresh) drops LatestFeeds. The per-game overload
        // intentionally retains them so the Done section can keep reading finished games.
        LatestFeeds.Clear();
        LastPlayDescriptions.Clear();
        IsMonitoring = false;
    }

    /// <summary>Stops monitoring a specific game (e.g. when no roster players remain).</summary>
    public void StopMonitoring(int gamePk)
    {
        _monitoredGames.Remove(gamePk);
        LineupPlayerIds.Remove(gamePk);
        _lastBatterId.Remove(gamePk);
        _lastPitcherId.Remove(gamePk);
        _lastHomePitcherId.Remove(gamePk);
        _lastAwayPitcherId.Remove(gamePk);
        _completedMilestones.Remove(gamePk);
        _liveGamesSeen.Remove(gamePk);

        // Keep LatestFeeds[gamePk] - the Done section reads feed.PlayerStats for finished games.
        if (_monitoredGames.Count == 0)
        {
            _coordinator?.Cancel();
            _coordinator?.Dispose();
            _coordinator = null;
            _coordinatorTask = null;
            IsMonitoring = false;
        }
    }

    /// <summary>
    /// Nulls each cached feed's timestamp so the next poll cycle does a full fetch per game.
    /// Used after system wake when stored timecodes are stale. Preserves the rest of each
    /// feed so the UI keeps rendering last-known state during the round trip.
    /// </summary>
    public void InvalidateTimecodes()
    {
        foreach (var feed in LatestFeeds.Values) feed.TimeStamp = null;
    }

    // Task 6 adds CoordinatePollingAsync, NextEventDelay and PollCycleAsync.
    // Task 7 adds PollSingleGameAsync.
    // Task 8 adds ProcessFeed and its helpers.
    private Task CoordinatePollingAsync(CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~GameMonitorLifecycleTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "phase 4: GameMonitor lifecycle and state maps"
```

---

## Task 6: GameMonitor — event scheduling

**Files:**
- Modify: `src/OnDeck.Core/Managers/GameMonitor.cs`
- Create: `tests/OnDeck.Core.Tests/Managers/GameMonitorSchedulingTests.cs`

**Spec:** `GameMonitor.swift:139-204, 456-483`

**Interfaces:**
- Produces (internal, exposed for tests via `internal` + `InternalsVisibleTo`):
  `internal TimeSpan NextEventDelay()`, `internal IReadOnlyList<int> SelectGamesToPoll()`.

**`NextEventDelay` rules:** for each monitored game, the active window opens at `StartTime - 15min`; if that is already reached, return `TimeSpan.Zero` immediately. Otherwise consider each **uncompleted** milestone time (`StartTime - milestone`): if it has passed, return `TimeSpan.Zero`; if it is in the future, it is a candidate for the minimum. The active-window open time is also a candidate. Return the minimum candidate minus now, floored at zero; with no games, zero.

**`SelectGamesToPoll` rules:** every game whose active window has opened is polled. Additionally, for games still **more than** 15 minutes out, the *first* uncompleted milestone whose threshold has been reached is marked completed and that game is polled — `break` after the first, so one cycle consumes at most one milestone per game.

- [ ] **Step 1: Add `InternalsVisibleTo`**

In `src/OnDeck.Core/OnDeck.Core.csproj`, inside the `<Project>` element:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="OnDeck.Core.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test**

Create `tests/OnDeck.Core.Tests/Managers/GameMonitorSchedulingTests.cs`:

```csharp
using OnDeck.Core.Managers;
using static OnDeck.Core.Tests.Managers.GameMonitorLifecycleTests;

namespace OnDeck.Core.Tests.Managers;

public class GameMonitorSchedulingTests
{
    [Fact]
    public void NextEventDelay_IsZeroWhenAGameIsAlreadyInTheActiveWindow()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddMinutes(10))], [Hitter(10)]);

        Assert.Equal(TimeSpan.Zero, monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_CountsDownToTheNextMilestone()
    {
        // Start in 3h: the 2h milestone fires in 1h.
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(3))], [Hitter(10)]);

        Assert.Equal(TimeSpan.FromHours(1), monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_IsZeroWhenAMilestoneIsAlreadyDue()
    {
        // Start in 90 min: the 2h milestone has passed and is uncompleted.
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddMinutes(90))], [Hitter(10)]);

        Assert.Equal(TimeSpan.Zero, monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_FallsToTheActiveWindowOnceMilestonesAreConsumed()
    {
        // Start in 90 min: consume the 2h milestone, then the 1h milestone is 30 min out.
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddMinutes(90))], [Hitter(10)]);
        monitor.SelectGamesToPoll();

        Assert.Equal(TimeSpan.FromMinutes(30), monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_TakesTheEarliestAcrossGames()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring(
            [GameAt(1, Now.AddHours(6)), GameAt(2, Now.AddHours(3))], [Hitter(10)]);

        // Game 2's 2h milestone is 1h out; game 1's earliest is 4h out.
        Assert.Equal(TimeSpan.FromHours(1), monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_IsZeroWithNoGames()
    {
        var (monitor, _, _) = Create();
        Assert.Equal(TimeSpan.Zero, monitor.NextEventDelay());
    }

    [Fact]
    public void SelectGamesToPoll_IncludesGamesInsideTheActiveWindow()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring(
            [GameAt(1, Now.AddMinutes(10)), GameAt(2, Now.AddHours(8))], [Hitter(10)]);

        Assert.Equal([1], monitor.SelectGamesToPoll());
        monitor.StopMonitoring();
    }

    [Fact]
    public void SelectGamesToPoll_ConsumesOneMilestonePerCycle()
    {
        // Start in 25 min: 2h, 1h and 30m thresholds have all been crossed.
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddMinutes(25))], [Hitter(10)]);

        // ...but 25 min < the 15-min active window? No - 25 > 15, so it is a milestone game.
        Assert.Equal([1], monitor.SelectGamesToPoll());
        Assert.Equal([1], monitor.SelectGamesToPoll());   // next milestone
        Assert.Equal([1], monitor.SelectGamesToPoll());   // last milestone
        Assert.Empty(monitor.SelectGamesToPoll());        // all three consumed
        monitor.StopMonitoring();
    }

    [Fact]
    public void SelectGamesToPoll_IgnoresMilestonesForGamesAlreadyActive()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddMinutes(10))], [Hitter(10)]);

        // Active games poll every cycle regardless; milestones never accumulate for them.
        Assert.Equal([1], monitor.SelectGamesToPoll());
        Assert.Equal([1], monitor.SelectGamesToPoll());
        monitor.StopMonitoring();
    }

    [Fact]
    public void SelectGamesToPoll_IsEmptyForDistantGames()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(8))], [Hitter(10)]);

        Assert.Empty(monitor.SelectGamesToPoll());
        monitor.StopMonitoring();
    }
}
```

- [ ] **Step 3: Add the scheduling methods**

Replace the `private Task CoordinatePollingAsync(CancellationToken ct) => Task.CompletedTask;` stub with:

```csharp
    // MARK: - Centralized Polling

    private async Task CoordinatePollingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sleepDuration = NextEventDelay();
            if (sleepDuration > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(sleepDuration, _time, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await PollCycleAsync(ct);

            // Once any game is in active polling range, switch to the 10s loop.
            var hasActiveGames = _monitoredGames.Values.Any(
                game => game.StartTime - ActiveWindow <= _time.GetUtcNow());

            if (hasActiveGames)
            {
                try
                {
                    await Task.Delay(PollInterval, _time, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task PollCycleAsync(CancellationToken ct)
    {
        var gamesToPoll = SelectGamesToPoll();
        if (gamesToPoll.Count == 0) return;

        await Task.WhenAll(gamesToPoll
            .Where(_monitoredGames.ContainsKey)
            .Select(gamePk => PollSingleGameAsync(gamePk, _monitoredGames[gamePk], ct)));
    }

    /// <summary>
    /// Games due for a poll this cycle: everything inside the active window, plus any game
    /// that has just crossed an uncompleted pre-game milestone. One cycle consumes at most
    /// one milestone per game.
    /// </summary>
    internal IReadOnlyList<int> SelectGamesToPoll()
    {
        var now = _time.GetUtcNow();
        var gamesToPoll = new List<int>();

        // Active games: within 15 min of start - poll every cycle.
        foreach (var (gamePk, game) in _monitoredGames)
        {
            if (game.StartTime - ActiveWindow <= now) gamesToPoll.Add(gamePk);
        }

        // Pre-game milestone checks: one-shot fetch when a milestone is reached.
        foreach (var (gamePk, game) in _monitoredGames)
        {
            var timeUntilStart = game.StartTime - now;
            if (timeUntilStart <= ActiveWindow) continue;   // already active

            foreach (var milestone in PreGameMilestones)
            {
                if (timeUntilStart > milestone) continue;

                if (!_completedMilestones.TryGetValue(gamePk, out var completed))
                {
                    completed = [];
                    _completedMilestones[gamePk] = completed;
                }

                if (!completed.Add(milestone)) continue;

                gamesToPoll.Add(gamePk);
                break;
            }
        }

        return gamesToPoll;
    }

    /// <summary>
    /// Time until the next event (milestone or active polling window).
    /// <see cref="TimeSpan.Zero"/> when an event is ready now.
    /// </summary>
    internal TimeSpan NextEventDelay()
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? nextTime = null;

        foreach (var game in _monitoredGames.Values)
        {
            // Active polling starts 15 min before the game.
            var activeStart = game.StartTime - ActiveWindow;
            if (activeStart <= now) return TimeSpan.Zero;

            _completedMilestones.TryGetValue(game.Id, out var completed);

            foreach (var milestone in PreGameMilestones)
            {
                var milestoneTime = game.StartTime - milestone;
                if (milestoneTime <= now && !(completed?.Contains(milestone) ?? false)) return TimeSpan.Zero;
                if (milestoneTime > now && (nextTime is null || milestoneTime < nextTime)) nextTime = milestoneTime;
            }

            if (nextTime is null || activeStart < nextTime) nextTime = activeStart;
        }

        if (nextTime is not { } next) return TimeSpan.Zero;

        var delay = next - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    // Task 7 adds PollSingleGameAsync.
    private Task PollSingleGameAsync(int gamePk, Game game, CancellationToken ct) => Task.CompletedTask;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~GameMonitorSchedulingTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "phase 4: GameMonitor event scheduling and pre-game milestones"
```

---

## Task 7: GameMonitor — poll cycle

**Files:**
- Modify: `src/OnDeck.Core/Managers/GameMonitor.cs`
- Create: `tests/OnDeck.Core.Tests/Managers/GameMonitorPollingTests.cs`

**Spec:** `GameMonitor.swift:206-261`

**Interfaces:**
- Produces: `internal Task PollSingleGameAsync(int gamePk, Game game, CancellationToken ct)`.

**Poll dispatch:** with a cached feed **and** a non-null `TimeStamp`, call `FetchDiffPatchAsync`:
`NoChanges` → return without touching state; `Patches` → `LiveFeedPatcher.Apply` onto the cached feed and store the result; `FullUpdate` → decode and replace. Without a cached feed or timecode, do a full `FetchLiveFeedAsync`.

**After processing:** when `GameState == "Final"`, branch on `DetailedState`:
- `"Postponed"` → **do not** mark players; just `StopMonitoring(gamePk)`. Postponed carries `Final` with no stats, and marking players `GameOver` would filter them out of the UI entirely (Done requires a stat line). They stay `.Upcoming` so the PPD label shows.
- otherwise → `StateManager.SetGameOver` for every roster player in that game, then `StopMonitoring(gamePk)`.

**On exception:** null `LatestFeeds[gamePk].TimeStamp` (preserving the rest for UI continuity) so the next cycle does a full fetch. Never rethrow.

**`IsPlayerInGame`:** two-way substring containment between the player's team and either game team — the same rule as `Game.SideFor`.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Managers/GameMonitorPollingTests.cs`:

```csharp
using System.Net;
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Tests.Fixtures;
using static OnDeck.Core.Tests.Managers.GameMonitorLifecycleTests;

namespace OnDeck.Core.Tests.Managers;

public class GameMonitorPollingTests
{
    private static string FeedWith(string gameState, string detailedState, string timeStamp = "20260808_140000") => $$"""
    {
      "metaData": {"timeStamp": "{{timeStamp}}"},
      "gameData": {
        "status": {"abstractGameState": "{{gameState}}", "detailedState": "{{detailedState}}"},
        "teams": {"away": {"id": 137, "name": "San Francisco Giants"},
                  "home": {"id": 119, "name": "Los Angeles Dodgers"}}
      },
      "liveData": {}
    }
    """;

    [Fact]
    public async Task PollSingleGameAsync_FullFetchesWhenThereIsNoSeed()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);

        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Contains("/feed/live", handler.LastUri!.AbsoluteUri);
        Assert.DoesNotContain("diffPatch", handler.LastUri.AbsoluteUri);
        Assert.Equal("20260808_140000", monitor.LatestFeeds[1].TimeStamp);
    }

    [Fact]
    public async Task PollSingleGameAsync_UsesDiffPatchOnceSeeded()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        handler.EnqueueJson("""[{"diff": [{"op": "replace", "path": "/liveData/linescore/teams/home/runs", "value": 4}]}]""");
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Contains("diffPatch?startTimecode=20260808_140000", handler.LastUri!.AbsoluteUri);
        Assert.Equal(4, monitor.LatestFeeds[1].HomeScore);
    }

    [Fact]
    public async Task PollSingleGameAsync_NoChangesLeavesTheFeedAlone()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);
        var before = monitor.LatestFeeds[1].Clone();

        handler.EnqueueJson("[]");
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Equal(before, monitor.LatestFeeds[1]);
    }

    [Fact]
    public async Task PollSingleGameAsync_FullUpdateReplacesTheFeed()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        handler.EnqueueJson(FeedWith("Live", "In Progress", timeStamp: "20260808_141000"));
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Equal("20260808_141000", monitor.LatestFeeds[1].TimeStamp);
    }

    [Fact]
    public async Task PollSingleGameAsync_NullsTimestampOnTransientError()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Null(monitor.LatestFeeds[1].TimeStamp);
        Assert.Equal("Live", monitor.LatestFeeds[1].GameState);   // rest preserved for the UI
    }

    [Fact]
    public async Task PollSingleGameAsync_FinalMarksRosterPlayersGameOverAndStops()
    {
        var (monitor, _, handler) = Create();
        var states = new StateManager();
        monitor.Configure(states);
        monitor.StartMonitoring([GameAt(1, Now)], [Hitter(10), Hitter(11, "Boston Red Sox")]);

        handler.EnqueueJson(FeedWith("Final", "Final"));
        await monitor.PollSingleGameAsync(1, GameAt(1, Now), CancellationToken.None);

        var inactive = Assert.IsType<PlayerState.Inactive>(states.PlayerStates[10]);
        Assert.Equal(1, Assert.IsType<PlayerState.InactiveReason.GameOver>(inactive.Reason).GamePk);
        Assert.False(states.PlayerStates.ContainsKey(11));   // not in this game
        Assert.False(monitor.IsMonitoring);
    }

    [Fact]
    public async Task PollSingleGameAsync_PostponedStopsPollingWithoutMarkingPlayers()
    {
        // Postponed carries Final with no stats; marking players gameOver would filter them
        // out of the UI entirely, so they stay .upcoming and keep the PPD label.
        var (monitor, _, handler) = Create();
        var states = new StateManager();
        monitor.Configure(states);
        monitor.StartMonitoring([GameAt(1, Now)], [Hitter(10)]);

        handler.EnqueueJson(FeedWith("Final", "Postponed"));
        await monitor.PollSingleGameAsync(1, GameAt(1, Now), CancellationToken.None);

        Assert.Empty(states.PlayerStates);
        Assert.False(monitor.IsMonitoring);
        Assert.True(monitor.LatestFeeds.ContainsKey(1));   // feed retained
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~GameMonitorPollingTests`
Expected: FAIL — the stub `PollSingleGameAsync` does nothing.

- [ ] **Step 3: Replace the stub**

Swap `private Task PollSingleGameAsync(...) => Task.CompletedTask;` for:

```csharp
    internal async Task PollSingleGameAsync(int gamePk, Game game, CancellationToken ct)
    {
        try
        {
            LiveFeedData feed;

            if (LatestFeeds.TryGetValue(gamePk, out var existing) && existing.TimeStamp is { } timecode)
            {
                var result = await mlb.FetchDiffPatchAsync(gamePk, timecode, ct);

                switch (result)
                {
                    case DiffPatchResult.NoChanges:
                        return;

                    case DiffPatchResult.Patches patches:
                        feed = LiveFeedPatcher.Apply(patches.Operations, existing);
                        LatestFeeds[gamePk] = feed;
                        break;

                    case DiffPatchResult.FullUpdate full:
                        feed = LiveFeedDecoder.Decode(full.Json);
                        LatestFeeds[gamePk] = feed;
                        break;

                    default:
                        return;
                }
            }
            else
            {
                // No seed - full fetch.
                feed = await mlb.FetchLiveFeedAsync(gamePk, ct);
                LatestFeeds[gamePk] = feed;
            }

            ProcessFeed(feed, gamePk, game);

            if (feed.GameState != "Final") return;

            // Postponed carries gameState "Final" but has no stats - marking players
            // gameOver would filter them out of the UI entirely (the Done section requires
            // a stat line). Leave them in .upcoming so the UPCOMING row's red X icon and
            // "PPD" label stay visible until the next day's refresh.
            if (feed.DetailedState == "Postponed")
            {
                StopMonitoring(gamePk);
                return;
            }

            var playerIdsInGame = _rosterPlayerIds.Where(id => IsPlayerInGame(id, game)).ToArray();
            _stateManager?.SetGameOver(playerIdsInGame, gamePk);
            StopMonitoring(gamePk);
        }
        catch (Exception)
        {
            // Transient error - preserve the last-known feed for UI continuity, but null its
            // timestamp so the next cycle does a full fetch.
            if (LatestFeeds.TryGetValue(gamePk, out var stale)) stale.TimeStamp = null;
        }
    }

    // MARK: - Helpers

    private bool IsPlayerInGame(int playerId, Game game)
    {
        if (!_rosterPlayers.TryGetValue(playerId, out var player)) return false;

        return game.HomeTeam.Contains(player.Team, StringComparison.Ordinal)
            || game.AwayTeam.Contains(player.Team, StringComparison.Ordinal)
            || player.Team.Contains(game.HomeTeam, StringComparison.Ordinal)
            || player.Team.Contains(game.AwayTeam, StringComparison.Ordinal);
    }

    // Task 8 adds ProcessFeed.
    private void ProcessFeed(LiveFeedData feed, int gamePk, Game game) { }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~GameMonitorPollingTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "phase 4: GameMonitor poll cycle with diffPatch and Final handling"
```

---

## Task 8: GameMonitor — feed processing

**Files:**
- Modify: `src/OnDeck.Core/Managers/GameMonitor.cs`
- Create: `tests/OnDeck.Core.Tests/Managers/GameMonitorFeedTests.cs`

**Spec:** `GameMonitor.swift:265-444, 485-489`

**Interfaces:**
- Produces: the real `ProcessFeed(LiveFeedData feed, int gamePk, Game game)`.

**Processing order — do not reorder:**

1. **Lineup tracking.** Home/away batters come from the batting orders; pitchers from the boxscore pitcher lists **plus** the game's probable pitcher. An empty side keeps the existing value. When the merged `GameLineup` differs from the stored one, store it and fire `OnLineupUpdate(gamePk)`.
2. **Playability gate.** Bail unless `GameState == "Live"` **and** `DetailedState` is `"In Progress"`, starts with `"Delayed"`, starts with `"Suspended"`, or equals `"Manager challenge"`.
3. **First-live detection.** Adding to `_liveGamesSeen` for the first time fires `OnGameStart(gamePk)`.
4. **Break detection.** `isBreak` when not `In Progress`, or `InningState` is `"Middle"` or `"End"`. Between half-innings the feed's current batter/pitcher are stale holdovers, and mid-game delays pause play — so on a break, flip every roster player *currently active in this game* to `Upcoming`, leaving substituted players alone.
5. **Otherwise:** a current batter who is a rostered **hitter** becomes `Active(Batting)`; a current pitcher who is a rostered **pitcher** becomes `Active(Pitching)`.
6. **Previous batter revert.** If the last batter changed and was a rostered **hitter**, flip them to `Upcoming`. Pitcher-only roster players (Ohtani-P) can appear as the feed's batter without being tracked, hence the hitter check.
7. **Pitcher side tracking.** A changed pitcher on a side substitutes the previous one out (`Inactive(Substituted)`) when they're on the roster.
8. **Previous pitcher revert.** If the last pitcher changed and is rostered, flip to `Upcoming` **unless** already `Inactive(Substituted)`.
9. **Substitution catch-all.** For each side, the boxscore pitcher list is ordered by appearance, so the last entry is current. Any roster pitcher who appears in that list, isn't the last, has a formatted pitching line, and is pitcher-but-not-hitter gets `Inactive(Substituted)` — unless already substituted or currently active. Handles app restarts and missed transitions.
10. **Result capture.** When `IsPlayComplete` and there's a description, store it under the current batter and pitcher if they're on the roster.
11. **Record `_lastBatterId` / `_lastPitcherId`.**

**`FormatInning`:** `""` unless both `Inning` and `InningHalf` are present; otherwise `"Top {n}"` or `"Bot {n}"`.

**Team short names** for the context are the **last space-separated word** of each team name.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Managers/GameMonitorFeedTests.cs`:

```csharp
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using static OnDeck.Core.Tests.Managers.GameMonitorLifecycleTests;

namespace OnDeck.Core.Tests.Managers;

public class GameMonitorFeedTests
{
    private static Player Pitcher(int id, string team = "Los Angeles Dodgers") =>
        new(id, $"Pitcher {id}", team,
            new HashSet<PlayerPosition> { PlayerPosition.Pitcher },
            new HashSet<string> { "SP" },
            RosterStatus.Active);

    private static LiveFeedData LiveFeed(
        string detailedState = "In Progress",
        int? batter = null,
        int? pitcher = null,
        string? inningState = null,
        int inning = 3,
        string half = "Top") => new()
    {
        GameState = "Live",
        DetailedState = detailedState,
        CurrentBatterId = batter,
        CurrentPitcherId = pitcher,
        Inning = inning,
        InningHalf = half,
        InningState = inningState,
        HomeTeam = "Los Angeles Dodgers",
        AwayTeam = "San Francisco Giants",
        HomeTeamId = 119,
        AwayTeamId = 137,
    };

    private static (GameMonitor Monitor, StateManager States) Started(params Player[] players)
    {
        var (monitor, _, _) = Create();
        var states = new StateManager();
        monitor.Configure(states);
        monitor.StartMonitoring([GameAt(1, Now)], players);
        return (monitor, states);
    }

    [Fact]
    public void ProcessFeed_TracksLineupsPerSideAndFiresTheCallback()
    {
        var (monitor, _) = Started(Hitter(10));
        var fired = new List<int>();
        monitor.OnLineupUpdate = fired.Add;

        var feed = LiveFeed();
        feed.HomeBattingOrder = [10, 11];
        feed.AwayBattingOrder = [20];
        monitor.ProcessFeed(feed, 1, GameAt(1, Now, homePitcher: 30));

        var lineup = monitor.LineupPlayerIds[1];
        Assert.Equal(new HashSet<int> { 10, 11 }, lineup.Home);
        Assert.Equal(new HashSet<int> { 20 }, lineup.Away);
        Assert.Contains(30, lineup.HomePitchers);       // probable pitcher folded in
        Assert.Equal([1], fired);
    }

    [Fact]
    public void ProcessFeed_EmptyLineupSideDoesNotOverwriteWhatWeHad()
    {
        var (monitor, _) = Started(Hitter(10));
        var first = LiveFeed();
        first.HomeBattingOrder = [10, 11];
        monitor.ProcessFeed(first, 1, GameAt(1, Now));

        var second = LiveFeed();
        second.HomeBattingOrder = [];
        monitor.ProcessFeed(second, 1, GameAt(1, Now));

        Assert.Equal(new HashSet<int> { 10, 11 }, monitor.LineupPlayerIds[1].Home);
    }

    [Fact]
    public void ProcessFeed_DoesNotFireLineupUpdateWhenNothingChanged()
    {
        var (monitor, _) = Started(Hitter(10));
        var fired = 0;
        monitor.OnLineupUpdate = _ => fired++;

        var feed = LiveFeed();
        feed.HomeBattingOrder = [10];
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.Equal(1, fired);
    }

    [Theory]
    [InlineData("In Progress", true)]
    [InlineData("Delayed: Rain", true)]
    [InlineData("Suspended: Rain", true)]
    [InlineData("Manager challenge", true)]
    [InlineData("Warmup", false)]
    [InlineData("Game Over", false)]
    [InlineData("Pre-Game", false)]
    public void ProcessFeed_OnlyPlayableStatesCountAsLive(string detailedState, bool expected)
    {
        var (monitor, _) = Started(Hitter(10));

        monitor.ProcessFeed(LiveFeed(detailedState, batter: 10), 1, GameAt(1, Now));

        Assert.Equal(expected, monitor.IsLive(1));
    }

    [Fact]
    public void ProcessFeed_FiresGameStartOnceOnly()
    {
        var (monitor, _) = Started(Hitter(10));
        var fired = new List<int>();
        monitor.OnGameStart = fired.Add;

        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));
        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        Assert.Equal([1], fired);
    }

    [Fact]
    public void ProcessFeed_MarksRosteredBatterActive()
    {
        var (monitor, states) = Started(Hitter(10));

        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        var active = Assert.IsType<PlayerState.Active>(states.PlayerStates[10]);
        Assert.Equal(PlayerState.ActiveRole.Batting, active.Context.Role);
        Assert.Equal("Top 3", active.Context.Inning);
        Assert.Equal("Dodgers", active.Context.HomeTeam);
        Assert.Equal("Giants", active.Context.AwayTeam);
    }

    [Fact]
    public void ProcessFeed_MarksRosteredPitcherActive()
    {
        var (monitor, states) = Started(Pitcher(30));

        monitor.ProcessFeed(LiveFeed(pitcher: 30), 1, GameAt(1, Now));

        var active = Assert.IsType<PlayerState.Active>(states.PlayerStates[30]);
        Assert.Equal(PlayerState.ActiveRole.Pitching, active.Context.Role);
    }

    [Fact]
    public void ProcessFeed_IgnoresPitcherOnlyPlayersAppearingAsBatter()
    {
        var (monitor, states) = Started(Pitcher(30));

        monitor.ProcessFeed(LiveFeed(batter: 30), 1, GameAt(1, Now));

        Assert.False(states.PlayerStates.ContainsKey(30));
    }

    [Fact]
    public void ProcessFeed_FlipsActivePlayersToUpcomingBetweenHalfInnings()
    {
        var (monitor, states) = Started(Hitter(10));
        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        monitor.ProcessFeed(LiveFeed(batter: 10, inningState: "Middle"), 1, GameAt(1, Now));

        Assert.IsType<PlayerState.Upcoming>(states.PlayerStates[10]);
    }

    [Fact]
    public void ProcessFeed_FlipsActivePlayersToUpcomingDuringADelay()
    {
        var (monitor, states) = Started(Hitter(10));
        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        monitor.ProcessFeed(LiveFeed("Delayed: Rain", batter: 10), 1, GameAt(1, Now));

        Assert.IsType<PlayerState.Upcoming>(states.PlayerStates[10]);
    }

    [Fact]
    public void ProcessFeed_RevertsThePreviousBatterWhenTheAtBatEnds()
    {
        var (monitor, states) = Started(Hitter(10), Hitter(11));
        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        monitor.ProcessFeed(LiveFeed(batter: 11), 1, GameAt(1, Now));

        Assert.IsType<PlayerState.Upcoming>(states.PlayerStates[10]);
        Assert.IsType<PlayerState.Active>(states.PlayerStates[11]);
    }

    [Fact]
    public void ProcessFeed_SubstitutesTheOutgoingPitcherOnASide()
    {
        var (monitor, states) = Started(Pitcher(30), Pitcher(31));

        var first = LiveFeed(pitcher: 30);
        first.HomePitchers = [30];
        monitor.ProcessFeed(first, 1, GameAt(1, Now));

        var second = LiveFeed(pitcher: 31);
        second.HomePitchers = [30, 31];
        monitor.ProcessFeed(second, 1, GameAt(1, Now));

        var inactive = Assert.IsType<PlayerState.Inactive>(states.PlayerStates[30]);
        Assert.Equal(1, Assert.IsType<PlayerState.InactiveReason.Substituted>(inactive.Reason).GamePk);
    }

    [Fact]
    public void ProcessFeed_CatchAllSubstitutesEarlierPitchersWithAStatLine()
    {
        // Handles app restarts and missed transitions: pitchers are ordered by appearance,
        // so anyone before the last entry who actually pitched has been substituted.
        var (monitor, states) = Started(Pitcher(30), Pitcher(31));

        var feed = LiveFeed(pitcher: 31);
        feed.HomePitchers = [30, 31];
        feed.PlayerStats[30] = new PlayerGameStats
        {
            Pitching = new PlayerPitchingStats { InningsPitched = "5.0" },
        };
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        var inactive = Assert.IsType<PlayerState.Inactive>(states.PlayerStates[30]);
        Assert.IsType<PlayerState.InactiveReason.Substituted>(inactive.Reason);
    }

    [Fact]
    public void ProcessFeed_CatchAllSkipsPitchersWithoutAStatLine()
    {
        var (monitor, states) = Started(Pitcher(30), Pitcher(31));

        var feed = LiveFeed(pitcher: 31);
        feed.HomePitchers = [30, 31];
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.False(states.PlayerStates.ContainsKey(30));
    }

    [Fact]
    public void ProcessFeed_StoresCompletedPlayDescriptionsForRosterPlayers()
    {
        var (monitor, _) = Started(Hitter(10), Pitcher(30));

        var feed = LiveFeed(batter: 10, pitcher: 30);
        feed.IsPlayComplete = true;
        feed.LastPlayDescription = "Player 10 singles on a line drive.";
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.Equal("Player 10 singles on a line drive.", monitor.LastPlayDescriptions[10]);
        Assert.Equal("Player 10 singles on a line drive.", monitor.LastPlayDescriptions[30]);
    }

    [Fact]
    public void ProcessFeed_IgnoresIncompletePlays()
    {
        var (monitor, _) = Started(Hitter(10));

        var feed = LiveFeed(batter: 10);
        feed.IsPlayComplete = false;
        feed.LastPlayDescription = "in progress";
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.Empty(monitor.LastPlayDescriptions);
    }

    [Fact]
    public void ProcessFeed_FormatsInningAsTopOrBot()
    {
        var (monitor, states) = Started(Hitter(10));

        monitor.ProcessFeed(LiveFeed(batter: 10, inning: 7, half: "Bottom"), 1, GameAt(1, Now));

        Assert.Equal("Bot 7", Assert.IsType<PlayerState.Active>(states.PlayerStates[10]).Context.Inning);
    }

    [Fact]
    public void ProcessFeed_LeavesInningBlankWhenTheFeedHasNoInning()
    {
        var (monitor, states) = Started(Hitter(10));

        var feed = LiveFeed(batter: 10);
        feed.Inning = null;
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.Equal("", Assert.IsType<PlayerState.Active>(states.PlayerStates[10]).Context.Inning);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~GameMonitorFeedTests`
Expected: FAIL — `ProcessFeed` is an empty stub.

- [ ] **Step 3: Replace the `ProcessFeed` stub**

```csharp
    // MARK: - Feed Processing

    internal void ProcessFeed(LiveFeedData feed, int gamePk, Game game)
    {
        // Track lineup per side. Only overwrite a batting side when the feed actually has
        // data for it - an empty side means that team hasn't filed its lineup card yet, not
        // that we should drop what we had. Pitchers live in a separate set so that a filed
        // batting card (used to gate "not in lineup" logic) doesn't falsely flag the
        // probable starter as missing before the boxscore lists him.
        var homeBatters = feed.HomeBattingOrder.ToHashSet();
        var awayBatters = feed.AwayBattingOrder.ToHashSet();

        var homePitchers = feed.HomePitchers.ToHashSet();
        if (game.HomeProbablePitcherId is { } homeProbable) homePitchers.Add(homeProbable);

        var awayPitchers = feed.AwayPitchers.ToHashSet();
        if (game.AwayProbablePitcherId is { } awayProbable) awayPitchers.Add(awayProbable);

        var existing = LineupPlayerIds.TryGetValue(gamePk, out var stored) ? stored : new GameLineup();
        var updated = new GameLineup
        {
            Home = homeBatters.Count == 0 ? existing.Home : homeBatters,
            Away = awayBatters.Count == 0 ? existing.Away : awayBatters,
            HomePitchers = homePitchers.Count == 0 ? existing.HomePitchers : homePitchers,
            AwayPitchers = awayPitchers.Count == 0 ? existing.AwayPitchers : awayPitchers,
        };

        if (!updated.Equals(existing))
        {
            LineupPlayerIds[gamePk] = updated;
            OnLineupUpdate?.Invoke(gamePk);
        }

        // Allowlist of detailedStates that count as "ball in play or paused mid-play".
        // abstractGameState "Live" alone isn't enough - it also covers "Warmup" (~30 min
        // pre-first-pitch) and briefly "Game Over" before the flip to Final. Pre-game
        // "Delayed Start: Rain" carries abstractGameState "Preview", so the "Delayed" prefix
        // here only matches the mid-game "Delayed: Rain" form - don't tighten it without
        // re-checking, or rain delay detection breaks.
        var detailed = feed.DetailedState ?? "";
        var isPlayable = detailed == "In Progress"
                         || detailed.StartsWith("Delayed", StringComparison.Ordinal)
                         || detailed.StartsWith("Suspended", StringComparison.Ordinal)
                         || detailed == "Manager challenge";

        if (feed.GameState != "Live" || !isPlayable) return;

        if (_liveGamesSeen.Add(gamePk)) OnGameStart?.Invoke(gamePk);

        // Between half-innings, currentBatter/currentPitcher are stale holdovers from the last
        // play of the previous half-inning - MLB doesn't clear them until play resumes.
        // Mid-game delays (rain etc.) also pause play, so flip active players out.
        var isInProgress = feed.DetailedState == "In Progress";
        var isBreak = !isInProgress || feed.InningState == "Middle" || feed.InningState == "End";

        var awayShort = LastWord(game.AwayTeam);
        var homeShort = LastWord(game.HomeTeam);
        var inning = FormatInning(feed);

        PlayerState.GameContext MakeContext(PlayerState.ActiveRole role) => new(
            gamePk, role, inning,
            homeShort, awayShort,
            feed.HomeTeamId, feed.AwayTeamId,
            feed.HomeScore, feed.AwayScore,
            feed.Balls, feed.Strikes, feed.Outs,
            feed.RunnerOnFirst is not null, feed.RunnerOnSecond is not null, feed.RunnerOnThird is not null);

        if (isBreak)
        {
            // Flip any roster player currently active in this game to upcoming. Leaves
            // substituted players alone (they're inactive, not active).
            foreach (var id in _rosterPlayerIds)
            {
                if (_stateManager?.PlayerStates.GetValueOrDefault(id) is not PlayerState.Active active) continue;
                if (active.Context.GamePk != gamePk) continue;
                _stateManager.Update(id, new PlayerState.Upcoming(game.StartTime));
            }
        }
        else
        {
            // Check current batter - only track if rostered as a hitter.
            if (feed.CurrentBatterId is { } batterId
                && _rosterPlayers.TryGetValue(batterId, out var batter)
                && batter.IsHitter)
            {
                _stateManager?.Update(batterId, new PlayerState.Active(MakeContext(PlayerState.ActiveRole.Batting)));
            }

            // Check current pitcher - only track if rostered as a pitcher.
            if (feed.CurrentPitcherId is { } activePitcherId
                && _rosterPlayers.TryGetValue(activePitcherId, out var activePitcher)
                && activePitcher.IsPitcher)
            {
                _stateManager?.Update(
                    activePitcherId, new PlayerState.Active(MakeContext(PlayerState.ActiveRole.Pitching)));
            }
        }

        // Check if the previous batter from our roster is no longer active. Only revert if
        // they were actually a hitter - pitcher-only roster players (e.g. Ohtani-P) can appear
        // as the feed's current batter without being tracked.
        if (_lastBatterId.GetValueOrDefault(gamePk) is { } prevBatter
            && prevBatter != feed.CurrentBatterId
            && _rosterPlayers.TryGetValue(prevBatter, out var prevBatterPlayer)
            && prevBatterPlayer.IsHitter)
        {
            _stateManager?.Update(prevBatter, new PlayerState.Upcoming(game.StartTime));
        }

        // Track pitcher per team side and detect substitutions.
        if (feed.CurrentPitcherId is { } pitcherId)
        {
            var isHome = feed.HomePitchers.Contains(pitcherId);
            var sideMap = isHome ? _lastHomePitcherId : _lastAwayPitcherId;

            if (sideMap.TryGetValue(gamePk, out var prev)
                && prev != pitcherId
                && _rosterPlayerIds.Contains(prev))
            {
                _stateManager?.Update(
                    prev, new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(gamePk)));
            }

            sideMap[gamePk] = pitcherId;
        }

        // Revert pitcher to in-game when the half-inning changes (they're not on the mound).
        if (_lastPitcherId.GetValueOrDefault(gamePk) is { } prevPitcher
            && prevPitcher != feed.CurrentPitcherId
            && _rosterPlayerIds.Contains(prevPitcher))
        {
            var currentState = _stateManager?.PlayerStates.GetValueOrDefault(prevPitcher);
            if (currentState is not PlayerState.Inactive { Reason: PlayerState.InactiveReason.Substituted })
            {
                _stateManager?.Update(prevPitcher, new PlayerState.Upcoming(game.StartTime));
            }
        }

        // Catch-all: check both sides using the last pitcher in each pitchers array (boxscore
        // pitchers are ordered by appearance, last = current for that side). Any roster pitcher
        // who pitched earlier but isn't the latest for their side has been substituted.
        // Handles app restarts and missed transitions.
        foreach (var pitchers in new[] { feed.HomePitchers, feed.AwayPitchers })
        {
            if (pitchers.Count == 0) continue;
            var currentForSide = pitchers[^1];

            foreach (var id in _rosterPlayerIds)
            {
                if (id == currentForSide) continue;
                if (!pitchers.Contains(id)) continue;
                if (feed.PlayerStats.GetValueOrDefault(id)?.Pitching?.Formatted is null) continue;
                if (!_rosterPlayers.TryGetValue(id, out var player)) continue;
                if (!player.IsPitcher || player.IsHitter) continue;

                var currentState = _stateManager?.PlayerStates.GetValueOrDefault(id);
                if (currentState is PlayerState.Inactive { Reason: PlayerState.InactiveReason.Substituted }) continue;
                if (currentState is PlayerState.Active) continue;

                _stateManager?.Update(
                    id, new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(gamePk)));
            }
        }

        // Store completed play results for notifications.
        if (feed.IsPlayComplete && feed.LastPlayDescription is { } description)
        {
            if (feed.CurrentBatterId is { } completedBatter && _rosterPlayerIds.Contains(completedBatter))
            {
                LastPlayDescriptions[completedBatter] = description;
            }

            if (feed.CurrentPitcherId is { } completedPitcher && _rosterPlayerIds.Contains(completedPitcher))
            {
                LastPlayDescriptions[completedPitcher] = description;
            }
        }

        _lastBatterId[gamePk] = feed.CurrentBatterId;
        _lastPitcherId[gamePk] = feed.CurrentPitcherId;
    }

    private static string LastWord(string teamName)
    {
        var words = teamName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[^1] : teamName;
    }

    private static string FormatInning(LiveFeedData feed)
    {
        if (feed.Inning is not { } inning || feed.InningHalf is not { } half) return "";

        var shortHalf = half == "Top" ? "Top" : "Bot";
        return $"{shortHalf} {inning}";
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~GameMonitorFeedTests`
Expected: PASS.

- [ ] **Step 5: Run the whole suite and the publish check**

```bash
dotnet test
dotnet publish src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Expected: all tests pass; publish succeeds.

- [ ] **Step 6: Commit**

```bash
git add windows/
git commit -m "phase 4: GameMonitor feed processing and state transitions"
```

---

## Done criteria

- `dotnet build` and `dotnet test` green; single-file publish still produces `OnDeck.App.exe`.
- `OnDeck.Core` still has zero package references.
- All eight load-bearing behaviours listed at the top have a dedicated passing test.
- `AppOrchestrator` (Phase 5) is the only Core file left.
