# Phase 3: API Clients — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the two network clients — `MlbStatsApi` (player search, schedule with lineups, live feed, diffPatch, game changes) and `FantraxApi` (teams, roster with period detection) — as `HttpClient`-based classes testable through an injected `HttpMessageHandler`.

**Architecture:** Both clients take an `HttpClient` in their constructor; nothing in Core constructs one implicitly, so every test drives them with canned JSON and asserts the exact request URL and body. `MlbStatsApi` also takes a `TimeProvider` because the diffPatch `endTimecode` is "now". Response DTOs use `System.Text.Json`; the diffPatch response is walked as raw `JsonElement` because its shape is polymorphic (array of patch batches, or a bare feed object).

**Tech Stack:** .NET 10, `HttpClient` + `System.Text.Json`, `TimeProvider`, xunit.

## Global Constraints

- `OnDeck.Core` must have **zero** Windows-specific dependencies.
- No `ConfigureAwait(false)` anywhere in Core — the single-logical-thread model requires continuations return to the captured context.
- Single-file publish must stay green.
- Mirror Swift names 1:1 where possible.
- Preserve the diffPatch dict-fallback, the Fantrax `period` semantics, and the `statusId` meanings (1=active, 2=reserve, 3=IR, 9=minors).
- Commands run from `windows/`.

## Scope notes

- **`LiveFeedData` and the `feed/live` decoder already exist** (pulled forward into Phase 2). This phase adds the transport around them.
- **`FantraxAPI.findScorers` is not ported.** It is dead code in the Swift original — `fetchRoster` uses the direct `responses[0].data.tables[].rows[].scorer` walk, and nothing else references `findScorers`. Porting it would carry over an unused recursive walker that also hardcodes `statusId: 1`.
- **`ScheduleManager.baseballDate()` is pulled forward** into `Core/Utilities/BaseballCalendar.cs`. `FantraxAPI.findTodayPeriod` depends on it, so it cannot wait for Phase 4; `ScheduleManager` will consume the same helper.
- `MemoryStats`-driven session tuning (`urlCache = nil`, `httpMaximumConnectionsPerHost = 2`) becomes a `CreateDefaultClient()` factory: .NET's `HttpClient` does not cache responses by default, so only the connection cap carries over.

## Swift → C# mapping decisions

| Swift | C# | Why |
|---|---|---|
| `URLSession` static, `urlCache = nil` | injected `HttpClient`; `MlbStatsApi.CreateDefaultClient()` sets `MaxConnectionsPerServer = 2` | Testability; .NET has no response cache to disable |
| `DiffPatchResult` enum | `abstract record DiffPatchResult` with `NoChanges`, `Patches`, `FullUpdate` | Closed hierarchy, same three cases |
| `.fullUpdate(Data)` | `FullUpdate(byte[] Json)` | Caller re-decodes via `LiveFeedDecoder` |
| `[[String: Any]]` patches | `IReadOnlyList<PatchOperation>` | Already typed in Phase 2 |
| `FantraxError` enum | `FantraxException` + `FantraxErrorKind` | Exceptions are the .NET idiom for `throws` |
| `Calendar.current` hour check | `TimeProvider.GetLocalNow()` | Same local-time semantics, testable |
| `Date.now` for timecode | `TimeProvider.GetUtcNow()` | Deterministic tests |

## File Structure

| File | Responsibility |
|---|---|
| `src/OnDeck.Core/Utilities/BaseballCalendar.cs` | "Baseball day" — before 8 AM local counts as yesterday |
| `src/OnDeck.Core/Networking/MlbStatsApi.cs` | Player search, schedule, live feed, diffPatch, game changes |
| `src/OnDeck.Core/Networking/DiffPatchResult.cs` | Three-case result of a diffPatch request |
| `src/OnDeck.Core/Networking/FantraxApi.cs` | Teams + roster over the `fxpa/req` POST endpoint |
| `src/OnDeck.Core/Networking/FantraxModels.cs` | `FantraxTeam`, `FantraxPlayer`, `FantraxException` |
| `tests/OnDeck.Core.Tests/Networking/StubHttpMessageHandler.cs` | Records requests, replays canned responses |
| `tests/OnDeck.Core.Tests/Networking/*Tests.cs`, `Utilities/BaseballCalendarTests.cs` | One test file per concern |

---

## Task 1: BaseballCalendar and the HTTP stub

**Files:**
- Create: `src/OnDeck.Core/Utilities/BaseballCalendar.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/BaseballCalendarTests.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/StubHttpMessageHandler.cs`

**Spec:** `Managers/ScheduleManager.swift:25-33`

**Interfaces:**
- Produces:
  - `static class BaseballCalendar` with `DateTimeOffset Today(TimeProvider timeProvider)` — returns local now, minus one day when the local hour is < 8.
  - `sealed class StubHttpMessageHandler : HttpMessageHandler` with
    `List<HttpRequestMessage> Requests`, `List<string> RequestBodies`,
    `void EnqueueJson(string json)`, `void EnqueueStatus(HttpStatusCode status)`,
    and round-robin replay of the last response once the queue drains.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Utilities/BaseballCalendarTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class BaseballCalendarTests
{
    private static FakeTimeProvider At(int year, int month, int day, int hour)
    {
        var provider = new FakeTimeProvider(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
        provider.SetLocalTimeZone(TimeZoneInfo.Utc);
        return provider;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void Today_BeforeEightAmIsYesterday(int hour)
    {
        var today = BaseballCalendar.Today(At(2026, 8, 8, hour));
        Assert.Equal(new DateOnly(2026, 8, 7), DateOnly.FromDateTime(today.Date));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(23)]
    public void Today_FromEightAmIsToday(int hour)
    {
        var today = BaseballCalendar.Today(At(2026, 8, 8, hour));
        Assert.Equal(new DateOnly(2026, 8, 8), DateOnly.FromDateTime(today.Date));
    }

    [Fact]
    public void Today_RollsBackAcrossMonthBoundary()
    {
        var today = BaseballCalendar.Today(At(2026, 8, 1, 2));
        Assert.Equal(new DateOnly(2026, 7, 31), DateOnly.FromDateTime(today.Date));
    }
}
```

- [ ] **Step 2: Add the FakeTimeProvider package**

```bash
dotnet add tests/OnDeck.Core.Tests package Microsoft.Extensions.TimeProvider.Testing
```

This is a **test-project-only** dependency; `OnDeck.Core` keeps zero package references.

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~BaseballCalendarTests`
Expected: compile error — `BaseballCalendar` does not exist.

- [ ] **Step 4: Write `src/OnDeck.Core/Utilities/BaseballCalendar.cs`**

```csharp
namespace OnDeck.Core.Utilities;

/// <summary>
/// Port of <c>ScheduleManager.baseballDate()</c>. Lives here rather than on the manager
/// because <c>FantraxApi</c>'s period detection needs it too.
/// </summary>
public static class BaseballCalendar
{
    /// <summary>
    /// The "baseball date" — before 8 AM local, we're still on yesterday's schedule.
    /// </summary>
    public static DateTimeOffset Today(TimeProvider timeProvider)
    {
        var now = timeProvider.GetLocalNow();
        return now.Hour < 8 ? now.AddDays(-1) : now;
    }
}
```

- [ ] **Step 5: Write the HTTP stub**

Create `tests/OnDeck.Core.Tests/Networking/StubHttpMessageHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace OnDeck.Core.Tests.Networking;

/// <summary>
/// Records every outgoing request and replays queued responses in order. Once the queue
/// drains, the last response repeats — convenient for the two-call Fantrax roster flow.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private HttpResponseMessage? _last;

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public Uri? LastUri => Requests.LastOrDefault()?.RequestUri;

    public void EnqueueJson(string json) =>
        _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public void EnqueueStatus(HttpStatusCode status) =>
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count > 0) _last = _responses.Dequeue();

        var source = _last ?? throw new InvalidOperationException("no response queued");

        // Responses are single-use once read, so hand back a fresh copy each time.
        return new HttpResponseMessage(source.StatusCode)
        {
            Content = new StringContent(
                await source.Content.ReadAsStringAsync(cancellationToken),
                Encoding.UTF8,
                "application/json"),
        };
    }

    public HttpClient CreateClient() => new(this);
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~BaseballCalendarTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add windows/
git commit -m "phase 3: add BaseballCalendar and the HTTP test stub"
```

---

## Task 2: MlbStatsApi — schedule

**Files:**
- Create: `src/OnDeck.Core/Networking/MlbStatsApi.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/MlbStatsApiScheduleTests.cs`

**Spec:** `MLBStatsAPI.swift:6-18, 49-81, 358-416`

**Interfaces:**
- Consumes: `Game` (Phase 1), `StubHttpMessageHandler` (Task 1).
- Produces:
  - `sealed class MlbStatsApi(HttpClient http, TimeProvider timeProvider)` — `timeProvider` defaults to `TimeProvider.System` via a second constructor.
  - `static HttpClient CreateDefaultClient()` — `SocketsHttpHandler { MaxConnectionsPerServer = 2 }`.
  - `Task<IReadOnlyList<Game>> FetchScheduleAsync(DateTimeOffset date, CancellationToken ct = default)`.

**URL:** `https://statsapi.mlb.com/api/v1/schedule?sportId=1&date={yyyy-MM-dd}&hydrate=team,broadcasts,probablePitcher,lineups`

The `hydrate=lineups` term is what returns batting orders as soon as managers submit lineup cards — often hours pre-game. Do not drop it.

**Mapping rules:** flatten `dates[].games[]`; a broadcast contributes only when `callSign` is non-null, and `IsExclusive` is `availability.availabilityCode == "exclusive"`; `StartTime` parses `gameDate` as ISO 8601 and falls back to *now* when unparseable; lineups map `homePlayers[].id` / `awayPlayers[].id`, defaulting to empty.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Networking/MlbStatsApiScheduleTests.cs`:

```csharp
using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class MlbStatsApiScheduleTests
{
    private const string ScheduleJson = """
    {
      "dates": [
        {
          "games": [
            {
              "gamePk": 776543,
              "gameDate": "2026-08-08T23:10:00Z",
              "status": {"abstractGameState": "Preview", "detailedState": "Scheduled"},
              "teams": {
                "away": {
                  "team": {"id": 137, "name": "San Francisco Giants"},
                  "probablePitcher": {"id": 592866}
                },
                "home": {
                  "team": {"id": 119, "name": "Los Angeles Dodgers"},
                  "probablePitcher": {"id": 605483}
                }
              },
              "broadcasts": [
                {"type": "TV", "callSign": "SNLA", "availability": {"availabilityCode": "regional"}},
                {"type": "TV", "callSign": "Peacock", "availability": {"availabilityCode": "exclusive"}},
                {"type": "TV", "availability": {"availabilityCode": "exclusive"}}
              ],
              "lineups": {
                "homePlayers": [{"id": 660271}, {"id": 605141}],
                "awayPlayers": [{"id": 592885}]
              }
            }
          ]
        }
      ]
    }
    """;

    private static (MlbStatsApi Api, StubHttpMessageHandler Handler) Create(string json)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);
        return (new MlbStatsApi(handler.CreateClient()), handler);
    }

    [Fact]
    public async Task FetchScheduleAsync_RequestsHydratedScheduleForTheDate()
    {
        var (api, handler) = Create(ScheduleJson);

        await api.FetchScheduleAsync(new DateTimeOffset(2026, 8, 8, 14, 30, 0, TimeSpan.Zero));

        var url = handler.LastUri!.ToString();
        Assert.StartsWith("https://statsapi.mlb.com/api/v1/schedule?", url);
        Assert.Contains("sportId=1", url);
        Assert.Contains("date=2026-08-08", url);
        Assert.Contains("hydrate=team,broadcasts,probablePitcher,lineups", url);
    }

    [Fact]
    public async Task FetchScheduleAsync_MapsGameFields()
    {
        var (api, _) = Create(ScheduleJson);

        var game = Assert.Single(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));

        Assert.Equal(776543, game.Id);
        Assert.Equal("Los Angeles Dodgers", game.HomeTeam);
        Assert.Equal("San Francisco Giants", game.AwayTeam);
        Assert.Equal(119, game.HomeTeamId);
        Assert.Equal(137, game.AwayTeamId);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero), game.StartTime);
        Assert.Equal(605483, game.HomeProbablePitcherId);
        Assert.Equal(592866, game.AwayProbablePitcherId);
    }

    [Fact]
    public async Task FetchScheduleAsync_KeepsNamedBroadcastsAndFlagsExclusive()
    {
        var (api, _) = Create(ScheduleJson);

        var game = Assert.Single(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));

        // The third broadcast has no callSign and is dropped.
        Assert.Equal(2, game.Broadcasts.Count);
        Assert.Equal("SNLA", game.Broadcasts[0].CallSign);
        Assert.False(game.Broadcasts[0].IsExclusive);
        Assert.Equal("Peacock", game.Broadcasts[1].CallSign);
        Assert.True(game.Broadcasts[1].IsExclusive);
    }

    [Fact]
    public async Task FetchScheduleAsync_ReadsSubmittedLineups()
    {
        var (api, _) = Create(ScheduleJson);

        var game = Assert.Single(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));

        Assert.Equal([660271, 605141], game.HomeLineup);
        Assert.Equal([592885], game.AwayLineup);
    }

    [Fact]
    public async Task FetchScheduleAsync_DefaultsMissingOptionalSections()
    {
        const string json = """
        {
          "dates": [{"games": [{
            "gamePk": 1,
            "gameDate": "2026-08-08T23:10:00Z",
            "status": {"abstractGameState": "Preview"},
            "teams": {
              "away": {"team": {"id": 1, "name": "A"}},
              "home": {"team": {"id": 2, "name": "H"}}
            }
          }]}]
        }
        """;
        var (api, _) = Create(json);

        var game = Assert.Single(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));

        Assert.Null(game.HomeProbablePitcherId);
        Assert.Null(game.AwayProbablePitcherId);
        Assert.Empty(game.Broadcasts);
        Assert.Empty(game.HomeLineup);
        Assert.Empty(game.AwayLineup);
    }

    [Fact]
    public async Task FetchScheduleAsync_FlattensMultipleDates()
    {
        const string json = """
        {
          "dates": [
            {"games": [{"gamePk": 1, "gameDate": "2026-08-08T23:10:00Z",
              "status": {"abstractGameState": "Preview"},
              "teams": {"away": {"team": {"id": 1, "name": "A"}}, "home": {"team": {"id": 2, "name": "H"}}}}]},
            {"games": [{"gamePk": 2, "gameDate": "2026-08-09T23:10:00Z",
              "status": {"abstractGameState": "Preview"},
              "teams": {"away": {"team": {"id": 3, "name": "C"}}, "home": {"team": {"id": 4, "name": "D"}}}}]}
          ]
        }
        """;
        var (api, _) = Create(json);

        var games = await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch);

        Assert.Equal([1, 2], games.Select(g => g.Id));
    }

    [Fact]
    public async Task FetchScheduleAsync_ReturnsEmptyWhenNoDates()
    {
        var (api, _) = Create("""{"dates": []}""");
        Assert.Empty(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~MlbStatsApiScheduleTests`
Expected: compile error — `MlbStatsApi` does not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Networking/MlbStatsApi.cs`**

```csharp
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnDeck.Core.Models;

namespace OnDeck.Core.Networking;

/// <summary>Port of <c>Networking/MLBStatsAPI.swift</c>.</summary>
public sealed class MlbStatsApi(HttpClient http, TimeProvider timeProvider)
{
    private const string BaseUrl = "https://statsapi.mlb.com/api";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public MlbStatsApi(HttpClient http) : this(http, TimeProvider.System) { }

    /// <summary>
    /// Swift pins <c>urlCache = nil</c> and <c>httpMaximumConnectionsPerHost = 2</c> to keep
    /// poll-cycle residency down. .NET does not cache responses by default, so only the
    /// connection cap carries over.
    /// </summary>
    public static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler { MaxConnectionsPerServer = 2 });

    // MARK: - Schedule

    public async Task<IReadOnlyList<Game>> FetchScheduleAsync(
        DateTimeOffset date, CancellationToken ct = default)
    {
        var dateString = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl}/v1/schedule?sportId=1&date={dateString}"
                  + "&hydrate=team,broadcasts,probablePitcher,lineups";

        var response = await GetJsonAsync<ScheduleResponse>(url, ct);

        var games = new List<Game>();
        foreach (var scheduleDate in response.Dates ?? [])
        {
            foreach (var game in scheduleDate.Games ?? [])
            {
                games.Add(MapGame(game));
            }
        }

        return games;
    }

    private Game MapGame(ScheduleGame game)
    {
        var broadcasts = new List<Game.Broadcast>();
        foreach (var broadcast in game.Broadcasts ?? [])
        {
            if (broadcast.CallSign is not { } callSign) continue;
            broadcasts.Add(new Game.Broadcast(
                callSign,
                broadcast.Availability?.AvailabilityCode == "exclusive"));
        }

        var startTime = DateTimeOffset.TryParse(
            game.GameDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : timeProvider.GetUtcNow();

        return new Game(
            game.GamePk,
            game.Teams.Home.Team.Name,
            game.Teams.Away.Team.Name,
            game.Teams.Home.Team.Id,
            game.Teams.Away.Team.Id,
            startTime,
            game.Teams.Home.ProbablePitcher?.Id,
            game.Teams.Away.ProbablePitcher?.Id,
            broadcasts,
            [.. (game.Lineups?.HomePlayers ?? []).Select(p => p.Id)],
            [.. (game.Lineups?.AwayPlayers ?? []).Select(p => p.Id)]);
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct)
               ?? throw new JsonException($"{url} decoded to null");
    }

    // --- Schedule DTOs

    private sealed class ScheduleResponse
    {
        public List<ScheduleDate>? Dates { get; set; }
    }

    private sealed class ScheduleDate
    {
        public List<ScheduleGame>? Games { get; set; }
    }

    private sealed class ScheduleGame
    {
        public int GamePk { get; set; }
        public required string GameDate { get; set; }
        public required ScheduleGameTeams Teams { get; set; }
        public List<ScheduleBroadcast>? Broadcasts { get; set; }
        public ScheduleLineups? Lineups { get; set; }
    }

    private sealed class ScheduleGameTeams
    {
        public required ScheduleTeamEntry Away { get; set; }
        public required ScheduleTeamEntry Home { get; set; }
    }

    private sealed class ScheduleTeamEntry
    {
        public required ScheduleTeamInfo Team { get; set; }
        public ScheduleProbablePitcher? ProbablePitcher { get; set; }
    }

    private sealed class ScheduleTeamInfo
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }

    private sealed class ScheduleProbablePitcher
    {
        public int Id { get; set; }
    }

    private sealed class ScheduleBroadcast
    {
        public string? Type { get; set; }
        public string? CallSign { get; set; }
        public BroadcastAvailability? Availability { get; set; }
    }

    private sealed class BroadcastAvailability
    {
        public string? AvailabilityCode { get; set; }
    }

    private sealed class ScheduleLineups
    {
        public List<ScheduleLineupPlayer>? HomePlayers { get; set; }
        public List<ScheduleLineupPlayer>? AwayPlayers { get; set; }
    }

    private sealed class ScheduleLineupPlayer
    {
        public int Id { get; set; }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~MlbStatsApiScheduleTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "phase 3: MlbStatsApi schedule fetch with hydrated lineups"
```

---

## Task 3: MlbStatsApi — player search

**Files:**
- Modify: `src/OnDeck.Core/Networking/MlbStatsApi.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/MlbStatsApiSearchTests.cs`

**Spec:** `MLBStatsAPI.swift:22-45`

**Interfaces:**
- Produces: `Task<int?> SearchPlayerAsync(string name, string? teamName, CancellationToken ct = default)`.

**URL:** `{BaseUrl}/v1/people/search?names={urlEncoded}&hydrate=currentTeam`

**Disambiguation order (preserve exactly):** when `teamName` is supplied, return the **first** person whose `currentTeam.name` satisfies `TeamMapping.Matches(currentTeamName, teamName)` **or** `currentTeamName.Contains(teamName)` **or** `teamName.Contains(currentTeamName)`. When no person matches — or no `teamName` was given — fall back to the **first** result. Return `null` only when `people` is absent or empty.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Networking/MlbStatsApiSearchTests.cs`:

```csharp
using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class MlbStatsApiSearchTests
{
    private const string TwoOhtanisJson = """
    {
      "people": [
        {"id": 1, "fullName": "Shohei Ohtani", "currentTeam": {"id": 108, "name": "Los Angeles Angels"}},
        {"id": 2, "fullName": "Shohei Ohtani", "currentTeam": {"id": 119, "name": "Los Angeles Dodgers"}}
      ]
    }
    """;

    private static (MlbStatsApi Api, StubHttpMessageHandler Handler) Create(string json)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);
        return (new MlbStatsApi(handler.CreateClient()), handler);
    }

    [Fact]
    public async Task SearchPlayerAsync_UrlEncodesTheNameAndHydratesTeam()
    {
        var (api, handler) = Create(TwoOhtanisJson);

        await api.SearchPlayerAsync("Shohei Ohtani", null);

        var url = handler.LastUri!.ToString();
        Assert.Contains("/v1/people/search?names=Shohei%20Ohtani", url);
        Assert.Contains("hydrate=currentTeam", url);
    }

    [Fact]
    public async Task SearchPlayerAsync_DisambiguatesByFantraxAbbreviation()
    {
        var (api, _) = Create(TwoOhtanisJson);
        Assert.Equal(2, await api.SearchPlayerAsync("Shohei Ohtani", "LAD"));
    }

    [Fact]
    public async Task SearchPlayerAsync_DisambiguatesByPartialTeamName()
    {
        var (api, _) = Create(TwoOhtanisJson);
        Assert.Equal(1, await api.SearchPlayerAsync("Shohei Ohtani", "Angels"));
    }

    [Fact]
    public async Task SearchPlayerAsync_FallsBackToFirstResultWhenTeamDoesNotMatch()
    {
        var (api, _) = Create(TwoOhtanisJson);
        Assert.Equal(1, await api.SearchPlayerAsync("Shohei Ohtani", "BOS"));
    }

    [Fact]
    public async Task SearchPlayerAsync_FallsBackToFirstResultWithoutATeam()
    {
        var (api, _) = Create(TwoOhtanisJson);
        Assert.Equal(1, await api.SearchPlayerAsync("Shohei Ohtani", null));
    }

    [Fact]
    public async Task SearchPlayerAsync_IgnoresPeopleWithoutACurrentTeam()
    {
        const string json = """
        {
          "people": [
            {"id": 10, "fullName": "Prospect Guy"},
            {"id": 11, "fullName": "Prospect Guy", "currentTeam": {"id": 119, "name": "Los Angeles Dodgers"}}
          ]
        }
        """;
        var (api, _) = Create(json);

        Assert.Equal(11, await api.SearchPlayerAsync("Prospect Guy", "LAD"));
    }

    [Theory]
    [InlineData("""{"people": []}""")]
    [InlineData("""{}""")]
    public async Task SearchPlayerAsync_ReturnsNullWhenNobodyFound(string json)
    {
        var (api, _) = Create(json);
        Assert.Null(await api.SearchPlayerAsync("Nobody", null));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~MlbStatsApiSearchTests`
Expected: compile error — `SearchPlayerAsync` does not exist.

- [ ] **Step 3: Add the method**

Add `using OnDeck.Core.Utilities;` to the file, then insert before the `// MARK: - Schedule` region:

```csharp
    // MARK: - Player Search

    public async Task<int?> SearchPlayerAsync(
        string name, string? teamName, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/v1/people/search?names={Uri.EscapeDataString(name)}&hydrate=currentTeam";
        var response = await GetJsonAsync<SearchResponse>(url, ct);

        if (response.People is not { Count: > 0 } people) return null;

        // Disambiguate by team when we have one to go on.
        if (teamName is not null)
        {
            foreach (var person in people)
            {
                if (person.CurrentTeam?.Name is not { } currentTeamName) continue;

                if (TeamMapping.Matches(currentTeamName, teamName)
                    || currentTeamName.Contains(teamName, StringComparison.Ordinal)
                    || teamName.Contains(currentTeamName, StringComparison.Ordinal))
                {
                    return person.Id;
                }
            }
        }

        // Fall back to first result
        return people[0].Id;
    }
```

and these DTOs alongside the schedule ones:

```csharp
    private sealed class SearchResponse
    {
        public List<SearchPerson>? People { get; set; }
    }

    private sealed class SearchPerson
    {
        public int Id { get; set; }
        public required string FullName { get; set; }
        public SearchTeam? CurrentTeam { get; set; }
    }

    private sealed class SearchTeam
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~MlbStatsApiSearchTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "phase 3: MlbStatsApi player search with team disambiguation"
```

---

## Task 4: MlbStatsApi — live feed and game changes

**Files:**
- Modify: `src/OnDeck.Core/Networking/MlbStatsApi.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/MlbStatsApiLiveFeedTests.cs`

**Spec:** `MLBStatsAPI.swift:86-92, 197-206, 543-553`

**Interfaces:**
- Produces:
  - `Task<LiveFeedData> FetchLiveFeedAsync(int gamePk, CancellationToken ct = default)` — GET `{BaseUrl}/v1.1/game/{gamePk}/feed/live`, decoded through `LiveFeedDecoder`.
  - `Task<IReadOnlySet<int>> FetchGameChangesAsync(DateTimeOffset since, CancellationToken ct = default)` — GET `{BaseUrl}/v1/game/changes?updatedSince={urlEncoded ISO 8601}&sportId=1`, flattening `dates[].games[].gamePk` into a set.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Networking/MlbStatsApiLiveFeedTests.cs`:

```csharp
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;

namespace OnDeck.Core.Tests.Networking;

public class MlbStatsApiLiveFeedTests
{
    private static (MlbStatsApi Api, StubHttpMessageHandler Handler) Create(string json)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);
        return (new MlbStatsApi(handler.CreateClient()), handler);
    }

    [Fact]
    public async Task FetchLiveFeedAsync_RequestsTheV11FeedForTheGame()
    {
        var (api, handler) = Create(LiveFeedPatcherFixtures.BaseFeedJson);

        await api.FetchLiveFeedAsync(776543);

        Assert.Equal(
            "https://statsapi.mlb.com/api/v1.1/game/776543/feed/live",
            handler.LastUri!.ToString());
    }

    [Fact]
    public async Task FetchLiveFeedAsync_DecodesThroughLiveFeedDecoder()
    {
        var (api, _) = Create(LiveFeedPatcherFixtures.BaseFeedJson);

        var feed = await api.FetchLiveFeedAsync(776543);

        Assert.Equal("20260416_180000", feed.TimeStamp);
        Assert.Equal("Live", feed.GameState);
        Assert.Equal(1, feed.CurrentBatterId);
        Assert.Equal([2], feed.HomePitchers);
    }

    [Fact]
    public async Task FetchGameChangesAsync_RequestsChangesSinceTheTimestamp()
    {
        var (api, handler) = Create("""{"dates": []}""");

        await api.FetchGameChangesAsync(new DateTimeOffset(2026, 8, 8, 14, 30, 0, TimeSpan.Zero));

        var url = handler.LastUri!.ToString();
        Assert.StartsWith("https://statsapi.mlb.com/api/v1/game/changes?", url);
        Assert.Contains("sportId=1", url);
        Assert.Contains("updatedSince=2026-08-08T14%3A30%3A00Z", url);
    }

    [Fact]
    public async Task FetchGameChangesAsync_FlattensGamePksAcrossDates()
    {
        const string json = """
        {
          "dates": [
            {"games": [{"gamePk": 1}, {"gamePk": 2}]},
            {"games": [{"gamePk": 3}, {"gamePk": 1}]}
          ]
        }
        """;
        var (api, _) = Create(json);

        var changed = await api.FetchGameChangesAsync(DateTimeOffset.UnixEpoch);

        Assert.Equal([1, 2, 3], changed.Order());
    }

    [Fact]
    public async Task FetchGameChangesAsync_ReturnsEmptySetWhenNoDates()
    {
        var (api, _) = Create("""{}""");
        Assert.Empty(await api.FetchGameChangesAsync(DateTimeOffset.UnixEpoch));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~MlbStatsApiLiveFeedTests`
Expected: compile error — `FetchLiveFeedAsync` does not exist.

- [ ] **Step 3: Add the methods**

Insert after the schedule region:

```csharp
    // MARK: - Live Feed

    /// <summary>Fetches the full live feed and returns parsed data.</summary>
    public async Task<LiveFeedData> FetchLiveFeedAsync(int gamePk, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/v1.1/game/{gamePk}/feed/live";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return LiveFeedDecoder.Decode(bytes);
    }

    // MARK: - Game Changes

    /// <summary>Returns the set of gamePks that have been updated since <paramref name="since"/>.</summary>
    public async Task<IReadOnlySet<int>> FetchGameChangesAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        var timestamp = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl}/v1/game/changes?updatedSince={Uri.EscapeDataString(timestamp)}&sportId=1";

        var response = await GetJsonAsync<GameChangesResponse>(url, ct);

        var gamePks = new HashSet<int>();
        foreach (var date in response.Dates ?? [])
        {
            foreach (var game in date.Games ?? []) gamePks.Add(game.GamePk);
        }

        return gamePks;
    }
```

and these DTOs:

```csharp
    private sealed class GameChangesResponse
    {
        public List<GameChangesDate>? Dates { get; set; }
    }

    private sealed class GameChangesDate
    {
        public List<GameChangesGame>? Games { get; set; }
    }

    private sealed class GameChangesGame
    {
        public int GamePk { get; set; }
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~MlbStatsApiLiveFeedTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/
git commit -m "phase 3: MlbStatsApi live feed and game changes"
```

---

## Task 5: MlbStatsApi — diffPatch

**Files:**
- Create: `src/OnDeck.Core/Networking/DiffPatchResult.cs`
- Modify: `src/OnDeck.Core/Networking/MlbStatsApi.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/MlbStatsApiDiffPatchTests.cs`

**Spec:** `MLBStatsAPI.swift:103-149, 246-251`

**Interfaces:**
- Produces:
  - `abstract record DiffPatchResult` with a private constructor and three nested cases:
    `DiffPatchResult.NoChanges`, `DiffPatchResult.Patches(IReadOnlyList<PatchOperation> Operations)`,
    `DiffPatchResult.FullUpdate(byte[] Json)`.
  - `Task<DiffPatchResult> FetchDiffPatchAsync(int gamePk, string sinceTimecode, CancellationToken ct = default)`.

**URL:** `{BaseUrl}/v1.1/game/{gamePk}/feed/live/diffPatch?startTimecode={since}&endTimecode={now}` where `now` is `TimeProvider.GetUtcNow()` formatted `yyyyMMdd_HHmmss`. This is the **same format** MLB puts in `metaData.timeStamp`, which is where `sinceTimecode` comes from.

**Response dispatch (this is the dict-fallback gotcha — preserve exactly):**
1. Root is a JSON **object**, not an array → `FullUpdate` carrying the whole body. MLB does this during game-phase transitions (inning changes); it resolves after a few cycles.
2. Root is neither object nor array → `FullUpdate` carrying the whole body.
3. Root is an **empty** array → `NoChanges`.
4. Otherwise walk entries in order: an entry with a `diff` array contributes its ops; the **first** entry without one aborts the walk and returns `FullUpdate` carrying **that entry** re-serialized (not the whole body).
5. All entries had `diff` → `Patches` with the concatenated ops.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Networking/MlbStatsApiDiffPatchTests.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class MlbStatsApiDiffPatchTests
{
    private static (MlbStatsApi Api, StubHttpMessageHandler Handler) Create(string json)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 23, 45, 7, TimeSpan.Zero));
        return (new MlbStatsApi(handler.CreateClient(), time), handler);
    }

    [Fact]
    public async Task FetchDiffPatchAsync_FormsStartAndEndTimecodes()
    {
        var (api, handler) = Create("[]");

        await api.FetchDiffPatchAsync(776543, "20260808_234500");

        Assert.Equal(
            "https://statsapi.mlb.com/api/v1.1/game/776543/feed/live/diffPatch"
                + "?startTimecode=20260808_234500&endTimecode=20260808_234507",
            handler.LastUri!.ToString());
    }

    [Fact]
    public async Task FetchDiffPatchAsync_EmptyArrayIsNoChanges()
    {
        var (api, _) = Create("[]");
        Assert.IsType<DiffPatchResult.NoChanges>(await api.FetchDiffPatchAsync(1, "t"));
    }

    [Fact]
    public async Task FetchDiffPatchAsync_CollectsOpsFromEveryDiffEntry()
    {
        const string json = """
        [
          {"diff": [
            {"op": "replace", "path": "/metaData/timeStamp", "value": "20260808_234507"},
            {"op": "replace", "path": "/liveData/linescore/outs", "value": 2}
          ]},
          {"diff": [{"op": "replace", "path": "/liveData/linescore/balls", "value": 1}]}
        ]
        """;
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.Patches>(await api.FetchDiffPatchAsync(1, "t"));

        Assert.Equal(
            ["/metaData/timeStamp", "/liveData/linescore/outs", "/liveData/linescore/balls"],
            result.Operations.Select(o => o.Path));
    }

    [Fact]
    public async Task FetchDiffPatchAsync_ObjectRootIsFullUpdateCarryingTheWholeBody()
    {
        // The dict-instead-of-array fallback: MLB returns a bare feed object during game
        // phase transitions. It resolves itself after a few cycles.
        const string json = """{"metaData": {"timeStamp": "20260808_234507"}, "gameData": {}}""";
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.FullUpdate>(await api.FetchDiffPatchAsync(1, "t"));

        Assert.Contains("\"timeStamp\"", Encoding.UTF8.GetString(result.Json));
    }

    [Fact]
    public async Task FetchDiffPatchAsync_EntryWithoutDiffIsFullUpdateCarryingThatEntry()
    {
        const string json = """
        [
          {"diff": [{"op": "replace", "path": "/a", "value": 1}]},
          {"metaData": {"timeStamp": "20260808_234507"}, "gameData": {"marker": "second-entry"}}
        ]
        """;
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.FullUpdate>(await api.FetchDiffPatchAsync(1, "t"));

        var payload = Encoding.UTF8.GetString(result.Json);
        Assert.Contains("second-entry", payload);
        Assert.DoesNotContain("\"diff\"", payload);
    }

    [Fact]
    public async Task FetchDiffPatchAsync_ScalarRootIsFullUpdate()
    {
        var (api, _) = Create("\"unexpected\"");
        Assert.IsType<DiffPatchResult.FullUpdate>(await api.FetchDiffPatchAsync(1, "t"));
    }

    [Fact]
    public async Task FetchDiffPatchAsync_SkipsMalformedOpsWithinADiff()
    {
        const string json = """
        [{"diff": [
          {"op": "replace", "path": "/a", "value": 1},
          {"op": "replace"},
          {"path": "/b"}
        ]}]
        """;
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.Patches>(await api.FetchDiffPatchAsync(1, "t"));

        Assert.Equal(["/a"], result.Operations.Select(o => o.Path));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~MlbStatsApiDiffPatchTests`
Expected: compile error — `DiffPatchResult` does not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Networking/DiffPatchResult.cs`**

```csharp
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Networking;

/// <summary>Result of a diffPatch request. Port of <c>DiffPatchResult</c> in MLBStatsAPI.swift.</summary>
public abstract record DiffPatchResult
{
    private DiffPatchResult() { }

    public sealed record NoChanges : DiffPatchResult;

    public sealed record Patches(IReadOnlyList<PatchOperation> Operations) : DiffPatchResult;

    /// <summary>The API returned a full feed object instead of patches.</summary>
    public sealed record FullUpdate(byte[] Json) : DiffPatchResult;
}
```

- [ ] **Step 4: Add `FetchDiffPatchAsync`**

Add `using OnDeck.Core.Utilities;` if not already present, then insert after the live-feed region:

```csharp
    // MARK: - Diff Patch

    /// <summary>Fetches diff patches for a game since a given timecode.</summary>
    public async Task<DiffPatchResult> FetchDiffPatchAsync(
        int gamePk, string sinceTimecode, CancellationToken ct = default)
    {
        var now = CurrentTimecode();
        var url = $"{BaseUrl}/v1.1/game/{gamePk}/feed/live/diffPatch"
                  + $"?startTimecode={sinceTimecode}&endTimecode={now}";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        // The API sometimes returns a single feed object (dict) instead of an array. This
        // happens during game phase transitions and resolves itself after a few cycles.
        if (root.ValueKind != JsonValueKind.Array) return new DiffPatchResult.FullUpdate(bytes);

        if (root.GetArrayLength() == 0) return new DiffPatchResult.NoChanges();

        // Entries either carry a "diff" array (patches) or are full feed objects (fallback).
        var operations = new List<PatchOperation>();
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("diff", out var diff)
                && diff.ValueKind == JsonValueKind.Array)
            {
                operations.AddRange(PatchOperation.ParseArray(diff));
                continue;
            }

            // Full feed object instead of patches - hand back just this entry.
            return new DiffPatchResult.FullUpdate(
                JsonSerializer.SerializeToUtf8Bytes(entry, Options));
        }

        return new DiffPatchResult.Patches(operations);
    }

    /// <summary>
    /// <c>yyyyMMdd_HHmmss</c> UTC — the same format MLB reports in <c>metaData.timeStamp</c>,
    /// which is where the caller's <c>startTimecode</c> comes from.
    /// </summary>
    private string CurrentTimecode() =>
        timeProvider.GetUtcNow().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~MlbStatsApiDiffPatchTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add windows/
git commit -m "phase 3: MlbStatsApi diffPatch with dict-instead-of-array fallback"
```

---

## Task 6: FantraxApi — transport and teams

**Files:**
- Create: `src/OnDeck.Core/Networking/FantraxModels.cs`
- Create: `src/OnDeck.Core/Networking/FantraxApi.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/FantraxApiTeamsTests.cs`

**Spec:** `FantraxAPI.swift:3-48, 129-154, 179-196`

**Interfaces:**
- Produces:
  - `sealed record FantraxTeam(string Id, string Name)`
  - `sealed record FantraxPlayer(string Name, string TeamShortName, IReadOnlyList<string> Positions, int StatusId)` — `StatusId` is 1=Active, 2=Reserve, 3=Inj Res, 9=Minors
  - `enum FantraxErrorKind { InvalidResponse, HttpError, NoTeamsFound }`
  - `sealed class FantraxException : Exception` with `FantraxErrorKind Kind` and `int? StatusCode`; messages mirror Swift's `errorDescription` ("Invalid response from Fantrax", "Fantrax API returned HTTP {code}", "No teams found in league")
  - `sealed class FantraxApi(HttpClient http, TimeProvider timeProvider)` with a one-arg convenience constructor and `Task<IReadOnlyList<FantraxTeam>> FetchTeamsAsync(string leagueId, CancellationToken ct = default)`

**Transport:** POST to `https://www.fantrax.com/fxpa/req?leagueId={leagueId}` with `Content-Type: text/plain` and body `{"msgs":[{"method":"{method}","data":{...}}],"uiv":3}`. Non-200 throws `FantraxException(HttpError, statusCode)`; a body that isn't a JSON object throws `FantraxException(InvalidResponse)`.

**Teams:** call `getStandings` with `data = {"leagueId": leagueId}`, then walk the whole response tree collecting every object that has **both** a non-empty string `teamId` and a non-empty string `content`. Dedupe by `teamId` keeping the first occurrence, then sort by name (ordinal). Empty → `FantraxException(NoTeamsFound)`.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Networking/FantraxApiTeamsTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class FantraxApiTeamsTests
{
    private const string StandingsJson = """
    {
      "responses": [{"data": {"tableList": [{"rows": [
        {"teamId": "t2", "content": "Zulu Squad"},
        {"teamId": "t1", "content": "Alpha Squad"},
        {"teamId": "t2", "content": "Zulu Squad"},
        {"teamId": "", "content": "Nameless"},
        {"teamId": "t3", "content": ""},
        {"nested": {"teamId": "t4", "content": "Mike Team"}}
      ]}]}}]
    }
    """;

    private static (FantraxApi Api, StubHttpMessageHandler Handler) Create(params string[] jsonResponses)
    {
        var handler = new StubHttpMessageHandler();
        foreach (var json in jsonResponses) handler.EnqueueJson(json);
        return (new FantraxApi(handler.CreateClient()), handler);
    }

    [Fact]
    public async Task FetchTeamsAsync_PostsGetStandingsToTheLeagueEndpoint()
    {
        var (api, handler) = Create(StandingsJson);

        await api.FetchTeamsAsync("lg123");

        var request = handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://www.fantrax.com/fxpa/req?leagueId=lg123", request.RequestUri!.ToString());
        Assert.Equal("text/plain", request.Content!.Headers.ContentType!.MediaType);

        using var body = JsonDocument.Parse(handler.RequestBodies[^1]);
        var message = body.RootElement.GetProperty("msgs")[0];
        Assert.Equal("getStandings", message.GetProperty("method").GetString());
        Assert.Equal("lg123", message.GetProperty("data").GetProperty("leagueId").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("uiv").GetInt32());
    }

    [Fact]
    public async Task FetchTeamsAsync_CollectsDedupesAndSortsByName()
    {
        var (api, _) = Create(StandingsJson);

        var teams = await api.FetchTeamsAsync("lg123");

        Assert.Equal(["Alpha Squad", "Mike Team", "Zulu Squad"], teams.Select(t => t.Name));
        Assert.Equal(["t1", "t4", "t2"], teams.Select(t => t.Id));
    }

    [Fact]
    public async Task FetchTeamsAsync_ThrowsWhenNoTeamsFound()
    {
        var (api, _) = Create("""{"responses": [{"data": {}}]}""");

        var error = await Assert.ThrowsAsync<FantraxException>(() => api.FetchTeamsAsync("lg123"));
        Assert.Equal(FantraxErrorKind.NoTeamsFound, error.Kind);
        Assert.Equal("No teams found in league", error.Message);
    }

    [Fact]
    public async Task FetchTeamsAsync_ThrowsOnHttpError()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var api = new FantraxApi(handler.CreateClient());

        var error = await Assert.ThrowsAsync<FantraxException>(() => api.FetchTeamsAsync("lg123"));
        Assert.Equal(FantraxErrorKind.HttpError, error.Kind);
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("Fantrax API returned HTTP 503", error.Message);
    }

    [Fact]
    public async Task FetchTeamsAsync_ThrowsWhenBodyIsNotAJsonObject()
    {
        var (api, _) = Create("[1, 2, 3]");

        var error = await Assert.ThrowsAsync<FantraxException>(() => api.FetchTeamsAsync("lg123"));
        Assert.Equal(FantraxErrorKind.InvalidResponse, error.Kind);
        Assert.Equal("Invalid response from Fantrax", error.Message);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~FantraxApiTeamsTests`
Expected: compile error — `FantraxApi` does not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Networking/FantraxModels.cs`**

```csharp
namespace OnDeck.Core.Networking;

public sealed record FantraxTeam(string Id, string Name);

/// <summary><paramref name="StatusId"/>: 1=Active, 2=Reserve, 3=Inj Res, 9=Minors.</summary>
public sealed record FantraxPlayer(
    string Name,
    string TeamShortName,
    IReadOnlyList<string> Positions,
    int StatusId);

public enum FantraxErrorKind
{
    InvalidResponse,
    HttpError,
    NoTeamsFound,
}

public sealed class FantraxException(FantraxErrorKind kind, string message, int? statusCode = null)
    : Exception(message)
{
    public FantraxErrorKind Kind { get; } = kind;

    public int? StatusCode { get; } = statusCode;

    public static FantraxException InvalidResponse() =>
        new(FantraxErrorKind.InvalidResponse, "Invalid response from Fantrax");

    public static FantraxException HttpError(int statusCode) =>
        new(FantraxErrorKind.HttpError, $"Fantrax API returned HTTP {statusCode}", statusCode);

    public static FantraxException NoTeamsFound() =>
        new(FantraxErrorKind.NoTeamsFound, "No teams found in league");
}
```

- [ ] **Step 4: Write `src/OnDeck.Core/Networking/FantraxApi.cs`**

```csharp
using System.Text;
using System.Text.Json;

namespace OnDeck.Core.Networking;

/// <summary>Port of <c>Networking/FantraxAPI.swift</c>.</summary>
public sealed class FantraxApi(HttpClient http, TimeProvider timeProvider)
{
    public FantraxApi(HttpClient http) : this(http, TimeProvider.System) { }

    // MARK: - Fetch Teams

    /// <summary>Fetches the list of teams in a league using <c>getStandings</c>.</summary>
    public async Task<IReadOnlyList<FantraxTeam>> FetchTeamsAsync(
        string leagueId, CancellationToken ct = default)
    {
        using var document = await PostRequestAsync(
            leagueId, "getStandings", new Dictionary<string, string> { ["leagueId"] = leagueId }, ct);

        var teams = new List<FantraxTeam>();
        FindTeams(document.RootElement, teams);

        if (teams.Count == 0) throw FantraxException.NoTeamsFound();

        // Deduplicate by teamId, keeping the first occurrence.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. teams.Where(team => seen.Add(team.Id)).OrderBy(team => team.Name, StringComparer.Ordinal)];
    }

    // MARK: - Network

    private async Task<JsonDocument> PostRequestAsync(
        string leagueId, string method, Dictionary<string, string> data, CancellationToken ct)
    {
        var url = $"https://www.fantrax.com/fxpa/req?leagueId={leagueId}";

        var body = JsonSerializer.Serialize(new
        {
            msgs = new[] { new { method, data } },
            uiv = 3,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw FantraxException.HttpError((int)response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw FantraxException.InvalidResponse();
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw FantraxException.InvalidResponse();
        }

        return document;
    }

    // MARK: - JSON Walkers

    /// <summary>
    /// Recursively walks the JSON tree to find team objects. In the standings response, teams
    /// have <c>teamId</c> and <c>content</c> (team name) fields.
    /// </summary>
    private static void FindTeams(JsonElement element, List<FantraxTeam> teams)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("teamId", out var teamId)
                    && teamId.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                    && teamId.GetString() is { Length: > 0 } id
                    && content.GetString() is { Length: > 0 } name)
                {
                    teams.Add(new FantraxTeam(id, name));
                }

                foreach (var property in element.EnumerateObject()) FindTeams(property.Value, teams);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) FindTeams(item, teams);
                break;
        }
    }
}
```

Note: `StringContent(body, Encoding.UTF8, "text/plain")` sets `Content-Type: text/plain; charset=utf-8`; the test asserts on `MediaType` only, matching Swift's `text/plain`.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~FantraxApiTeamsTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add windows/
git commit -m "phase 3: FantraxApi transport, errors, and team fetch"
```

---

## Task 7: FantraxApi — roster and period detection

**Files:**
- Modify: `src/OnDeck.Core/Networking/FantraxApi.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/FantraxApiRosterTests.cs`

**Spec:** `FantraxAPI.swift:52-127`

**Interfaces:**
- Produces: `Task<IReadOnlyList<FantraxPlayer>> FetchRosterAsync(string leagueId, string teamId, CancellationToken ct = default)`.

**The `period` gotcha — the whole reason this is a two-call flow.** `getTeamRosterInfo` defaults to the **next** period's lineup, not today's. So:
1. POST `getTeamRosterInfo` with `{leagueId, teamId}`.
2. Read `responses[0].data.displayedLists.periodList` — entries look like `"14 (Tue Apr 7)"`. Find the entry whose text contains `"{MonthAbbrev} {day})"` for **today's baseball date**, and take everything before its first space as the period number.
3. If a period was found, POST again with `{leagueId, teamId, period}` and parse that; otherwise parse the first response.

Month abbreviations are hardcoded English (`Jan`…`Dec`) — Fantrax emits English regardless of locale, so do **not** use `CultureInfo`.

**Row parsing:** navigate `responses[0].data.tables[].rows[]`; a row contributes when `row.scorer` has a string `name` **and** a `scorerId` property of any kind. `teamShortName` and `posShortNames` default to `""`; positions split on `,` and trim whitespace. `statusId` reads from the **row** (not the scorer) as an int or a numeric string, defaulting to `1`. Do not recurse into `cells` — they contain opposing-pitcher popovers. Empty result → `FantraxException(InvalidResponse)`.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Networking/FantraxApiRosterTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class FantraxApiRosterTests
{
    private const string RosterJson = """
    {
      "responses": [{"data": {
        "displayedLists": {"periodList": ["12 (Sun Aug 6)", "13 (Mon Aug 7)", "14 (Fri Aug 8)"]},
        "tables": [{"rows": [
          {"statusId": 1, "scorer": {"scorerId": "s1", "name": "Shohei Ohtani-P",
            "teamShortName": "LAD", "posShortNames": "SP,DH"}},
          {"statusId": "2", "scorer": {"scorerId": "s2", "name": "Mookie Betts",
            "teamShortName": "LAD", "posShortNames": "OF, 2B"}},
          {"scorer": {"scorerId": "s3", "name": "No Status Guy"}},
          {"scorer": {"name": "No ScorerId Guy", "teamShortName": "BOS"}},
          {"notAScorer": {"name": "Ignored"}}
        ]}]
      }}]
    }
    """;

    private static (FantraxApi Api, StubHttpMessageHandler Handler) Create(
        DateTimeOffset now, params string[] jsonResponses)
    {
        var handler = new StubHttpMessageHandler();
        foreach (var json in jsonResponses) handler.EnqueueJson(json);

        var time = new FakeTimeProvider(now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return (new FantraxApi(handler.CreateClient(), time), handler);
    }

    private static string MethodOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("msgs")[0].GetProperty("method").GetString()!;

    private static JsonElement DataOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("msgs")[0].GetProperty("data").Clone();

    [Fact]
    public async Task FetchRosterAsync_RefetchesWithTodaysPeriod()
    {
        // 2026-08-08 at 14:00 local -> baseball date is Aug 8 -> period "14".
        var (api, handler) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        await api.FetchRosterAsync("lg1", "tm1");

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal("getTeamRosterInfo", MethodOf(handler.RequestBodies[0]));

        var firstData = DataOf(handler.RequestBodies[0]);
        Assert.False(firstData.TryGetProperty("period", out _));

        var secondData = DataOf(handler.RequestBodies[1]);
        Assert.Equal("14", secondData.GetProperty("period").GetString());
        Assert.Equal("lg1", secondData.GetProperty("leagueId").GetString());
        Assert.Equal("tm1", secondData.GetProperty("teamId").GetString());
    }

    [Fact]
    public async Task FetchRosterAsync_UsesYesterdaysPeriodBeforeEightAm()
    {
        // 03:00 local on Aug 8 -> baseball date is Aug 7 -> period "13".
        var (api, handler) = Create(
            new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        await api.FetchRosterAsync("lg1", "tm1");

        Assert.Equal("13", DataOf(handler.RequestBodies[1]).GetProperty("period").GetString());
    }

    [Fact]
    public async Task FetchRosterAsync_SkipsTheSecondCallWhenNoPeriodMatches()
    {
        // Sept 8 is not in the periodList.
        var (api, handler) = Create(
            new DateTimeOffset(2026, 9, 8, 14, 0, 0, TimeSpan.Zero), RosterJson);

        var players = await api.FetchRosterAsync("lg1", "tm1");

        Assert.Single(handler.RequestBodies);
        Assert.Equal(3, players.Count);
    }

    [Fact]
    public async Task FetchRosterAsync_ParsesScorerRows()
    {
        var (api, _) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        var players = await api.FetchRosterAsync("lg1", "tm1");

        // The row without a scorerId and the row without a scorer are both dropped.
        Assert.Equal(3, players.Count);

        Assert.Equal("Shohei Ohtani-P", players[0].Name);
        Assert.Equal("LAD", players[0].TeamShortName);
        Assert.Equal(["SP", "DH"], players[0].Positions);
        Assert.Equal(1, players[0].StatusId);
    }

    [Fact]
    public async Task FetchRosterAsync_TrimsPositionsAndReadsNumericStringStatus()
    {
        var (api, _) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        var betts = (await api.FetchRosterAsync("lg1", "tm1"))[1];

        Assert.Equal(["OF", "2B"], betts.Positions);
        Assert.Equal(2, betts.StatusId);
    }

    [Fact]
    public async Task FetchRosterAsync_DefaultsMissingFields()
    {
        var (api, _) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        var noStatus = (await api.FetchRosterAsync("lg1", "tm1"))[2];

        Assert.Equal("No Status Guy", noStatus.Name);
        Assert.Equal("", noStatus.TeamShortName);
        Assert.Empty(noStatus.Positions);
        Assert.Equal(1, noStatus.StatusId);       // defaults to active
    }

    [Fact]
    public async Task FetchRosterAsync_ThrowsWhenNoPlayersParsed()
    {
        var (api, _) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero),
            """{"responses": [{"data": {"tables": []}}]}""");

        var error = await Assert.ThrowsAsync<FantraxException>(() => api.FetchRosterAsync("lg1", "tm1"));
        Assert.Equal(FantraxErrorKind.InvalidResponse, error.Kind);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~FantraxApiRosterTests`
Expected: compile error — `FetchRosterAsync` does not exist.

- [ ] **Step 3: Add the roster methods**

Add `using System.Globalization;` and `using OnDeck.Core.Utilities;`, then insert after the teams region:

```csharp
    // MARK: - Fetch Roster

    public async Task<IReadOnlyList<FantraxPlayer>> FetchRosterAsync(
        string leagueId, string teamId, CancellationToken ct = default)
    {
        // getTeamRosterInfo defaults to NEXT period's lineup, not today's - so discover
        // today's period from the first call, then re-fetch pinned to it.
        var data = new Dictionary<string, string> { ["leagueId"] = leagueId, ["teamId"] = teamId };

        using var initial = await PostRequestAsync(leagueId, "getTeamRosterInfo", data, ct);
        var todayPeriod = FindTodayPeriod(initial.RootElement);

        JsonDocument document = initial;
        JsonDocument? refetched = null;
        try
        {
            if (todayPeriod is not null)
            {
                var pinned = new Dictionary<string, string>(data) { ["period"] = todayPeriod };
                refetched = await PostRequestAsync(leagueId, "getTeamRosterInfo", pinned, ct);
                document = refetched;
            }

            var players = ParseRoster(document.RootElement);
            if (players.Count == 0) throw FantraxException.InvalidResponse();

            return players;
        }
        finally
        {
            refetched?.Dispose();
        }
    }

    /// <summary>
    /// Navigate to <c>responses[0].data.tables</c> and extract the top-level <c>scorer</c>
    /// from each row. Don't recurse into cells — they contain opposing pitcher popovers.
    /// </summary>
    private static List<FantraxPlayer> ParseRoster(JsonElement root)
    {
        var players = new List<FantraxPlayer>();

        if (!TryGetFirstResponseData(root, out var data)) return players;
        if (!data.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array)
        {
            return players;
        }

        foreach (var table in tables.EnumerateArray())
        {
            if (!table.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty("scorer", out var scorer) || scorer.ValueKind != JsonValueKind.Object) continue;
                if (!scorer.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) continue;
                if (!scorer.TryGetProperty("scorerId", out _)) continue;

                var teamShortName = StringOrEmpty(scorer, "teamShortName");
                var positionText = StringOrEmpty(scorer, "posShortNames");
                var positions = positionText.Length == 0
                    ? []
                    : positionText.Split(',').Select(part => part.Trim()).ToArray();

                players.Add(new FantraxPlayer(
                    nameElement.GetString()!, teamShortName, positions, StatusId(row)));
            }
        }

        return players;
    }

    private static int StatusId(JsonElement row)
    {
        if (!row.TryGetProperty("statusId", out var status)) return 1;

        return status.ValueKind switch
        {
            JsonValueKind.Number when status.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(status.GetString(), out var parsed) => parsed,
            _ => 1,
        };
    }

    private static string StringOrEmpty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // MARK: - Period Detection

    /// <summary>
    /// Parses <c>periodList</c> from the API response to find today's period number. Entries
    /// look like "14 (Tue Apr 7)". Uses the baseball day (before 8 AM = yesterday).
    /// </summary>
    private string? FindTodayPeriod(JsonElement root)
    {
        if (!TryGetFirstResponseData(root, out var data)) return null;
        if (!data.TryGetProperty("displayedLists", out var lists) || lists.ValueKind != JsonValueKind.Object) return null;
        if (!lists.TryGetProperty("periodList", out var periodList) || periodList.ValueKind != JsonValueKind.Array) return null;

        var target = BaseballCalendar.Today(timeProvider);

        // Fantrax emits English month abbreviations regardless of locale.
        string[] monthAbbreviations =
            ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        var suffix = $"{monthAbbreviations[target.Month - 1]} {target.Day})";

        foreach (var entry in periodList.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            if (entry.GetString() is not { } text) continue;
            if (!text.Contains(suffix, StringComparison.Ordinal)) continue;

            var space = text.IndexOf(' ');
            if (space > 0) return text[..space];
        }

        return null;
    }

    private static bool TryGetFirstResponseData(JsonElement root, out JsonElement data)
    {
        data = default;

        if (!root.TryGetProperty("responses", out var responses)
            || responses.ValueKind != JsonValueKind.Array
            || responses.GetArrayLength() == 0)
        {
            return false;
        }

        var first = responses[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("data", out var candidate)
            || candidate.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        data = candidate;
        return true;
    }
```

Also change `PostRequestAsync`'s `data` parameter type to `IReadOnlyDictionary<string, string>` so both call sites compile, and update `FetchTeamsAsync` accordingly.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~FantraxApiRosterTests`
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
git commit -m "phase 3: FantraxApi roster fetch with today's-period detection"
```

---

## Done criteria

- `dotnet build` and `dotnet test` green; single-file publish still produces `OnDeck.App.exe`.
- `OnDeck.Core` still has zero package references (`Microsoft.Extensions.TimeProvider.Testing` is test-project-only).
- Every endpoint in `MLBStatsAPI.swift` and `FantraxAPI.swift` has a C# counterpart except the deliberately-dropped `findScorers`.
- Request URLs and POST bodies are asserted, not just response parsing — the `hydrate` terms, the timecode formats, and the `period` parameter are the things that silently break.
