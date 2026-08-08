# Phase 5: AppOrchestrator — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the portable logic of `App/AppState.swift` plus the sorting/filter rules of `Views/MenuBarView.swift` into `Core/AppOrchestrator.cs`, completing `OnDeck.Core`.

**Architecture:** `AppOrchestrator` owns the four managers, subscribes to `StateManager.OnStateChange` / `GameMonitor.OnLineupUpdate` / `GameMonitor.OnGameStart`, and publishes four immutable `PlayerDisplay` snapshots plus sync/team-picker state. Everything runs on one logical thread (the WPF `Dispatcher` in the app, a pumping single-threaded `SynchronizationContext` in tests) — the `isStillActive` race guard and the coalesced list rebuild are only correct under that serialization. Notifications go out through `INotificationSink`, implemented by the shell in Phase 9.

**Tech Stack:** .NET 10, `TimeProvider` + `FakeTimeProvider`, xunit, a URL-routing `HttpMessageHandler` test double.

## Global Constraints

- `OnDeck.Core` must have **zero** Windows-specific dependencies and **zero** package references.
- **No `ConfigureAwait(false)` anywhere in Core.** The race guards depend on continuations returning to the captured context.
- Public list properties are **immutable snapshots** — `IReadOnlyList` replaced wholesale, never mutated in place.
- The `AppOrchestrator` and `INotificationSink` shapes in `PORT_PLAN.md` § "Cross-Phase Interface Contracts" are **binding**. Every member listed there must exist with that exact name and type. Additive members are allowed; renames are not.
- Mirror Swift names 1:1 where possible. Swift `Date` → `DateTimeOffset`, Swift `ID` suffix → `Id`.
- macOS-only concerns are **not ported**: `MemoryPressureRelief.releaseReclaimablePages()`, `NotificationManager.requestPermission()` (a shell concern in Phase 9), `FloatingPanel` auto-open (Phase 7).
- Commands run from the repo root; the solution is `windows/OnDeck.slnx`.

## Load-bearing behaviours (from CLAUDE.md and the Swift source)

These break silently. Each gets a dedicated test.

1. **15-min pre-game resync must not fire once games have started.** `AppState.swift:504-508`: if the refresh time is already past, skip. Resyncing after start restarts monitoring in a loop that cancels in-flight requests.
2. **Seed lineups AFTER `StartMonitoring`.** `StartMonitoring` calls `StopMonitoring` internally, clearing `LineupPlayerIds`.
3. **`isStillActive` re-check after every await.** After an async notification send, re-check the player's state; if it changed, purge — otherwise a stale toast sticks. This is why Core never uses `ConfigureAwait(false)`.
4. **`notifiedNotInLineup` is a one-shot set**, cleared on every schedule refresh; `PurgeAllAsync` runs at the top of every schedule refresh, `PurgeNotInLineupAsync(gamePk)` on game start.
5. **Don't notify not-in-lineup once the game has started.** Prefer live-feed state (`Live`/`Final`); fall back to the scheduled start time only when no feed exists yet.
6. **Coalesced rebuild.** One poll cycle fires 10+ state updates; a dirty flag plus one posted continuation collapses them into a single rebuild.
7. **Done section filters by stat line** — pitcher-only players need a *pitching* line, everyone else a *batting* line. No line, no row.
8. **The bench filter applies everywhere** — list building *and* transition notifications.
9. **`SettingsChanged()` never touches the network.** It is the `hideBenchPlayers` `didSet` analog: re-read settings, rebuild locally.

## File Structure

| File | Responsibility |
|---|---|
| `src/OnDeck.Core/INotificationSink.cs` | The notification surface Core calls; implemented by the shell |
| `src/OnDeck.Core/Models/PlayerDisplay.cs` | `PlayerDisplay` row + `BattingProximity`, `LineupInfo`, `DelayIndicator` |
| `src/OnDeck.Core/DisplayRules.cs` | Pure `MenuBarView.swift` rules: proximity, In Game sort key, stat line, lineup badge |
| `src/OnDeck.Core/AppOrchestrator.cs` | Lists, sync flows, reconciliation, transitions, schedules |
| `tests/OnDeck.Core.Tests/SingleThreadedContext.cs` | Pumping single-threaded `SynchronizationContext` fixture |
| `tests/OnDeck.Core.Tests/RecordingNotificationSink.cs` | `INotificationSink` double recording an ordered call log |
| `tests/OnDeck.Core.Tests/Networking/RoutingHttpMessageHandler.cs` | URL-routed HTTP double (the FIFO stub can't serve concurrent player searches) |
| `tests/OnDeck.Core.Tests/App/OrchestratorHarness.cs` | Composes managers + routes + orchestrator for the whole phase |
| `tests/OnDeck.Core.Tests/App/*Tests.cs` | One file per task from Task 2 on |

---

## Task 1: Notification sink and test infrastructure

**Files:**
- Create: `src/OnDeck.Core/INotificationSink.cs`
- Create: `tests/OnDeck.Core.Tests/RecordingNotificationSink.cs`
- Create: `tests/OnDeck.Core.Tests/SingleThreadedContext.cs`
- Create: `tests/OnDeck.Core.Tests/SingleThreadedContextTests.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/RoutingHttpMessageHandler.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/RoutingHttpMessageHandlerTests.cs`

**Interfaces:**
- Produces: `OnDeck.Core.INotificationSink` — copied verbatim from `PORT_PLAN.md`. Every later task calls it.
- Produces: `SingleThreadedContext.Run(Func<Task> body)` and `SingleThreadedContext.Settle(int rounds = 8)`; every orchestrator test body runs inside `Run`.
- Produces: `RecordingNotificationSink` with `List<string> Calls` and a `Func<Task>? DuringNotify` hook (Task 8's race-guard tests mutate state inside it).
- Produces: `RoutingHttpMessageHandler` with `MapJson(string urlSubstring, string json)`, `MapJson(string urlSubstring, Func<HttpRequestMessage, string, string> respond)`, `MapStatus(string urlSubstring, HttpStatusCode status)`, `CountRequests(string urlSubstring)`, `Requests`, `RequestBodies`, `CreateClient()`.

**Why a second HTTP double:** `StubHttpMessageHandler` replays a FIFO queue. `RosterManager` resolves every player's MLB ID concurrently through `Task.WhenAll`, so name→ID mapping would be non-deterministic. Routing by URL fixes that and lets one handler serve Fantrax, search, schedule and feed in a single test.

- [ ] **Step 1: Write the failing tests**

Create `tests/OnDeck.Core.Tests/SingleThreadedContextTests.cs`:

```csharp
namespace OnDeck.Core.Tests;

public class SingleThreadedContextTests
{
    [Fact]
    public void Run_RunsEveryContinuationOnOneThread()
    {
        var threads = new List<int>();

        SingleThreadedContext.Run(async () =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Delay(1);
            threads.Add(Environment.CurrentManagedThreadId);
        });

        Assert.Equal(3, threads.Count);
        Assert.Single(threads.Distinct());
    }

    [Fact]
    public void Run_PumpsPostedCallbacks()
    {
        var ran = false;

        SingleThreadedContext.Run(async () =>
        {
            SynchronizationContext.Current!.Post(_ => ran = true, null);
            Assert.False(ran);              // posted work is queued, not immediate
            await SingleThreadedContext.Settle();
            Assert.True(ran);
        });

        Assert.True(ran);
    }

    [Fact]
    public void Run_PropagatesExceptions()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            SingleThreadedContext.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal("boom", thrown.Message);
    }

    [Fact]
    public void Run_RestoresThePreviousContext()
    {
        var before = SynchronizationContext.Current;

        SingleThreadedContext.Run(() => Task.CompletedTask);

        Assert.Same(before, SynchronizationContext.Current);
    }
}
```

Create `tests/OnDeck.Core.Tests/Networking/RoutingHttpMessageHandlerTests.cs`:

```csharp
using System.Net;

namespace OnDeck.Core.Tests.Networking;

public class RoutingHttpMessageHandlerTests
{
    [Fact]
    public async Task MapJson_RoutesByUrlSubstring()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapJson("/alpha", """{"which":"alpha"}""");
        handler.MapJson("/beta", """{"which":"beta"}""");
        var client = handler.CreateClient();

        Assert.Equal("""{"which":"beta"}""", await client.GetStringAsync("https://example.com/beta"));
        Assert.Equal("""{"which":"alpha"}""", await client.GetStringAsync("https://example.com/alpha"));
    }

    [Fact]
    public async Task MapJson_RespondersSeeTheRequestAndItsBody()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapJson("/echo", (request, body) => $$"""{"path":"{{request.RequestUri!.AbsolutePath}}","body":{{body}}}""");
        var client = handler.CreateClient();

        var response = await client.PostAsync("https://example.com/echo", new StringContent("""{"n":1}"""));

        Assert.Equal("""{"path":"/echo","body":{"n":1}}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MapJson_ReplacesAnEarlierRouteWithTheSameKey()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapJson("/thing", """{"v":1}""");
        handler.MapJson("/thing", """{"v":2}""");

        Assert.Equal("""{"v":2}""", await handler.CreateClient().GetStringAsync("https://example.com/thing"));
    }

    [Fact]
    public async Task MapStatus_ReturnsTheStatusWithoutABody()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapStatus("/down", HttpStatusCode.ServiceUnavailable);

        var response = await handler.CreateClient().GetAsync("https://example.com/down");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task CountRequests_CountsMatchingUrls()
    {
        var handler = new RoutingHttpMessageHandler();
        handler.MapJson("/x", "{}");
        var client = handler.CreateClient();

        await client.GetAsync("https://example.com/x?a=1");
        await client.GetAsync("https://example.com/x?a=2");

        Assert.Equal(2, handler.CountRequests("/x"));
        Assert.Equal(1, handler.CountRequests("a=2"));
    }

    [Fact]
    public async Task SendAsync_ThrowsForAnUnroutedUrl()
    {
        var handler = new RoutingHttpMessageHandler();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.CreateClient().GetAsync("https://example.com/missing"));

        Assert.Contains("no route", thrown.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test windows/OnDeck.slnx --filter "FullyQualifiedName~SingleThreadedContextTests|FullyQualifiedName~RoutingHttpMessageHandlerTests"`
Expected: build failure — `SingleThreadedContext` and `RoutingHttpMessageHandler` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/OnDeck.Core/INotificationSink.cs`:

```csharp
namespace OnDeck.Core;

/// <summary>
/// The notification surface Core calls, implemented by the shell (Phase 9's
/// <c>ToastService</c>). Mirrors <c>Notifications/NotificationManager.swift</c> 1:1 —
/// Core drives it directly from its transition and reconcile logic exactly as
/// <c>AppState</c> does on macOS, so the <c>isStillActive</c> race-guard purges stay
/// inside Core. Implementations check the per-type <see cref="ISettingsStore"/> toggles
/// (as the Mac's <c>NotificationManager</c> does) and no-op when disabled.
/// </summary>
public interface INotificationSink
{
    Task NotifyBattingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl);

    Task NotifyPitchingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl);

    Task NotifyAtBatResultAsync(string playerName, int playerId, string description, Uri? streamUrl);

    Task NotifyPitchingResultAsync(string playerName, int playerId, string description, Uri? streamUrl);

    Task NotifyNotInLineupAsync(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl);

    void PurgeBatting(int gamePk, int playerId);

    void PurgePitching(int gamePk, int playerId);

    /// <summary>Game-scoped: players never in the lineup have no transition to hang this on.</summary>
    Task PurgeNotInLineupAsync(int gamePk);

    /// <summary>Schedule refresh / day rollover.</summary>
    Task PurgeAllAsync();
}
```

Create `tests/OnDeck.Core.Tests/SingleThreadedContext.cs`:

```csharp
using System.Collections.Concurrent;

namespace OnDeck.Core.Tests;

/// <summary>
/// A pumping single-threaded <see cref="SynchronizationContext"/> standing in for the WPF
/// <c>Dispatcher</c> that Core runs on in the app. Every continuation and every posted
/// callback runs on the thread that called <see cref="Run"/>, in FIFO order — which is what
/// makes the <c>isStillActive</c> race guard and the coalesced list rebuild deterministic
/// under test.
/// </summary>
internal sealed class SingleThreadedContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The pump has finished; work posted after that is irrelevant to the assertions.
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>
    /// Runs <paramref name="body"/> under a fresh context, pumping until it completes.
    /// Exceptions from the body surface to the caller.
    /// </summary>
    public static void Run(Func<Task> body)
    {
        var previous = Current;
        var context = new SingleThreadedContext();
        SetSynchronizationContext(context);

        try
        {
            var task = body();
            task.ContinueWith(
                _ => context._queue.CompleteAdding(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            foreach (var (callback, state) in context._queue.GetConsumingEnumerable()) callback(state);

            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Yields repeatedly so queued continuations — and the continuations they queue in turn —
    /// all get pumped before the assertions run. Each yield lets one generation of queued work
    /// run, so a chain like resync → schedule fetch → rebuild needs a couple of dozen.
    /// </summary>
    public static async Task Settle(int rounds = 32)
    {
        for (var i = 0; i < rounds; i++) await Task.Yield();
    }
}
```

Create `tests/OnDeck.Core.Tests/RecordingNotificationSink.cs`:

```csharp
namespace OnDeck.Core.Tests;

/// <summary>
/// Records an ordered call log. <see cref="DuringNotify"/> runs at the await point inside
/// every <c>Notify*</c> method, which is where the race-guard tests mutate player state.
/// </summary>
internal sealed class RecordingNotificationSink : INotificationSink
{
    public List<string> Calls { get; } = [];

    public Func<Task>? DuringNotify { get; set; }

    public async Task NotifyBattingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl)
    {
        Calls.Add($"batting:{playerId}:{gamePk}:{game}:{inning}:{streamUrl}");
        await RunHook();
    }

    public async Task NotifyPitchingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl)
    {
        Calls.Add($"pitching:{playerId}:{gamePk}:{game}:{inning}:{streamUrl}");
        await RunHook();
    }

    public async Task NotifyAtBatResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl)
    {
        Calls.Add($"atBatResult:{playerId}:{description}");
        await RunHook();
    }

    public async Task NotifyPitchingResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl)
    {
        Calls.Add($"pitchingResult:{playerId}:{description}");
        await RunHook();
    }

    public async Task NotifyNotInLineupAsync(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl)
    {
        Calls.Add($"notInLineup:{playerId}:{gamePk}:{game}:{fantraxUrl}");
        await RunHook();
    }

    public void PurgeBatting(int gamePk, int playerId) => Calls.Add($"purgeBatting:{playerId}:{gamePk}");

    public void PurgePitching(int gamePk, int playerId) => Calls.Add($"purgePitching:{playerId}:{gamePk}");

    public Task PurgeNotInLineupAsync(int gamePk)
    {
        Calls.Add($"purgeNotInLineup:{gamePk}");
        return Task.CompletedTask;
    }

    public Task PurgeAllAsync()
    {
        Calls.Add("purgeAll");
        return Task.CompletedTask;
    }

    private Task RunHook() => DuringNotify?.Invoke() ?? Task.CompletedTask;
}
```

Create `tests/OnDeck.Core.Tests/Networking/RoutingHttpMessageHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace OnDeck.Core.Tests.Networking;

/// <summary>
/// Routes responses by URL substring instead of a FIFO queue, so concurrent requests (the
/// per-player MLB ID searches <see cref="OnDeck.Core.Managers.RosterManager"/> fans out with
/// <c>Task.WhenAll</c>) stay deterministic and one handler can serve Fantrax, search,
/// schedule and feed in the same test. Routes are matched in registration order; re-mapping
/// a key replaces the earlier route.
/// </summary>
internal sealed class RoutingHttpMessageHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];
    private readonly Lock _gate = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public void MapJson(string urlSubstring, string json) => Map(urlSubstring, (_, _) => (HttpStatusCode.OK, json));

    public void MapJson(string urlSubstring, Func<HttpRequestMessage, string, string> respond) =>
        Map(urlSubstring, (request, body) => (HttpStatusCode.OK, respond(request, body)));

    public void MapStatus(string urlSubstring, HttpStatusCode status) =>
        Map(urlSubstring, (_, _) => (status, ""));

    public int CountRequests(string urlSubstring)
    {
        lock (_gate)
        {
            return Requests.Count(
                request => request.RequestUri!.AbsoluteUri.Contains(urlSubstring, StringComparison.Ordinal));
        }
    }

    public HttpClient CreateClient() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        Route route;
        lock (_gate)
        {
            Requests.Add(request);
            RequestBodies.Add(body);

            var url = request.RequestUri!.AbsoluteUri;
            route = _routes.FirstOrDefault(candidate => url.Contains(candidate.Key, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"no route for {url}");
        }

        var (status, responseBody) = route.Respond(request, body);
        var content = new StringContent(responseBody, Encoding.UTF8, "application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    private void Map(string key, Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond)
    {
        lock (_gate)
        {
            _routes.RemoveAll(route => route.Key == key);
            _routes.Add(new Route(key, respond));
        }
    }

    private sealed record Route(
        string Key, Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> Respond);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test windows/OnDeck.slnx --filter "FullyQualifiedName~SingleThreadedContextTests|FullyQualifiedName~RoutingHttpMessageHandlerTests"`
Expected: PASS, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/INotificationSink.cs windows/tests/OnDeck.Core.Tests
git commit -m "phase 5: INotificationSink and orchestrator test infrastructure"
```

---

## Task 2: Display models and batting proximity

**Files:**
- Create: `src/OnDeck.Core/Models/PlayerDisplay.cs`
- Create: `src/OnDeck.Core/DisplayRules.cs`
- Create: `tests/OnDeck.Core.Tests/App/DisplayRulesProximityTests.cs`

**Spec:** `Views/MenuBarView.swift:3-95` (`BattingProximity`, `battingProximity(for:in:)`).

**Interfaces:**
- Produces: `public enum BattingProximityKind { AtBat, OnDeck, DueUp, Order, NotBatting }`.
- Produces: `public readonly record struct BattingProximity` with `Kind`, `Value`, `SortKey`, statics `AtBat`/`OnDeck`/`DueUp` and factories `Order(int distance)` / `NotBatting(int spot)`.
- Produces: `public enum LineupInfoKind`, `public readonly record struct LineupInfo`, `public enum DelayIndicator`, `public sealed record PlayerDisplay` (fields used by Tasks 4-8 and the Phase 7 shell).
- Produces: `internal static class DisplayRules` with `BattingProximity? ProximityFor(Player player, LiveFeedData? feed)`. Tasks 3-4 add the rest of its members.

**Why value-struct cases instead of a record hierarchy:** Swift's `.onDeck` case would become a nested type named `OnDeck` inside namespace `OnDeck.Core`, which makes the bare identifier `OnDeck` ambiguous at type-name position. A `Kind` + `Value` struct sidesteps that and keeps `BattingProximity?` a cheap nullable.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/App/DisplayRulesProximityTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class DisplayRulesProximityTests
{
    private static Player Hitter(int id) =>
        new(id, $"Player {id}", "Los Angeles Dodgers",
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static Player PitcherOnly(int id) =>
        new(id, $"Pitcher {id}", "Los Angeles Dodgers",
            new HashSet<PlayerPosition> { PlayerPosition.Pitcher },
            new HashSet<string> { "SP" },
            RosterStatus.Active);

    /// <summary>Home team batting in the bottom half, current batter is 101.</summary>
    private static LiveFeedData HomeBatting(int currentBatterId = 101) => new()
    {
        GameState = "Live",
        DetailedState = "In Progress",
        InningHalf = "Bottom",
        InningState = "Bottom",
        CurrentBatterId = currentBatterId,
        HomeBattingOrder = [101, 102, 103, 104, 105, 106, 107, 108, 109],
        AwayBattingOrder = [201, 202, 203],
    };

    [Fact]
    public void ProximityFor_IsNullWithoutAFeed()
    {
        Assert.Null(DisplayRules.ProximityFor(Hitter(101), null));
    }

    [Fact]
    public void ProximityFor_IsNullForPitcherOnlyPlayers()
    {
        var feed = HomeBatting();
        feed.HomeBattingOrder = [901, 102, 103];

        Assert.Null(DisplayRules.ProximityFor(PitcherOnly(901), feed));
    }

    [Fact]
    public void ProximityFor_IsNullWhenTheePlayerIsInNeitherBattingOrder()
    {
        Assert.Null(DisplayRules.ProximityFor(Hitter(999), HomeBatting()));
    }

    [Fact]
    public void ProximityFor_MapsDistanceZeroToAtBat()
    {
        Assert.Equal(BattingProximityKind.AtBat, DisplayRules.ProximityFor(Hitter(101), HomeBatting())!.Value.Kind);
    }

    [Fact]
    public void ProximityFor_MapsDistanceOneToOnDeckAndTwoToDueUp()
    {
        Assert.Equal(BattingProximityKind.OnDeck, DisplayRules.ProximityFor(Hitter(102), HomeBatting())!.Value.Kind);
        Assert.Equal(BattingProximityKind.DueUp, DisplayRules.ProximityFor(Hitter(103), HomeBatting())!.Value.Kind);
    }

    [Fact]
    public void ProximityFor_MapsFurtherDistancesToOrder()
    {
        var proximity = DisplayRules.ProximityFor(Hitter(105), HomeBatting())!.Value;

        Assert.Equal(BattingProximityKind.Order, proximity.Kind);
        Assert.Equal(4, proximity.Value);
    }

    [Fact]
    public void ProximityFor_WrapsSoTheJustBattedHitterSinks()
    {
        // 109 batted immediately before 101 - distance 8, the bottom of the live band.
        var proximity = DisplayRules.ProximityFor(Hitter(109), HomeBatting())!.Value;

        Assert.Equal(BattingProximityKind.Order, proximity.Kind);
        Assert.Equal(8, proximity.Value);
    }

    [Fact]
    public void ProximityFor_FallsToNotBattingBetweenHalfInnings()
    {
        // MLB keeps currentBatter/inningHalf as a stale holdover during the break, so the
        // third-out hitter would still look at bat.
        var feed = HomeBatting();
        feed.InningState = "Middle";

        var proximity = DisplayRules.ProximityFor(Hitter(101), feed)!.Value;

        Assert.Equal(BattingProximityKind.NotBatting, proximity.Kind);
        Assert.Equal(0, proximity.Value);       // lineup spot index
    }

    [Fact]
    public void ProximityFor_FallsToNotBattingWhenTheOtherTeamIsUp()
    {
        var feed = HomeBatting();
        feed.InningHalf = "Top";
        feed.InningState = "Top";

        var proximity = DisplayRules.ProximityFor(Hitter(103), feed)!.Value;

        Assert.Equal(BattingProximityKind.NotBatting, proximity.Kind);
        Assert.Equal(2, proximity.Value);
    }

    [Fact]
    public void ProximityFor_FallsToNotBattingWhenTheCurrentBatterIsUnknown()
    {
        var feed = HomeBatting();
        feed.CurrentBatterId = null;

        Assert.Equal(BattingProximityKind.NotBatting, DisplayRules.ProximityFor(Hitter(103), feed)!.Value.Kind);
    }

    [Fact]
    public void ProximityFor_ReadsTheAwayOrderForAwayHitters()
    {
        var feed = HomeBatting();
        feed.InningHalf = "Top";
        feed.InningState = "Top";
        feed.CurrentBatterId = 201;

        Assert.Equal(BattingProximityKind.OnDeck, DisplayRules.ProximityFor(Hitter(202), feed)!.Value.Kind);
    }

    [Theory]
    [InlineData(BattingProximityKind.AtBat, 0, 0)]
    [InlineData(BattingProximityKind.OnDeck, 0, 1)]
    [InlineData(BattingProximityKind.DueUp, 0, 2)]
    [InlineData(BattingProximityKind.Order, 5, 5)]
    [InlineData(BattingProximityKind.NotBatting, 3, 53)]
    public void SortKey_PutsNotBattingInItsOwnBandAboveTheLiveOnes(
        BattingProximityKind kind, int value, int expected)
    {
        var proximity = kind switch
        {
            BattingProximityKind.AtBat => BattingProximity.AtBat,
            BattingProximityKind.OnDeck => BattingProximity.OnDeck,
            BattingProximityKind.DueUp => BattingProximity.DueUp,
            BattingProximityKind.Order => BattingProximity.Order(value),
            _ => BattingProximity.NotBatting(value),
        };

        Assert.Equal(expected, proximity.SortKey);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~DisplayRulesProximityTests`
Expected: build failure — `DisplayRules` and `BattingProximity` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/OnDeck.Core/Models/PlayerDisplay.cs`:

```csharp
namespace OnDeck.Core.Models;

/// <summary>
/// Port of the private <c>BattingProximity</c> enum in <c>Views/MenuBarView.swift</c>.
/// Swift's associated values become <see cref="Value"/>: the distance from the current
/// batter for <see cref="BattingProximityKind.Order"/>, the lineup spot index for
/// <see cref="BattingProximityKind.NotBatting"/>, unused otherwise.
/// </summary>
public enum BattingProximityKind
{
    AtBat,
    OnDeck,
    DueUp,
    Order,
    NotBatting,
}

/// <summary>
/// A hitter's distance from the plate. <c>default</c> is <see cref="AtBat"/> — callers use
/// <c>BattingProximity?</c> to mean "no proximity" (pitcher-only, not in the order, no feed).
/// </summary>
public readonly record struct BattingProximity
{
    private BattingProximity(BattingProximityKind kind, int value)
    {
        Kind = kind;
        Value = value;
    }

    public BattingProximityKind Kind { get; }

    public int Value { get; }

    public static readonly BattingProximity AtBat = new(BattingProximityKind.AtBat, 0);

    public static readonly BattingProximity OnDeck = new(BattingProximityKind.OnDeck, 0);

    public static readonly BattingProximity DueUp = new(BattingProximityKind.DueUp, 0);

    /// <param name="distance">Distance from the current batter, 3...8.</param>
    public static BattingProximity Order(int distance) => new(BattingProximityKind.Order, distance);

    /// <param name="spot">Lineup spot index; the other team is up.</param>
    public static BattingProximity NotBatting(int spot) => new(BattingProximityKind.NotBatting, spot);

    /// <summary>
    /// Distance-based while the team is batting (0 = at bat, 8 = just finished) so the player
    /// who just batted sinks and bubbles back up as the lineup cycles. <c>notBatting</c> bumps
    /// into a separate band so a leadoff hitter on a non-batting team doesn't tie with on-deck.
    /// </summary>
    public int SortKey => Kind switch
    {
        BattingProximityKind.AtBat => 0,
        BattingProximityKind.OnDeck => 1,
        BattingProximityKind.DueUp => 2,
        BattingProximityKind.Order => Value,
        _ => 50 + Value,
    };
}

/// <summary>Port of <c>UpcomingPlayerRow.LineupInfo</c> in <c>Views/MenuBarView.swift</c>.</summary>
public enum LineupInfoKind
{
    Unknown,
    NotInLineup,
    InLineup,
    BattingOrder,
}

/// <summary>The upcoming-row lineup badge. <c>default</c> is <see cref="Unknown"/>.</summary>
public readonly record struct LineupInfo
{
    private LineupInfo(LineupInfoKind kind, int spot)
    {
        Kind = kind;
        Spot = spot;
    }

    public LineupInfoKind Kind { get; }

    /// <summary>1-based batting order spot; 0 unless <see cref="Kind"/> is BattingOrder.</summary>
    public int Spot { get; }

    public static readonly LineupInfo Unknown = new(LineupInfoKind.Unknown, 0);

    public static readonly LineupInfo NotInLineup = new(LineupInfoKind.NotInLineup, 0);

    public static readonly LineupInfo InLineup = new(LineupInfoKind.InLineup, 0);

    public static LineupInfo BattingOrder(int spot) => new(LineupInfoKind.BattingOrder, spot);
}

/// <summary>
/// Port of <c>delayIcon(detailedState:)</c> in <c>Views/MenuBarView.swift</c>. The icon
/// choice itself is the shell's; Core only classifies. Shared by UPCOMING (pre-game
/// "Delayed Start: Rain") and IN GAME (mid-game "Delayed: Rain").
/// </summary>
public enum DelayIndicator
{
    None,
    Rain,
    Delayed,
    Postponed,
}

/// <summary>
/// One rendered row. Fields are exactly what <c>Views/MenuBarView.swift</c> reads out of
/// <c>AppState</c> for a player, resolved once on the Core context so the shell never has to
/// reach back into <c>GameMonitor</c> while rendering.
/// </summary>
public sealed record PlayerDisplay
{
    public required Player Player { get; init; }

    /// <summary>The game this player's team is in today, if any.</summary>
    public int? GamePk { get; init; }

    /// <summary>
    /// Latest feed for <see cref="GamePk"/>. The live row reads score, bases, count, outs,
    /// inning and half off it directly.
    /// </summary>
    public LiveFeedData? Feed { get; init; }

    /// <summary>True when the player's state is <c>Active</c> (at bat or on the mound).</summary>
    public bool IsActive { get; init; }

    public BattingProximity? Proximity { get; init; }

    /// <summary>False only when this player's own side filed a card without them.</summary>
    public bool IsInLineup { get; init; } = true;

    /// <summary>The UPCOMING row badge; <see cref="LineupInfo.Unknown"/> for other sections.</summary>
    public LineupInfo Lineup { get; init; }

    /// <summary>
    /// The secondary line: "Not in Lineup", a delay label, an "On Deck"/"In Hole" prefix and
    /// the boxscore stat line, composed per section.
    /// </summary>
    public string? StatLine { get; init; }

    public DelayIndicator Delay { get; init; }

    /// <summary>Scheduled first pitch; set on UPCOMING rows only.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Where a click on the row goes.</summary>
    public Uri? StreamUrl { get; init; }

    /// <summary>IN GAME ordering key; 0 for other sections.</summary>
    public int SortKey { get; init; }

    public int Id => Player.Id;

    public string Name => Player.Name;
}
```

Create `src/OnDeck.Core/DisplayRules.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core;

/// <summary>
/// The display rules from <c>Views/MenuBarView.swift</c>, as pure functions over the feed and
/// lineup snapshots. <see cref="AppOrchestrator"/> calls these while building
/// <see cref="PlayerDisplay"/> rows.
/// </summary>
internal static class DisplayRules
{
    /// <summary>
    /// Port of <c>battingProximity(for:in:)</c>. Null for pitcher-only players, players in
    /// neither batting order, and before the first feed arrives.
    /// </summary>
    public static BattingProximity? ProximityFor(Player player, LiveFeedData? feed)
    {
        if (feed is null) return null;
        if (player.IsPitcher && !player.IsHitter) return null;

        bool isHome;
        if (feed.HomeBattingOrder.Contains(player.Id)) isHome = true;
        else if (feed.AwayBattingOrder.Contains(player.Id)) isHome = false;
        else return null;

        var battingOrder = isHome ? feed.HomeBattingOrder : feed.AwayBattingOrder;
        var playerIndex = battingOrder.IndexOf(player.Id);
        if (playerIndex < 0) return null;

        // Between half-innings MLB keeps currentBatter/inningHalf as a stale holdover from the
        // previous play, so the 3rd-out hitter would still look "at bat" until play resumes.
        var isBreak = feed.InningState is "Middle" or "End";
        var teamIsBatting = !isBreak
            && ((isHome && feed.InningHalf == "Bottom") || (!isHome && feed.InningHalf == "Top"));

        if (!teamIsBatting || feed.CurrentBatterId is not { } currentBatterId)
        {
            return BattingProximity.NotBatting(playerIndex);
        }

        var currentIndex = battingOrder.IndexOf(currentBatterId);
        if (currentIndex < 0) return BattingProximity.NotBatting(playerIndex);

        var distance = (playerIndex - currentIndex + battingOrder.Count) % battingOrder.Count;
        return distance switch
        {
            0 => BattingProximity.AtBat,
            1 => BattingProximity.OnDeck,
            2 => BattingProximity.DueUp,
            _ => BattingProximity.Order(distance),
        };
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~DisplayRulesProximityTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Models/PlayerDisplay.cs windows/src/OnDeck.Core/DisplayRules.cs windows/tests/OnDeck.Core.Tests/App
git commit -m "phase 5: PlayerDisplay model and batting proximity rules"
```

---

## Task 3: Sort key, stat line and lineup badge

**Files:**
- Modify: `src/OnDeck.Core/DisplayRules.cs`
- Create: `tests/OnDeck.Core.Tests/App/DisplayRulesTests.cs`

**Spec:** `Views/MenuBarView.swift:25-55` (`inGameSortKey`), `:355-362` (`isInLineup`), `:470-528` (`formattedStatLine`, `delayLabel`, `delayIcon`), `:550-573` (`lineupInfo`).

**Interfaces:**
- Consumes: `DisplayRules.ProximityFor` from Task 2.
- Produces, all on `DisplayRules`:
  - `int InGameSortKey(Player player, Game? game, LiveFeedData? feed, GameLineup? lineup, BattingProximity? proximity)`
  - `bool IsInLineup(Player player, Game? game, GameLineup? lineup)`
  - `string? RawStatLine(Player player, LiveFeedData? feed)`
  - `string? LiveStatLine(Player player, LiveFeedData? feed, bool isInLineup, BattingProximity? proximity)`
  - `string? DelayLabel(string? detailedState)`
  - `DelayIndicator DelayFor(string? detailedState)`
  - `LineupInfo LineupInfoFor(Player player, Game? game, GameLineup? lineup, LiveFeedData? feed)`

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/App/DisplayRulesTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class DisplayRulesTests
{
    private static Player Hitter(int id = 101, string team = "Los Angeles Dodgers") =>
        new(id, $"Player {id}", team,
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static Player PitcherOnly(int id = 901, string team = "Los Angeles Dodgers") =>
        new(id, $"Pitcher {id}", team,
            new HashSet<PlayerPosition> { PlayerPosition.Pitcher },
            new HashSet<string> { "SP" },
            RosterStatus.Active);

    private static Game DodgersHome(
        IReadOnlyList<int>? homeLineup = null, IReadOnlyList<int>? awayLineup = null) =>
        new(1, "Los Angeles Dodgers", "San Francisco Giants", 119, 137,
            new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero),
            null, null, [], homeLineup ?? [], awayLineup ?? []);

    private static LiveFeedData Feed(string detailedState = "In Progress") => new()
    {
        GameState = "Live",
        DetailedState = detailedState,
        InningHalf = "Bottom",
        InningState = "Bottom",
        CurrentBatterId = 101,
        HomeBattingOrder = [101, 102, 103],
    };

    // MARK: - InGameSortKey

    [Fact]
    public void InGameSortKey_UsesProximityWhenThePlayerHasOne()
    {
        var key = DisplayRules.InGameSortKey(
            Hitter(102), DodgersHome(), Feed(), null, BattingProximity.OnDeck);

        Assert.Equal(1, key);
    }

    [Fact]
    public void InGameSortKey_PutsThePitcherOnTheMoundInTheLiveBand()
    {
        var feed = Feed();
        feed.CurrentPitcherId = 901;

        Assert.Equal(0, DisplayRules.InGameSortKey(PitcherOnly(), DodgersHome(), feed, null, null));
    }

    [Fact]
    public void InGameSortKey_PutsOtherPitchersAboveNotBattingHitters()
    {
        Assert.Equal(70, DisplayRules.InGameSortKey(PitcherOnly(), DodgersHome(), Feed(), null, null));
    }

    [Fact]
    public void InGameSortKey_AddsOneHundredForAMidGameDelay()
    {
        Assert.Equal(
            101,
            DisplayRules.InGameSortKey(
                Hitter(102), DodgersHome(), Feed("Delayed: Rain"), null, BattingProximity.OnDeck));
    }

    [Fact]
    public void InGameSortKey_AddsOneHundredForASuspension()
    {
        Assert.Equal(
            170,
            DisplayRules.InGameSortKey(PitcherOnly(), DodgersHome(), Feed("Suspended: Rain"), null, null));
    }

    [Fact]
    public void InGameSortKey_AddsTwoHundredWhenTheFiledCardExcludesThePlayer()
    {
        var lineup = new GameLineup { Home = [102, 103] };

        Assert.Equal(
            250,
            DisplayRules.InGameSortKey(
                Hitter(199), DodgersHome(), Feed(), lineup, BattingProximity.NotBatting(0)));
    }

    [Fact]
    public void InGameSortKey_ExclusionOutranksDelay()
    {
        var lineup = new GameLineup { Home = [102, 103] };

        Assert.Equal(
            270,
            DisplayRules.InGameSortKey(Hitter(199), DodgersHome(), Feed("Delayed: Rain"), lineup, null));
    }

    [Fact]
    public void InGameSortKey_FallsBackWhenThePlayerHasNoGame()
    {
        Assert.Equal(70, DisplayRules.InGameSortKey(Hitter(), null, null, null, null));
        Assert.Equal(2, DisplayRules.InGameSortKey(Hitter(), null, null, null, BattingProximity.DueUp));
    }

    // MARK: - IsInLineup

    [Fact]
    public void IsInLineup_AssumesInUntilThatSidesCardIsFiled()
    {
        Assert.True(DisplayRules.IsInLineup(Hitter(199), DodgersHome(), null));
        Assert.True(DisplayRules.IsInLineup(Hitter(199), DodgersHome(), new GameLineup()));
    }

    [Fact]
    public void IsInLineup_IsFalseWhenTheFiledCardOmitsTheHitter()
    {
        Assert.False(
            DisplayRules.IsInLineup(Hitter(199), DodgersHome(), new GameLineup { Home = [101, 102] }));
    }

    [Fact]
    public void IsInLineup_IgnoresTheOpponentsCard()
    {
        Assert.True(
            DisplayRules.IsInLineup(Hitter(199), DodgersHome(), new GameLineup { Away = [201, 202] }));
    }

    // MARK: - Stat lines

    [Fact]
    public void RawStatLine_UsesPitchingStatsForPitcherOnlyPlayers()
    {
        var feed = Feed();
        feed.PlayerStats[901] = new PlayerGameStats
        {
            Pitching = new PlayerPitchingStats { InningsPitched = "5.0", StrikeOuts = 7, EarnedRuns = 1 },
            Batting = new PlayerBattingStats { AtBats = 2, Hits = 1 },
        };

        Assert.Equal("5.0 IP, 7K, 1ER", DisplayRules.RawStatLine(PitcherOnly(), feed));
    }

    [Fact]
    public void RawStatLine_UsesBattingStatsForEveryoneElse()
    {
        var feed = Feed();
        feed.PlayerStats[101] = new PlayerGameStats
        {
            Batting = new PlayerBattingStats { AtBats = 3, Hits = 2, HomeRuns = 1, Rbi = 2 },
        };

        Assert.Equal("2-3 · HR, 2 RBI", DisplayRules.RawStatLine(Hitter(), feed));
    }

    [Fact]
    public void RawStatLine_IsNullWithoutStats()
    {
        Assert.Null(DisplayRules.RawStatLine(Hitter(), Feed()));
        Assert.Null(DisplayRules.RawStatLine(Hitter(), null));
    }

    [Fact]
    public void LiveStatLine_ReportsNotInLineupAboveEverythingElse()
    {
        Assert.Equal("Not in Lineup", DisplayRules.LiveStatLine(Hitter(), Feed(), false, BattingProximity.AtBat));
    }

    [Fact]
    public void LiveStatLine_PrefixesOnDeckAndInHole()
    {
        var feed = Feed();
        feed.PlayerStats[101] = new PlayerGameStats
        {
            Batting = new PlayerBattingStats { AtBats = 2, Hits = 1 },
        };

        Assert.Equal("On Deck · 1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.OnDeck));
        Assert.Equal("In Hole · 1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.DueUp));
    }

    [Fact]
    public void LiveStatLine_HasNoPrefixAtBatOrDeeperInTheOrder()
    {
        var feed = Feed();
        feed.PlayerStats[101] = new PlayerGameStats
        {
            Batting = new PlayerBattingStats { AtBats = 2, Hits = 1 },
        };

        Assert.Equal("1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.AtBat));
        Assert.Equal("1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.Order(5)));
        Assert.Equal("1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, null));
    }

    [Fact]
    public void LiveStatLine_IsJustThePrefixWithoutAStatLine()
    {
        Assert.Equal("On Deck", DisplayRules.LiveStatLine(Hitter(), Feed(), true, BattingProximity.OnDeck));
    }

    [Fact]
    public void LiveStatLine_LeadsWithTheDelayLabel()
    {
        var feed = Feed("Delayed: Rain");
        feed.PlayerStats[101] = new PlayerGameStats
        {
            Batting = new PlayerBattingStats { AtBats = 2, Hits = 1 },
        };

        Assert.Equal("Rain Delay · 1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.OnDeck));
    }

    [Fact]
    public void LiveStatLine_IsTheDelayLabelAloneWithoutStats()
    {
        Assert.Equal("Rain Delay", DisplayRules.LiveStatLine(Hitter(), Feed("Delayed: Rain"), true, null));
    }

    [Fact]
    public void LiveStatLine_IsNullWithoutAFeed()
    {
        Assert.Null(DisplayRules.LiveStatLine(Hitter(), null, true, null));
    }

    [Theory]
    [InlineData("Delayed: Rain", "Rain Delay")]
    [InlineData("Suspended: Rain", "Suspended: Rain")]
    [InlineData("Delayed", "Delayed")]
    [InlineData("Suspended", "Suspended")]
    [InlineData("In Progress", null)]
    [InlineData(null, null)]
    public void DelayLabel_MatchesTheSwiftForms(string? detailedState, string? expected)
    {
        Assert.Equal(expected, DisplayRules.DelayLabel(detailedState));
    }

    [Theory]
    [InlineData("Delayed: Rain", DelayIndicator.Rain)]
    [InlineData("Delayed Start: Rain", DelayIndicator.Rain)]
    [InlineData("Suspended: Darkness", DelayIndicator.Delayed)]
    [InlineData("Delayed", DelayIndicator.Delayed)]
    [InlineData("Postponed", DelayIndicator.Postponed)]
    [InlineData("In Progress", DelayIndicator.None)]
    [InlineData(null, DelayIndicator.None)]
    public void DelayFor_ClassifiesTheDetailedState(string? detailedState, DelayIndicator expected)
    {
        Assert.Equal(expected, DisplayRules.DelayFor(detailedState));
    }

    // MARK: - LineupInfoFor

    [Fact]
    public void LineupInfoFor_IsUnknownBeforeThatSidesCardIsFiled()
    {
        Assert.Equal(
            LineupInfoKind.Unknown,
            DisplayRules.LineupInfoFor(Hitter(199), DodgersHome(), new GameLineup(), null).Kind);
    }

    [Fact]
    public void LineupInfoFor_IsNotInLineupWhenTheFiledCardOmitsTheHitter()
    {
        Assert.Equal(
            LineupInfoKind.NotInLineup,
            DisplayRules.LineupInfoFor(
                Hitter(199), DodgersHome(), new GameLineup { Home = [101, 102] }, null).Kind);
    }

    [Fact]
    public void LineupInfoFor_ReadsTheBattingOrderSpotFromTheFeedFirst()
    {
        var feed = Feed();
        feed.HomeBattingOrder = [105, 101, 103];
        var info = DisplayRules.LineupInfoFor(
            Hitter(), DodgersHome(homeLineup: [101, 105, 103]), new GameLineup { Home = [101, 105, 103] }, feed);

        Assert.Equal(LineupInfoKind.BattingOrder, info.Kind);
        Assert.Equal(2, info.Spot);
    }

    [Fact]
    public void LineupInfoFor_FallsBackToTheScheduleLineup()
    {
        var info = DisplayRules.LineupInfoFor(
            Hitter(), DodgersHome(homeLineup: [105, 103, 101]), new GameLineup { Home = [105, 103, 101] }, null);

        Assert.Equal(LineupInfoKind.BattingOrder, info.Kind);
        Assert.Equal(3, info.Spot);
    }

    [Fact]
    public void LineupInfoFor_IsInLineupWhenListedWithoutAKnownSpot()
    {
        // Probable starter: on the pitchers set, so not excluded, but on no batting order.
        var lineup = new GameLineup { Home = [101, 102], HomePitchers = [901] };

        Assert.Equal(
            LineupInfoKind.InLineup,
            DisplayRules.LineupInfoFor(PitcherOnly(), DodgersHome(), lineup, null).Kind);
    }

    [Fact]
    public void LineupInfoFor_IsUnknownWhenThePlayerHasNoGame()
    {
        Assert.Equal(LineupInfoKind.Unknown, DisplayRules.LineupInfoFor(Hitter(), null, null, null).Kind);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~DisplayRulesTests`
Expected: build failure — `InGameSortKey` and friends do not exist.

- [ ] **Step 3: Write the implementation**

Append to `src/OnDeck.Core/DisplayRules.cs`, inside the `DisplayRules` class:

```csharp
    /// <summary>
    /// Port of <c>inGameSortKey(for:proximity:in:)</c>. Tiers stack on top of the proximity
    /// sort: 0 = normal proximity, +100 = mid-game delay, +200 = lineup card filed without
    /// this player. Pitchers have no proximity — base 0 if currently pitching (live action,
    /// like at bat) or 70 otherwise (above notBatting hitters, below the delay tier).
    /// </summary>
    public static int InGameSortKey(
        Player player, Game? game, LiveFeedData? feed, GameLineup? lineup, BattingProximity? proximity)
    {
        if (game is null) return proximity?.SortKey ?? 70;

        var baseKey = proximity?.SortKey ?? (feed?.CurrentPitcherId == player.Id ? 0 : 70);

        // Not in Lineup: own side's card is filed and this player isn't on it.
        if (game.SideFor(player) is { } side && lineup is not null && lineup.Excludes(player, side))
        {
            return 200 + baseKey;
        }

        if (feed?.DetailedState is { } detailed
            && (detailed.StartsWith("Delayed", StringComparison.Ordinal)
                || detailed.StartsWith("Suspended", StringComparison.Ordinal)))
        {
            return 100 + baseKey;
        }

        return baseKey;
    }

    /// <summary>
    /// Port of <c>LivePlayerRow.isInLineup</c>. Assumes the player is in until that side's
    /// card is filed; false only when the filed card omits them.
    /// </summary>
    public static bool IsInLineup(Player player, Game? game, GameLineup? lineup)
    {
        if (game is null) return false;
        if (game.SideFor(player) is not { } side || lineup is null) return true;
        return !lineup.Excludes(player, side);
    }

    /// <summary>The boxscore line for this player's role, or null when they have no stats yet.</summary>
    public static string? RawStatLine(Player player, LiveFeedData? feed)
    {
        if (feed is null) return null;
        if (!feed.PlayerStats.TryGetValue(player.Id, out var stats)) return null;

        return player.IsPitcher && !player.IsHitter ? stats.Pitching?.Formatted : stats.Batting?.Formatted;
    }

    /// <summary>Port of <c>LivePlayerRow.formattedStatLine(gamePk:)</c>.</summary>
    public static string? LiveStatLine(
        Player player, LiveFeedData? feed, bool isInLineup, BattingProximity? proximity)
    {
        if (!isInLineup) return "Not in Lineup";
        if (feed is null) return null;

        var statLine = RawStatLine(player, feed);

        if (DelayLabel(feed.DetailedState) is { } delay)
        {
            return statLine is null ? delay : $"{delay} · {statLine}";
        }

        if (player.IsPitcher && !player.IsHitter) return statLine;

        var prefix = proximity?.Kind switch
        {
            BattingProximityKind.OnDeck => "On Deck",
            BattingProximityKind.DueUp => "In Hole",
            _ => null,
        };

        return (prefix, statLine) switch
        {
            (not null, not null) => $"{prefix} · {statLine}",
            (not null, null) => prefix,
            (null, not null) => statLine,
            _ => null,
        };
    }

    /// <summary>
    /// Port of <c>LivePlayerRow.delayLabel(detailedState:)</c>. Mid-game pauses only —
    /// pre-game delays carry abstractGameState "Preview" and never reach this path.
    /// </summary>
    public static string? DelayLabel(string? detailedState)
    {
        if (detailedState is not { } detailed) return null;

        const string delayedPrefix = "Delayed: ";
        if (detailed.StartsWith(delayedPrefix, StringComparison.Ordinal))
        {
            return $"{detailed[delayedPrefix.Length..]} Delay";
        }

        const string suspendedPrefix = "Suspended: ";
        if (detailed.StartsWith(suspendedPrefix, StringComparison.Ordinal))
        {
            return $"Suspended: {detailed[suspendedPrefix.Length..]}";
        }

        if (detailed == "Delayed") return "Delayed";
        if (detailed == "Suspended") return "Suspended";

        return null;
    }

    /// <summary>Port of <c>delayIcon(detailedState:)</c>, classified rather than iconified.</summary>
    public static DelayIndicator DelayFor(string? detailedState)
    {
        if (detailedState is not { } detailed) return DelayIndicator.None;
        if (detailed.Contains("Rain", StringComparison.Ordinal)) return DelayIndicator.Rain;
        if (detailed.StartsWith("Delayed", StringComparison.Ordinal)
            || detailed.StartsWith("Suspended", StringComparison.Ordinal))
        {
            return DelayIndicator.Delayed;
        }

        return detailed == "Postponed" ? DelayIndicator.Postponed : DelayIndicator.None;
    }

    /// <summary>Port of <c>UpcomingPlayerRow.lineupInfo</c>.</summary>
    public static LineupInfo LineupInfoFor(
        Player player, Game? game, GameLineup? lineup, LiveFeedData? feed)
    {
        if (game is null
            || game.SideFor(player) is not { } side
            || lineup is null
            || !lineup.IsSubmitted(side))
        {
            return LineupInfo.Unknown;
        }

        if (lineup.Excludes(player, side)) return LineupInfo.NotInLineup;
        if (!lineup.Ids(side).Contains(player.Id)) return LineupInfo.Unknown;

        // Check the live feed first, then fall back to schedule lineup data.
        if (feed is not null)
        {
            var homeIndex = feed.HomeBattingOrder.IndexOf(player.Id);
            if (homeIndex >= 0) return LineupInfo.BattingOrder(homeIndex + 1);

            var awayIndex = feed.AwayBattingOrder.IndexOf(player.Id);
            if (awayIndex >= 0) return LineupInfo.BattingOrder(awayIndex + 1);
        }

        var scheduledHome = IndexOf(game.HomeLineup, player.Id);
        if (scheduledHome >= 0) return LineupInfo.BattingOrder(scheduledHome + 1);

        var scheduledAway = IndexOf(game.AwayLineup, player.Id);
        if (scheduledAway >= 0) return LineupInfo.BattingOrder(scheduledAway + 1);

        return LineupInfo.InLineup;
    }

    private static int IndexOf(IReadOnlyList<int> values, int value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value) return index;
        }

        return -1;
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~DisplayRules`
Expected: PASS (both display-rules files).

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/DisplayRules.cs windows/tests/OnDeck.Core.Tests/App/DisplayRulesTests.cs
git commit -m "phase 5: in-game sort key, stat line and lineup badge rules"
```

---

## Task 4: Orchestrator construction and sync flows

**Files:**
- Create: `src/OnDeck.Core/AppOrchestrator.cs`
- Create: `tests/OnDeck.Core.Tests/App/OrchestratorHarness.cs`
- Create: `tests/OnDeck.Core.Tests/App/AppOrchestratorSyncTests.cs`

**Spec:** `App/AppState.swift:66-135` (`start`, URL/team helpers), `:137-205` (`fetchTeams`, `resyncRoster`, `fetchScheduleAndStartMonitoring`), `:303-327` (`initializePlayerStates`).

**Interfaces:**
- Consumes: `INotificationSink` (Task 1), `RoutingHttpMessageHandler` / `SingleThreadedContext` / `RecordingNotificationSink` (Task 1).
- Produces: `public sealed class AppOrchestrator(RosterManager, ScheduleManager, GameMonitor, StateManager, FantraxApi, ISettingsStore, INotificationSink, TimeProvider?)` with `StartAsync`, `FetchTeamsAsync`, `ResyncRosterAsync`, `SettingsChanged` (stub until Task 5), the contract's sync/team-picker properties, `event Action? StateChanged`, and the additive `ParsedLeagueId` / `UrlHasTeamId` / `EffectiveTeamId` the Phase 7/8 shell needs.
- Produces: `OrchestratorHarness` with `AddPlayer`, `AddGame`, `GameOf`, `Build`, `Run(Func<AppOrchestrator, Task>)`, `RunStarted(Func<AppOrchestrator, Task>)`, `GoLive`, `SeedFeed`, `PlayerNamed`, `Stop`, and the `Http` / `Time` / `Settings` / `Sink` / `Roster` / `Schedule` / `Monitor` / `States` / `Lifetime` members. Every later task builds its tests on it.

**Deliberate omissions in this task** (added by the task named):
- `FetchScheduleAndStartMonitoringAsync` does not yet reconcile lineup notifications — Task 7.
- `StartAsync` does not yet schedule the daily refresh, and `FetchScheduleAndStartMonitoringAsync` does not yet schedule the pre-game refresh — Task 9.
- `InitializePlayerStates` does not yet rebuild the lists — Task 5.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/App/OrchestratorHarness.cs`:

```csharp
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.App;

/// <summary>
/// Composes the real managers over a routed HTTP double and runs the body on a pumping
/// single-threaded context — the same serialization Core gets from the WPF Dispatcher.
/// Roster entries drive the Fantrax response, the per-name MLB search response and the
/// cached-roster blob at once, so a test declares its players once.
/// </summary>
internal sealed class OrchestratorHarness
{
    public static readonly DateTimeOffset Now = new(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);

    public const string LeagueUrl = "https://www.fantrax.com/fantasy/league/lg1/team/roster;teamId=t1";

    public const string LeagueUrlWithoutTeam = "https://www.fantrax.com/fantasy/league/lg1/standings";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly List<RosterEntry> _roster = [];
    private readonly List<Game> _games = [];

    public OrchestratorHarness(DateTimeOffset? now = null)
    {
        Time = new FakeTimeProvider(now ?? Now);
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Settings.RosterUrl = LeagueUrl;
    }

    public RoutingHttpMessageHandler Http { get; } = new();

    public FakeTimeProvider Time { get; }

    public InMemorySettingsStore Settings { get; } = new();

    public RecordingNotificationSink Sink { get; } = new();

    public CancellationTokenSource Lifetime { get; } = new();

    public List<FantraxTeam> Teams { get; } = [new("t1", "My Team"), new("t2", "Their Team")];

    /// <summary>Pre-populates <c>RosterCacheJson</c> so the ctor's cache load has players.</summary>
    public bool SeedCachedRoster { get; set; } = true;

    public RosterManager Roster { get; private set; } = null!;

    public ScheduleManager Schedule { get; private set; } = null!;

    public GameMonitor Monitor { get; private set; } = null!;

    public StateManager States { get; private set; } = null!;

    public AppOrchestrator App { get; private set; } = null!;

    public OrchestratorHarness AddPlayer(
        int mlbId, string name, string fantraxTeam = "LAD", string positions = "OF", int statusId = 1)
    {
        _roster.Add(new RosterEntry(mlbId, name, fantraxTeam, positions, statusId));
        return this;
    }

    public OrchestratorHarness AddGame(Game game)
    {
        _games.Add(game);
        return this;
    }

    public static Game GameOf(
        int id,
        DateTimeOffset start,
        string home = "Los Angeles Dodgers",
        string away = "San Francisco Giants",
        int homeTeamId = 119,
        int awayTeamId = 137,
        int? homeProbablePitcher = null,
        int? awayProbablePitcher = null,
        IReadOnlyList<int>? homeLineup = null,
        IReadOnlyList<int>? awayLineup = null,
        string? exclusiveCallSign = null) =>
        new(id, home, away, homeTeamId, awayTeamId, start, homeProbablePitcher, awayProbablePitcher,
            exclusiveCallSign is null ? [] : [new Game.Broadcast(exclusiveCallSign, true)],
            homeLineup ?? [], awayLineup ?? []);

    /// <summary>Builds the orchestrator. Must run on the context the test pumps.</summary>
    public AppOrchestrator Build()
    {
        Http.MapStatus("/feed/live", HttpStatusCode.ServiceUnavailable);
        Http.MapJson("fantrax.com/fxpa/req", (_, body) =>
            body.Contains("getStandings", StringComparison.Ordinal) ? StandingsJson() : RosterJson());
        Http.MapJson("/v1/people/search", (request, _) => SearchJson(request));
        Http.MapJson("/v1/schedule", (_, _) => ScheduleJson());

        if (SeedCachedRoster) Settings.RosterCacheJson = RosterCacheJson();

        var client = Http.CreateClient();
        var mlb = new MlbStatsApi(client, Time);
        var fantrax = new FantraxApi(client, Time);

        Roster = new RosterManager(fantrax, mlb, Settings, null, Time);
        Schedule = new ScheduleManager(mlb, Time);
        Monitor = new GameMonitor(mlb, Time);
        States = new StateManager();
        App = new AppOrchestrator(Roster, Schedule, Monitor, States, fantrax, Settings, Sink, Time);

        return App;
    }

    /// <summary>Builds and runs <paramref name="body"/> on a pumped single-threaded context.</summary>
    public void Run(Func<AppOrchestrator, Task> body) =>
        SingleThreadedContext.Run(async () =>
        {
            var app = Build();
            try
            {
                await body(app);
            }
            finally
            {
                Stop();
            }
        });

    /// <summary>As <see cref="Run"/>, with <c>StartAsync</c> already awaited and settled.</summary>
    public void RunStarted(Func<AppOrchestrator, Task> body) =>
        Run(async app =>
        {
            await app.StartAsync(Lifetime.Token);
            await SingleThreadedContext.Settle();
            await body(app);
        });

    /// <summary>
    /// Drives a game to Live/In Progress through the real feed-processing path, which is what
    /// flips <c>GameMonitor.IsLive</c> and fires <c>OnGameStart</c>.
    /// </summary>
    public void GoLive(int gamePk, string detailedState = "In Progress")
    {
        var game = _games.First(candidate => candidate.Id == gamePk);
        Monitor.ProcessFeed(
            new LiveFeedData { GameState = "Live", DetailedState = detailedState }, gamePk, game);
    }

    /// <summary>Puts a feed in <c>LatestFeeds</c> without polling.</summary>
    public LiveFeedData SeedFeed(int gamePk, Action<LiveFeedData>? configure = null)
    {
        var feed = new LiveFeedData
        {
            GameState = "Live",
            DetailedState = "In Progress",
            HomeTeam = "Dodgers",
            AwayTeam = "Giants",
            HomeTeamId = 119,
            AwayTeamId = 137,
        };

        configure?.Invoke(feed);
        Monitor.LatestFeeds[gamePk] = feed;
        return feed;
    }

    public Player PlayerNamed(string name) =>
        Roster.Players.First(player => player.Name == name);

    public void Stop()
    {
        Lifetime.Cancel();
        Monitor.StopMonitoring();
    }

    // MARK: - Canned responses

    private string RosterJson() => JsonSerializer.Serialize(
        new
        {
            responses = new[]
            {
                new
                {
                    data = new
                    {
                        tables = new[]
                        {
                            new
                            {
                                rows = _roster.Select(entry => new
                                {
                                    statusId = entry.StatusId,
                                    scorer = new
                                    {
                                        name = entry.Name,
                                        scorerId = $"*{entry.MlbId}*",
                                        posShortNames = entry.Positions,
                                        teamShortName = entry.FantraxTeam,
                                    },
                                }),
                            },
                        },
                    },
                },
            },
        },
        Json);

    private string StandingsJson() => JsonSerializer.Serialize(
        new
        {
            responses = new[]
            {
                new { data = new { rows = Teams.Select(team => new { teamId = team.Id, content = team.Name }) } },
            },
        },
        Json);

    private string SearchJson(HttpRequestMessage request)
    {
        var name = HttpUtility.ParseQueryString(request.RequestUri!.Query)["names"] ?? "";
        var match = _roster.FirstOrDefault(entry => NameCleaner.Clean(entry.Name) == name);
        if (match is null) return """{"people": []}""";

        return JsonSerializer.Serialize(
            new
            {
                people = new[]
                {
                    new
                    {
                        id = match.MlbId,
                        fullName = NameCleaner.Clean(match.Name),
                        currentTeam = new { id = 0, name = MlbTeamName(match) },
                    },
                },
            },
            Json);
    }

    private string ScheduleJson() => JsonSerializer.Serialize(
        new
        {
            dates = new[]
            {
                new
                {
                    games = _games.Select(game => new
                    {
                        gamePk = game.Id,
                        gameDate = game.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        status = new { abstractGameState = "Preview", detailedState = "Scheduled" },
                        teams = new
                        {
                            away = new
                            {
                                team = new { id = game.AwayTeamId, name = game.AwayTeam },
                                probablePitcher = game.AwayProbablePitcherId is { } away
                                    ? (object?)new { id = away }
                                    : null,
                            },
                            home = new
                            {
                                team = new { id = game.HomeTeamId, name = game.HomeTeam },
                                probablePitcher = game.HomeProbablePitcherId is { } home
                                    ? (object?)new { id = home }
                                    : null,
                            },
                        },
                        broadcasts = game.Broadcasts.Select(broadcast => new
                        {
                            callSign = broadcast.CallSign,
                            availability = new
                            {
                                availabilityCode = broadcast.IsExclusive ? "exclusive" : "free",
                            },
                        }),
                        lineups = new
                        {
                            homePlayers = game.HomeLineup.Select(id => new { id }),
                            awayPlayers = game.AwayLineup.Select(id => new { id }),
                        },
                    }),
                },
            },
        },
        Json);

    private string RosterCacheJson() => JsonSerializer.Serialize(
        _roster.Select(entry => new
        {
            id = entry.MlbId,
            name = NameCleaner.Clean(entry.Name),
            team = MlbTeamName(entry),
            positions = PositionsOf(entry).Select(position => position.ToString()),
            fantraxPositions = RawPositionsOf(entry),
            rosterStatus = ((RosterStatus)entry.StatusId).ToString(),
        }),
        Json);

    private static string MlbTeamName(RosterEntry entry) =>
        TeamMapping.MlbTeamName(entry.FantraxTeam) ?? entry.FantraxTeam;

    private static string[] RawPositionsOf(RosterEntry entry) =>
        entry.Positions.Length == 0
            ? []
            : [.. entry.Positions.Split(',').Select(position => position.Trim().ToUpperInvariant())];

    /// <summary>Mirrors <c>RosterManager.ParsePositions</c>: SP/RP/P are pitchers.</summary>
    private static HashSet<PlayerPosition> PositionsOf(RosterEntry entry)
    {
        string[] pitcherCodes = ["SP", "RP", "P"];
        var positions = RawPositionsOf(entry)
            .Select(position => pitcherCodes.Contains(position) ? PlayerPosition.Pitcher : PlayerPosition.Hitter)
            .ToHashSet();

        if (positions.Count == 0) positions.Add(PlayerPosition.Hitter);

        return positions;
    }

    private sealed record RosterEntry(int MlbId, string Name, string FantraxTeam, string Positions, int StatusId);
}
```

Create `tests/OnDeck.Core.Tests/App/AppOrchestratorSyncTests.cs`:

```csharp
using System.Net;
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorSyncTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private static OrchestratorHarness Harness() =>
        new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

    [Fact]
    public void StartAsync_SyncsTheRosterThenFetchesTheSchedule()
    {
        var harness = Harness();

        harness.RunStarted(app =>
        {
            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));
            Assert.Equal(1, harness.Http.CountRequests("/v1/schedule"));
            Assert.NotNull(harness.Roster.LastSyncDate);
            Assert.Equal(1, app.LoadedPlayerCount);
            Assert.Null(app.SyncError);
            Assert.False(app.IsSyncing);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_StartsMonitoringAndSeedsLineupsAfterwards()
    {
        // Seeding must happen AFTER StartMonitoring - it calls StopMonitoring internally,
        // which clears LineupPlayerIds.
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.True(harness.Monitor.IsMonitoring);
            Assert.Equal([101, 102, 103], harness.Monitor.LineupPlayerIds[1].Home.Order());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_SeedsProbablePitchersEvenWithoutABattingCard()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(901, "Blake Snell", positions: "SP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeProbablePitcher: 901));

        harness.RunStarted(_ =>
        {
            Assert.Equal([901], harness.Monitor.LineupPlayerIds[1].HomePitchers.Order());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_PurgesEveryNotificationBeforeRefreshing()
    {
        var harness = Harness();

        harness.RunStarted(_ =>
        {
            Assert.Contains("purgeAll", harness.Sink.Calls);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_IsIdempotent()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            await app.StartAsync(harness.Lifetime.Token);
            await SingleThreadedContext.Settle();

            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void StartAsync_DoesNothingWithoutARosterUrl()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = "";

            await app.StartAsync(harness.Lifetime.Token);
            await SingleThreadedContext.Settle();

            Assert.Empty(harness.Http.Requests);
        });
    }

    [Fact]
    public void StartAsync_FetchesTeamsWhenTheUrlHasNoTeamAndNoneIsSelected()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = null;

            await app.StartAsync(harness.Lifetime.Token);
            await SingleThreadedContext.Settle();

            Assert.Equal(["t1", "t2"], app.AvailableTeams.Select(team => team.Id));
            Assert.False(app.IsLoadingTeams);
            Assert.Equal(0, harness.Http.CountRequests("/v1/schedule"));
        });
    }

    [Fact]
    public void StartAsync_UsesTheSelectedTeamWhenTheUrlHasNone()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = "t2";

            await app.StartAsync(harness.Lifetime.Token);
            await SingleThreadedContext.Settle();

            Assert.Contains("\"teamId\":\"t2\"", harness.Http.RequestBodies[0].Replace(" ", ""));
            Assert.Equal(1, harness.Http.CountRequests("/v1/schedule"));
        });
    }

    [Fact]
    public void StartAsync_MarksPlayersWithoutAGameAsDayOff()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(202, "Rafael Devers", fantraxTeam: "BOS")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(_ =>
        {
            Assert.IsType<PlayerState.Upcoming>(harness.States.PlayerStates[101]);
            var devers = Assert.IsType<PlayerState.Inactive>(harness.States.PlayerStates[202]);
            Assert.IsType<PlayerState.InactiveReason.DayOff>(devers.Reason);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_MarksStartingPitchersWhoArentTodaysProbableAsDayOff()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(901, "Blake Snell", positions: "SP")
            .AddPlayer(902, "Tyler Glasnow", positions: "SP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeProbablePitcher: 901));

        harness.RunStarted(_ =>
        {
            Assert.IsType<PlayerState.Upcoming>(harness.States.PlayerStates[901]);
            var glasnow = Assert.IsType<PlayerState.Inactive>(harness.States.PlayerStates[902]);
            Assert.IsType<PlayerState.InactiveReason.DayOff>(glasnow.Reason);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_LeavesRelieversUpcoming()
    {
        // isStartingPitcherOnly is SP-and-not-RP; a swingman stays in the pool.
        var harness = new OrchestratorHarness()
            .AddPlayer(903, "Michael Kopech", positions: "SP,RP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(_ =>
        {
            Assert.IsType<PlayerState.Upcoming>(harness.States.PlayerStates[903]);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void FetchTeamsAsync_ReportsAnInvalidUrl()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = "not a url";

            await app.FetchTeamsAsync();

            Assert.Equal("Invalid Fantrax URL", app.TeamsError);
            Assert.Empty(harness.Http.Requests);
        });
    }

    [Fact]
    public void FetchTeamsAsync_ClearsASelectionThatNoLongerExists()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = "gone";

            await app.FetchTeamsAsync();

            Assert.Equal("", harness.Settings.SelectedTeamId);
            Assert.Null(app.TeamsError);
        });
    }

    [Fact]
    public void FetchTeamsAsync_KeepsASelectionThatStillExists()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = "t2";

            await app.FetchTeamsAsync();

            Assert.Equal("t2", harness.Settings.SelectedTeamId);
        });
    }

    [Fact]
    public void FetchTeamsAsync_RecordsErrorsAndStopsLoading()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Http.MapStatus("fantrax.com/fxpa/req", HttpStatusCode.InternalServerError);

            await app.FetchTeamsAsync();

            Assert.StartsWith("Couldn't load teams:", app.TeamsError);
            Assert.False(app.IsLoadingTeams);
            Assert.Empty(app.AvailableTeams);
        });
    }

    [Fact]
    public void ResyncRosterAsync_ReturnsFalseWithoutALeagueOrTeam()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = null;

            Assert.False(await app.ResyncRosterAsync());
            Assert.Empty(harness.Http.Requests);
        });
    }

    [Fact]
    public void ResyncRosterAsync_ReturnsTrueAndRefetchesTheSchedule()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            Assert.True(await app.ResyncRosterAsync());
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
            Assert.Equal(2, harness.Http.CountRequests("/v1/schedule"));
            Assert.Equal(2, harness.Sink.Calls.Count(call => call == "purgeAll"));
        });
    }

    [Fact]
    public void ResyncRosterAsync_ReturnsFalseWhenTheRosterSyncFails()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.Http.MapStatus("fantrax.com/fxpa/req", HttpStatusCode.InternalServerError);

            Assert.False(await app.ResyncRosterAsync());
            Assert.StartsWith("Roster sync failed:", app.SyncError);
        });
    }

    [Fact]
    public void EffectiveTeamId_PrefersTheUrlOverThePicker()
    {
        var harness = Harness();

        harness.Run(app =>
        {
            harness.Settings.SelectedTeamId = "t2";

            Assert.Equal("lg1", app.ParsedLeagueId);
            Assert.True(app.UrlHasTeamId);
            Assert.Equal("t1", app.EffectiveTeamId);

            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;

            Assert.False(app.UrlHasTeamId);
            Assert.Equal("t2", app.EffectiveTeamId);
            return Task.CompletedTask;
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestratorSyncTests`
Expected: build failure — `AppOrchestrator` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/OnDeck.Core/AppOrchestrator.cs`:

```csharp
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core;

/// <summary>
/// Port of the portable half of <c>App/AppState.swift</c>: owns the managers, publishes the
/// four player lists as immutable snapshots, and drives roster/schedule refreshes.
/// Everything here runs on one logical thread — the WPF <c>Dispatcher</c> in the app, a
/// pumped single-threaded context in tests — which is what makes the coalesced list rebuild
/// and the post-await race guards correct. No <c>ConfigureAwait(false)</c> anywhere.
/// </summary>
public sealed class AppOrchestrator
{
    private readonly RosterManager _roster;
    private readonly ScheduleManager _schedule;
    private readonly GameMonitor _monitor;
    private readonly StateManager _states;
    private readonly FantraxApi _fantrax;
    private readonly ISettingsStore _settings;
    private readonly INotificationSink _notifications;
    private readonly TimeProvider _time;

    private IReadOnlyList<Game> _games = [];
    private bool _hasStarted;
    private bool _isSyncing;
    private bool _hideBenchPlayers;
    private CancellationTokenSource? _lifetime;

    public AppOrchestrator(
        RosterManager roster,
        ScheduleManager schedule,
        GameMonitor monitor,
        StateManager states,
        FantraxApi fantrax,
        ISettingsStore settings,
        INotificationSink notifications,
        TimeProvider? timeProvider = null)
    {
        _roster = roster;
        _schedule = schedule;
        _monitor = monitor;
        _states = states;
        _fantrax = fantrax;
        _settings = settings;
        _notifications = notifications;
        _time = timeProvider ?? TimeProvider.System;
        _hideBenchPlayers = settings.HideBenchPlayers;

        _monitor.Configure(_states);

        // Swift does this in RosterManager.init; the C# port made it explicit.
        _roster.LoadCachedRoster();
    }

    // MARK: - Published state

    /// <summary>Fired on the Core context whenever any published property changes.</summary>
    public event Action? StateChanged;

    public bool IsSyncing => _isSyncing || _roster.IsSyncing;

    public DateTimeOffset? LastSyncDate => _roster.LastSyncDate;

    public string? SyncError => _roster.Error ?? _schedule.Error;

    public int LoadedPlayerCount => _roster.Players.Count;

    public IReadOnlyList<FantraxTeam> AvailableTeams { get; private set; } = [];

    public bool IsLoadingTeams { get; private set; }

    public string? TeamsError { get; private set; }

    /// <summary>The parsed leagueID from the current URL, if valid.</summary>
    public string? ParsedLeagueId => FantraxUrlParser.Parse(RosterUrl)?.LeagueId;

    /// <summary>Whether the URL already contains a teamId (no picker needed).</summary>
    public bool UrlHasTeamId => FantraxUrlParser.Parse(RosterUrl)?.TeamId is not null;

    /// <summary>The effective teamID — from the URL if available, otherwise from the picker.</summary>
    public string? EffectiveTeamId
    {
        get
        {
            if (FantraxUrlParser.Parse(RosterUrl)?.TeamId is { } teamId) return teamId;
            return string.IsNullOrEmpty(_settings.SelectedTeamId) ? null : _settings.SelectedTeamId;
        }
    }

    private string RosterUrl => _settings.RosterUrl ?? "";

    // MARK: - Lifecycle

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_hasStarted) return;
        _hasStarted = true;

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (RosterUrl.Length == 0) return;
        if (FantraxUrlParser.Parse(RosterUrl) is not { } parsed) return;

        if (parsed.TeamId is { } teamId)
        {
            await _roster.SyncRosterAsync(parsed.LeagueId, teamId, Token);
        }
        else if (_settings.SelectedTeamId is { Length: > 0 } selected)
        {
            await _roster.SyncRosterAsync(parsed.LeagueId, selected, Token);
        }
        else
        {
            // No team selected yet - fetch teams so the user can pick.
            await FetchTeamsAsync();
            return;
        }

        await FetchScheduleAndStartMonitoringAsync();
    }

    // MARK: - Team fetching

    public async Task FetchTeamsAsync()
    {
        if (ParsedLeagueId is not { } leagueId)
        {
            TeamsError = "Invalid Fantrax URL";
            StateChanged?.Invoke();
            return;
        }

        IsLoadingTeams = true;
        TeamsError = null;
        StateChanged?.Invoke();

        try
        {
            AvailableTeams = await _fantrax.FetchTeamsAsync(leagueId, Token);

            // If a team was previously selected and still exists, keep it.
            if (_settings.SelectedTeamId is { Length: > 0 } selected
                && !AvailableTeams.Any(team => team.Id == selected))
            {
                _settings.SelectedTeamId = "";
            }
        }
        catch (Exception ex)
        {
            TeamsError = $"Couldn't load teams: {ex.Message}";
        }

        IsLoadingTeams = false;
        StateChanged?.Invoke();
    }

    /// <summary>Manually triggers a roster re-sync. False on failure (drives the Refresh button).</summary>
    public async Task<bool> ResyncRosterAsync()
    {
        if (ParsedLeagueId is not { } leagueId || EffectiveTeamId is not { } teamId) return false;

        _isSyncing = true;
        StateChanged?.Invoke();

        await _roster.SyncRosterAsync(leagueId, teamId, Token);
        var success = _roster.Error is null;
        await FetchScheduleAndStartMonitoringAsync();

        _isSyncing = false;
        StateChanged?.Invoke();
        return success;
    }

    private async Task FetchScheduleAndStartMonitoringAsync()
    {
        await _notifications.PurgeAllAsync();

        var teamNames = _roster.Players.Select(player => player.Team).ToHashSet(StringComparer.Ordinal);
        await _schedule.FetchScheduleAsync(teamNames, Token);
        _games = _schedule.TodaysGames;

        _states.Reset();
        InitializePlayerStates();

        _monitor.StopMonitoring();
        if (_games.Count == 0) return;

        _monitor.StartMonitoring(_games, _roster.Players);

        // Seed lineup data from the schedule (available before live feed polling starts).
        // StartMonitoring calls StopMonitoring internally, so this must come after it.
        foreach (var game in _games)
        {
            var lineup = new GameLineup
            {
                Home = [.. game.HomeLineup],
                Away = [.. game.AwayLineup],
                HomePitchers = game.HomeProbablePitcherId is { } homePitcher ? [homePitcher] : [],
                AwayPitchers = game.AwayProbablePitcherId is { } awayPitcher ? [awayPitcher] : [],
            };

            if (lineup.IsSubmitted(Game.Side.Home)
                || lineup.IsSubmitted(Game.Side.Away)
                || lineup.HomePitchers.Count > 0
                || lineup.AwayPitchers.Count > 0)
            {
                _monitor.LineupPlayerIds[game.Id] = lineup;
            }
        }
    }

    private void InitializePlayerStates()
    {
        foreach (var game in _games)
        {
            var playerIds = _roster.Players
                .Where(player => IsPlayerInGame(player, game))
                .Select(player => player.Id)
                .ToList();

            _states.SetUpcoming(playerIds, game.StartTime);
        }

        // Mark SP-only players as day off if they're not today's probable pitcher.
        var probablePitcherIds = _games
            .SelectMany(game => new[] { game.HomeProbablePitcherId, game.AwayProbablePitcherId })
            .OfType<int>()
            .ToHashSet();

        foreach (var player in _roster.Players)
        {
            if (!player.IsStartingPitcherOnly || probablePitcherIds.Contains(player.Id)) continue;
            _states.Update(player.Id, new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff()));
        }

        var allGamePlayerIds = _states.PlayerStates.Keys.ToHashSet();
        foreach (var player in _roster.Players)
        {
            if (allGamePlayerIds.Contains(player.Id)) continue;
            _states.Update(player.Id, new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff()));
        }
    }

    /// <summary>
    /// Bidirectional substring match so a Fantrax abbreviation ("Dodgers") matches an MLB
    /// full name ("Los Angeles Dodgers") and vice versa.
    /// </summary>
    private static bool IsPlayerInGame(Player player, Game game) =>
        game.HomeTeam.Contains(player.Team, StringComparison.Ordinal)
        || game.AwayTeam.Contains(player.Team, StringComparison.Ordinal)
        || player.Team.Contains(game.HomeTeam, StringComparison.Ordinal)
        || player.Team.Contains(game.AwayTeam, StringComparison.Ordinal);

    private Game? GameFor(Player player) => _games.FirstOrDefault(game => IsPlayerInGame(player, game));

    private CancellationToken Token => _lifetime?.Token ?? CancellationToken.None;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestratorSyncTests`
Expected: PASS. `GameFor` is unused until Task 5 — the CS0169-style warning is expected and goes away there.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/AppOrchestrator.cs windows/tests/OnDeck.Core.Tests/App
git commit -m "phase 5: AppOrchestrator construction and roster/schedule sync flows"
```

---

## Task 5: Player lists, filters and sorting

**Files:**
- Modify: `src/OnDeck.Core/AppOrchestrator.cs`
- Create: `tests/OnDeck.Core.Tests/App/AppOrchestratorListTests.cs`

**Spec:** `App/AppState.swift:329-389` (`updatePlayerLists`, `gamePk(for:)`, `hasStatLine`), `:57-64` (`menuBarTitle`), `:41-46` (`hideBenchPlayers` didSet), `Views/MenuBarView.swift:157-185` (In Game ordering).

**Interfaces:**
- Consumes: `DisplayRules` (Tasks 2-3), `AppOrchestrator` (Task 4).
- Produces on `AppOrchestrator`: `IReadOnlyList<PlayerDisplay> ActivePlayers / InGamePlayers / UpcomingPlayers / DonePlayers`, `bool HasActivePlayers`, `string MenuBarTitleText`, `void SettingsChanged()`, and the private `UpdatePlayerLists()` the later tasks post to.

**Additive deviation to record:** the contract lists `UpcomingPlayers`, `InGamePlayers` and `DonePlayers` but no `ActivePlayers`, while `MenuBarView.swift` renders an "Active Now" section from `appState.activePlayers`. `ActivePlayers` is added so Phase 7 can render it; `HasActivePlayers` is derived from it.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/App/AppOrchestratorListTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorListTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private static PlayerState.GameContext Context(
        int gamePk = 1, PlayerState.ActiveRole role = PlayerState.ActiveRole.Batting) =>
        new(gamePk, role, "Bot 3", "Dodgers", "Giants", 119, 137, 2, 1, 1, 2, 1, false, false, false);

    [Fact]
    public void UpcomingPlayers_SortsByStartTimeThenName()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(102, "Freddie Freeman")
            .AddPlayer(201, "Rafael Devers", fantraxTeam: "BOS")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch.AddHours(1)))
            .AddGame(OrchestratorHarness.GameOf(
                2, FirstPitch, home: "Boston Red Sox", away: "New York Yankees",
                homeTeamId: 111, awayTeamId: 147));

        harness.RunStarted(app =>
        {
            Assert.Equal(
                ["Rafael Devers", "Freddie Freeman", "Mookie Betts"],
                app.UpcomingPlayers.Select(display => display.Name));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void UpcomingPlayers_CarryTheirLineupBadgeAndStartTime()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [103, 101, 102]));

        harness.RunStarted(app =>
        {
            var row = Assert.Single(app.UpcomingPlayers);
            Assert.Equal(LineupInfoKind.BattingOrder, row.Lineup.Kind);
            Assert.Equal(2, row.Lineup.Spot);
            Assert.Equal(FirstPitch, row.StartTime);
            Assert.Equal(1, row.GamePk);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Lists_SkipUnavailablePlayers()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(102, "Injured Guy", statusId: 3)
            .AddPlayer(103, "Minors Guy", statusId: 9)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(app =>
        {
            Assert.Equal(["Mookie Betts"], app.UpcomingPlayers.Select(display => display.Name));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SettingsChanged_RefiltersBenchPlayersWithoutAnyNetworkCall()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(102, "Bench Guy", statusId: 2)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(app =>
        {
            Assert.Equal(2, app.UpcomingPlayers.Count);
            var requestsBefore = harness.Http.Requests.Count;

            harness.Settings.HideBenchPlayers = true;
            app.SettingsChanged();

            Assert.Equal(["Mookie Betts"], app.UpcomingPlayers.Select(display => display.Name));
            Assert.Equal(requestsBefore, harness.Http.Requests.Count);

            harness.Settings.HideBenchPlayers = false;
            app.SettingsChanged();

            Assert.Equal(2, app.UpcomingPlayers.Count);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void InGamePlayers_HoldUpcomingPlayersWhoseGameIsLive()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.GoLive(1);
            await SingleThreadedContext.Settle();

            Assert.Empty(app.UpcomingPlayers);
            var row = Assert.Single(app.InGamePlayers);
            Assert.Equal("Mookie Betts", row.Name);
            Assert.False(row.IsActive);
        });
    }

    [Fact]
    public void InGamePlayers_SortByProximityThenDelayThenExclusion()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "At Bat")
            .AddPlayer(102, "On Deck")
            .AddPlayer(105, "Deep In Order")
            .AddPlayer(901, "Bullpen Arm", positions: "RP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.GoLive(1);
            harness.SeedFeed(1, feed =>
            {
                feed.InningHalf = "Bottom";
                feed.InningState = "Bottom";
                feed.CurrentBatterId = 101;
                feed.HomeBattingOrder = [101, 102, 103, 104, 105];
            });

            app.SettingsChanged();      // local rebuild against the seeded feed
            await SingleThreadedContext.Settle();

            Assert.Equal(
                ["At Bat", "On Deck", "Deep In Order", "Bullpen Arm"],
                app.InGamePlayers.Select(display => display.Name));
        });
    }

    [Fact]
    public void InGamePlayers_PushExcludedHittersToTheBottom()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "In The Card")
            .AddPlayer(199, "Benched")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(async app =>
        {
            harness.GoLive(1);
            app.SettingsChanged();
            await SingleThreadedContext.Settle();

            Assert.Equal(["In The Card", "Benched"], app.InGamePlayers.Select(display => display.Name));
            Assert.False(app.InGamePlayers[1].IsInLineup);
            Assert.Equal("Not in Lineup", app.InGamePlayers[1].StatLine);
        });
    }

    [Fact]
    public void ActivePlayers_HoldPlayersInTheActiveStateAndDriveTheTrayFlag()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            Assert.False(app.HasActivePlayers);

            harness.States.Update(101, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();

            var row = Assert.Single(app.ActivePlayers);
            Assert.True(row.IsActive);
            Assert.True(app.HasActivePlayers);
            Assert.Empty(app.InGamePlayers);
        });
    }

    [Fact]
    public void MenuBarTitleText_JoinsUpToThreeNamesThenCounts()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "A")
            .AddPlayer(102, "B")
            .AddPlayer(103, "C")
            .AddPlayer(104, "D")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            Assert.Equal("", app.MenuBarTitleText);

            harness.States.Update(101, new PlayerState.Active(Context()));
            harness.States.Update(102, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();
            Assert.Equal("A | B", app.MenuBarTitleText);

            harness.States.Update(103, new PlayerState.Active(Context()));
            harness.States.Update(104, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();
            Assert.Equal("A | B | C +1", app.MenuBarTitleText);
        });
    }

    [Fact]
    public void DonePlayers_NeedAStatLineMatchingTheirRole()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Hit A Double")
            .AddPlayer(102, "Never Played")
            .AddPlayer(901, "Relief Pitcher", positions: "RP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.SeedFeed(1, feed =>
            {
                feed.PlayerStats[101] = new PlayerGameStats
                {
                    Batting = new PlayerBattingStats { AtBats = 4, Hits = 2, Doubles = 1 },
                };

                // A pitcher-only player's batting line must not qualify them.
                feed.PlayerStats[901] = new PlayerGameStats
                {
                    Batting = new PlayerBattingStats { AtBats = 1, Hits = 1 },
                };
            });

            harness.States.SetGameOver([101, 102, 901], 1);
            await SingleThreadedContext.Settle();

            var row = Assert.Single(app.DonePlayers);
            Assert.Equal("Hit A Double", row.Name);
            Assert.Equal("2-4 · 2B", row.StatLine);
        });
    }

    [Fact]
    public void DonePlayers_IncludeSubstitutedPitchersAndSortHittersFirst()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Hitter")
            .AddPlayer(901, "Starter", positions: "SP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeProbablePitcher: 901));

        harness.RunStarted(async app =>
        {
            harness.SeedFeed(1, feed =>
            {
                feed.PlayerStats[101] = new PlayerGameStats
                {
                    Batting = new PlayerBattingStats { AtBats = 3, Hits = 1 },
                };
                feed.PlayerStats[901] = new PlayerGameStats
                {
                    Pitching = new PlayerPitchingStats { InningsPitched = "6.0", StrikeOuts = 8, EarnedRuns = 2 },
                };
            });

            harness.States.Update(
                901, new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(1)));
            harness.States.SetGameOver([101], 1);
            await SingleThreadedContext.Settle();

            Assert.Equal(["Hitter", "Starter"], app.DonePlayers.Select(display => display.Name));
            Assert.Equal("6.0 IP, 8K, 2ER", app.DonePlayers[1].StatLine);
        });
    }

    [Fact]
    public void DonePlayers_ExcludeDayOffPlayers()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(201, "Off Today", fantraxTeam: "BOS")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(app =>
        {
            Assert.Empty(app.DonePlayers);
            Assert.Empty(app.UpcomingPlayers);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Rows_CarryTheStreamLinkForTheirGame()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, exclusiveCallSign: "Peacock"));

        harness.RunStarted(app =>
        {
            Assert.Equal(
                new Uri("https://www.peacocktv.com/sports/mlb"),
                Assert.Single(app.UpcomingPlayers).StreamUrl);
            return Task.CompletedTask;
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestratorListTests`
Expected: build failure — `UpcomingPlayers`, `SettingsChanged` etc. do not exist.

- [ ] **Step 3: Write the implementation**

In `src/OnDeck.Core/AppOrchestrator.cs`, add the list properties right after the `StateChanged` event:

```csharp
    /// <summary>At bat or on the mound right now, in roster order.</summary>
    public IReadOnlyList<PlayerDisplay> ActivePlayers { get; private set; } = [];

    /// <summary>Game started, not currently active — pre-sorted per the MenuBarView rules.</summary>
    public IReadOnlyList<PlayerDisplay> InGamePlayers { get; private set; } = [];

    /// <summary>Game hasn't started, sorted by first pitch then name.</summary>
    public IReadOnlyList<PlayerDisplay> UpcomingPlayers { get; private set; } = [];

    /// <summary>Game over or substituted out, filtered to players with a matching stat line.</summary>
    public IReadOnlyList<PlayerDisplay> DonePlayers { get; private set; } = [];

    /// <summary>Drives the green tray icon.</summary>
    public bool HasActivePlayers => ActivePlayers.Count > 0;

    /// <summary>"A | B | C +2" — the tray tooltip.</summary>
    public string MenuBarTitleText
    {
        get
        {
            var names = ActivePlayers.Select(display => display.Name).ToList();
            return names.Count switch
            {
                0 => "",
                <= 3 => string.Join(" | ", names),
                _ => string.Join(" | ", names.Take(3)) + $" +{names.Count - 3}",
            };
        }
    }
```

Add the settings hook and the list builders at the end of the class:

```csharp
    /// <summary>
    /// Re-reads <see cref="ISettingsStore"/> and rebuilds the lists locally. The
    /// <c>hideBenchPlayers</c> didSet analog — never touches the network.
    /// </summary>
    public void SettingsChanged()
    {
        _hideBenchPlayers = _settings.HideBenchPlayers;
        UpdatePlayerLists();
    }

    // MARK: - List building

    private void UpdatePlayerLists()
    {
        var active = new List<PlayerDisplay>();
        var inGame = new List<PlayerDisplay>();
        var upcoming = new List<PlayerDisplay>();
        var done = new List<PlayerDisplay>();

        foreach (var player in _roster.Players)
        {
            if (player.IsUnavailable) continue;
            if (_hideBenchPlayers && player.IsOnBench) continue;

            switch (_states.PlayerStates.GetValueOrDefault(player.Id))
            {
                case PlayerState.Active:
                    active.Add(BuildLiveRow(player, isActive: true));
                    break;

                case PlayerState.Upcoming upcomingState:
                    if (GameFor(player) is { } game && _monitor.IsLive(game.Id))
                    {
                        inGame.Add(BuildLiveRow(player, isActive: false));
                    }
                    else
                    {
                        upcoming.Add(BuildUpcomingRow(player, upcomingState.StartTime));
                    }

                    break;

                case PlayerState.Inactive { Reason: PlayerState.InactiveReason.GameOver over }:
                    AddDoneRow(done, player, over.GamePk);
                    break;

                case PlayerState.Inactive { Reason: PlayerState.InactiveReason.Substituted substituted }:
                    AddDoneRow(done, player, substituted.GamePk);
                    break;
            }
        }

        ActivePlayers = active;
        InGamePlayers = [.. inGame.OrderBy(display => display.SortKey)];
        UpcomingPlayers =
        [
            .. upcoming
                .OrderBy(display => display.StartTime ?? DateTimeOffset.MaxValue)
                .ThenBy(display => display.Name, StringComparer.Ordinal)
        ];
        DonePlayers = [.. done.OrderBy(display => display.Player.IsHitter ? 0 : 1)];

        StateChanged?.Invoke();
    }

    private void AddDoneRow(List<PlayerDisplay> done, Player player, int gamePk)
    {
        var feed = _monitor.LatestFeeds.GetValueOrDefault(gamePk);
        if (DisplayRules.RawStatLine(player, feed) is not { } statLine) return;

        done.Add(new PlayerDisplay
        {
            Player = player,
            GamePk = gamePk,
            Feed = feed,
            StatLine = statLine,
        });
    }

    private PlayerDisplay BuildLiveRow(Player player, bool isActive)
    {
        var game = GameFor(player);
        var feed = game is null ? null : _monitor.LatestFeeds.GetValueOrDefault(game.Id);
        var lineup = game is null ? null : _monitor.LineupPlayerIds.GetValueOrDefault(game.Id);
        var proximity = DisplayRules.ProximityFor(player, feed);
        var isInLineup = DisplayRules.IsInLineup(player, game, lineup);

        return new PlayerDisplay
        {
            Player = player,
            GamePk = game?.Id,
            Feed = feed,
            IsActive = isActive,
            Proximity = proximity,
            IsInLineup = isInLineup,
            StatLine = game is null ? null : DisplayRules.LiveStatLine(player, feed, isInLineup, proximity),
            Delay = DisplayRules.DelayFor(feed?.DetailedState),
            StreamUrl = game is null ? null : StreamLinkRouter.Url(game),
            SortKey = DisplayRules.InGameSortKey(player, game, feed, lineup, proximity),
        };
    }

    private PlayerDisplay BuildUpcomingRow(Player player, DateTimeOffset startTime)
    {
        var game = GameFor(player);
        var feed = game is null ? null : _monitor.LatestFeeds.GetValueOrDefault(game.Id);
        var lineup = game is null ? null : _monitor.LineupPlayerIds.GetValueOrDefault(game.Id);

        return new PlayerDisplay
        {
            Player = player,
            GamePk = game?.Id,
            Feed = feed,
            Lineup = DisplayRules.LineupInfoFor(player, game, lineup, feed),
            Delay = DisplayRules.DelayFor(feed?.DetailedState),
            StartTime = startTime,
            StreamUrl = game is null ? null : StreamLinkRouter.Url(game),
        };
    }
```

Then wire the rebuild into initialization — at the end of `InitializePlayerStates`, after the second day-off loop:

```csharp
        UpdatePlayerLists();
```

Finally, rebuild whenever state or game status changes. Add this to the constructor, after
`_monitor.Configure(_states);` — Task 6 replaces both lines with the coalesced form:

```csharp
        _states.OnStateChange = (_, _, _) => UpdatePlayerLists();
        _monitor.OnGameStart = _ => UpdatePlayerLists();
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestrator`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/AppOrchestrator.cs windows/tests/OnDeck.Core.Tests/App/AppOrchestratorListTests.cs
git commit -m "phase 5: player list grouping, filters and sorting"
```

---

## Task 6: Coalesced rebuild and change wiring

**Files:**
- Modify: `src/OnDeck.Core/AppOrchestrator.cs`
- Create: `tests/OnDeck.Core.Tests/App/AppOrchestratorRebuildTests.cs`

**Spec:** `App/AppState.swift:209-236` (`setupStateChangeHandler`, `schedulePlayerListRebuild`), `:291-301` (`setupGameStartHandler`).

**Interfaces:**
- Consumes: `UpdatePlayerLists()` (Task 5), `INotificationSink.PurgeNotInLineupAsync` (Task 1).
- Produces on `AppOrchestrator`: `SynchronizationContext`-captured `Post(Action)`, `SchedulePlayerListRebuild()`, `RunGuardedAsync(Func<Task>)`, and the constructor wiring of `StateManager.OnStateChange` and `GameMonitor.OnGameStart`.

**Why it matters:** one poll cycle fires 10+ state updates (pitcher substitution sweep, batter and pitcher transitions). A full roster scan per update is wasteful, and mid-pass snapshots would flicker in the UI. The dirty flag plus a single posted continuation collapses one synchronous pass into one rebuild.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/App/AppOrchestratorRebuildTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorRebuildTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private static PlayerState.GameContext Context(int gamePk = 1) =>
        new(gamePk, PlayerState.ActiveRole.Batting, "Bot 3", "Dodgers", "Giants",
            119, 137, 2, 1, 1, 2, 1, false, false, false);

    private static OrchestratorHarness Harness() =>
        new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(102, "Freddie Freeman")
            .AddPlayer(103, "Will Smith")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

    [Fact]
    public void StateChanges_CollapseIntoOneRebuildPerTick()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            var rebuilds = 0;
            app.StateChanged += () => rebuilds++;

            harness.States.Update(101, new PlayerState.Active(Context()));
            harness.States.Update(102, new PlayerState.Active(Context()));
            harness.States.Update(103, new PlayerState.Active(Context()));
            Assert.Equal(0, rebuilds);          // nothing rebuilt synchronously

            await SingleThreadedContext.Settle();

            Assert.Equal(1, rebuilds);
            Assert.Equal(3, app.ActivePlayers.Count);
        });
    }

    [Fact]
    public void StateChanges_RebuildAgainOnTheNextTick()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();

            var rebuilds = 0;
            app.StateChanged += () => rebuilds++;

            harness.States.Update(102, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();

            Assert.Equal(1, rebuilds);
            Assert.Equal(2, app.ActivePlayers.Count);
        });
    }

    [Fact]
    public void StateChanged_FiresOnTheCoreContext()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            var pumpThread = Environment.CurrentManagedThreadId;
            var eventThread = 0;
            app.StateChanged += () => eventThread = Environment.CurrentManagedThreadId;

            harness.States.Update(101, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();

            Assert.Equal(pumpThread, eventThread);
        });
    }

    [Fact]
    public void GameStart_RebuildsAndPurgesNotInLineupForThatGame()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            Assert.Equal(3, app.UpcomingPlayers.Count);

            harness.GoLive(1);
            await SingleThreadedContext.Settle();

            Assert.Empty(app.UpcomingPlayers);
            Assert.Equal(3, app.InGamePlayers.Count);
            Assert.Contains("purgeNotInLineup:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void GameStart_FiresOnlyOnce()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.GoLive(1);
            harness.GoLive(1);
            await SingleThreadedContext.Settle();

            Assert.Single(harness.Sink.Calls.Where(call => call == "purgeNotInLineup:1"));
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestratorRebuildTests`
Expected: FAIL — `rebuilds` stays 0 (nothing is wired to `OnStateChange`) and `purgeNotInLineup:1` is never recorded.

- [ ] **Step 3: Write the implementation**

Add the captured context field next to the other fields in `src/OnDeck.Core/AppOrchestrator.cs`:

```csharp
    private readonly SynchronizationContext? _context;
    private bool _playerListsDirty;
```

Capture it in the constructor and **replace** Task 5's two direct-rebuild handler assignments:

```csharp
        _context = SynchronizationContext.Current;

        _states.OnStateChange = HandleStateChange;
        _monitor.OnGameStart = HandleGameStart;
```

Add the scheduling helpers at the end of the class:

```csharp
    // MARK: - Change handling

    private void HandleStateChange(int playerId, PlayerState? oldState, PlayerState newState) =>
        SchedulePlayerListRebuild();

    private void HandleGameStart(int gamePk)
    {
        // The feed just flipped this game to In Progress - rebuild so upcoming players on it
        // move to the in-game bucket.
        SchedulePlayerListRebuild();
        RunGuarded(() => _notifications.PurgeNotInLineupAsync(gamePk));
    }

    /// <summary>
    /// Coalesces list rebuilds. A single poll cycle can fire 10+ state updates (pitcher
    /// substitution sweep, batter and pitcher transitions); a full roster scan for each is
    /// wasteful. Defer to the next tick on the Core context so all updates in one synchronous
    /// pass collapse into one rebuild.
    /// </summary>
    private void SchedulePlayerListRebuild()
    {
        if (_playerListsDirty) return;
        _playerListsDirty = true;

        Post(() =>
        {
            _playerListsDirty = false;
            UpdatePlayerLists();
        });
    }

    /// <summary>
    /// Queues work on the Core context — Swift's <c>Task { @MainActor in ... }</c>. Falls back
    /// to a yielded continuation only when no context is installed (never true in the app or
    /// in tests).
    /// </summary>
    private void Post(Action action)
    {
        if (_context is not null)
        {
            _context.Post(static state => ((Action)state!)(), action);
            return;
        }

        _ = YieldThen(action);

        static async Task YieldThen(Action queued)
        {
            await Task.Yield();
            queued();
        }
    }

    /// <summary>
    /// Fire-and-forget on the Core context. The sink is shell-implemented (the toast API can
    /// throw); a failed notification must not tear down the transition pipeline.
    /// </summary>
    private void RunGuarded(Func<Task> work) => _ = RunGuardedAsync(work);

    private static async Task RunGuardedAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppOrchestrator] notification work failed: {ex}");
        }
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestrator`
Expected: PASS, including every `AppOrchestratorListTests` case.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/AppOrchestrator.cs windows/tests/OnDeck.Core.Tests/App/AppOrchestratorRebuildTests.cs
git commit -m "phase 5: coalesced list rebuild and game-start wiring"
```

---

## Task 7: Not-in-lineup reconciliation

**Files:**
- Modify: `src/OnDeck.Core/AppOrchestrator.cs`
- Create: `tests/OnDeck.Core.Tests/App/AppOrchestratorLineupTests.cs`

**Spec:** `App/AppState.swift:238-289` (`reconcileLineupNotifications`, `setupLineupUpdateHandler`), `:176` (`purgeAll` on every schedule refresh), `:183` (`notifiedNotInLineup.removeAll()`).

**Interfaces:**
- Consumes: `RunGuarded` (Task 6), `INotificationSink.NotifyNotInLineupAsync` (Task 1).
- Produces on `AppOrchestrator`: `private readonly HashSet<int> _notifiedNotInLineup`, `ReconcileLineupNotificationsAsync(int gamePk)`, the `GameMonitor.OnLineupUpdate` wiring, plus the two call sites in `FetchScheduleAndStartMonitoringAsync`.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/App/AppOrchestratorLineupTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorLineupTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private const string Matchup = "San Francisco Giants @ Los Angeles Dodgers";

    /// <summary>Card filed for the Dodgers without 199.</summary>
    private static OrchestratorHarness FiledWithout199() =>
        new OrchestratorHarness()
            .AddPlayer(199, "Left Out")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

    [Fact]
    public void Reconcile_NotifiesActiveHittersMissingFromTheFiledCard()
    {
        var harness = FiledWithout199();

        harness.RunStarted(_ =>
        {
            Assert.Equal(
                [$"notInLineup:199:1:{Matchup}:{OrchestratorHarness.LeagueUrl}"],
                harness.Sink.Calls.Where(call => call.StartsWith("notInLineup:", StringComparison.Ordinal)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_NotifiesOnlyOncePerPlayer()
    {
        var harness = FiledWithout199();

        harness.RunStarted(async app =>
        {
            // A later feed changes the card, firing OnLineupUpdate again.
            harness.Monitor.ProcessFeed(
                new LiveFeedData
                {
                    GameState = "Preview",
                    DetailedState = "Pre-Game",
                    HomeBattingOrder = [101, 102, 103, 104],
                },
                1,
                harness.Schedule.TodaysGames[0]);

            await SingleThreadedContext.Settle();

            Assert.Single(harness.Sink.Calls.Where(
                call => call.StartsWith("notInLineup:", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void Reconcile_SkipsPlayersWhoAreOnTheCard()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "In The Card")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_SkipsPlayersWhoArentOnTheActiveRoster()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "On The Bench", statusId: 2)
            .AddPlayer(198, "On The IL", statusId: 3)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_SkipsPitchers()
    {
        // Relievers are never on the batting card, so its contents say nothing about them.
        var harness = new OrchestratorHarness()
            .AddPlayer(901, "Reliever", positions: "RP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_SkipsOnlyTheOpponentsCard()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "Left Out")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, awayLineup: [201, 202, 203]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_SkipsOnceTheFeedReportsTheGameLive()
    {
        // Too late to act on a bench swap.
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "Left Out")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.SeedFeed(1, feed => feed.GameState = "Live");

            harness.Monitor.ProcessFeed(
                new LiveFeedData
                {
                    GameState = "Preview",
                    DetailedState = "Pre-Game",
                    HomeBattingOrder = [101, 102, 103],
                },
                1,
                harness.Schedule.TodaysGames[0]);

            await SingleThreadedContext.Settle();

            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Reconcile_SkipsWhenTheScheduledStartHasPassedAndNoFeedExists()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "Left Out")
            .AddGame(OrchestratorHarness.GameOf(
                1, OrchestratorHarness.Now.AddMinutes(-30), homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_StartsOverOnEveryScheduleRefresh()
    {
        var harness = FiledWithout199();

        harness.RunStarted(async app =>
        {
            await app.ResyncRosterAsync();
            await SingleThreadedContext.Settle();

            Assert.Equal(
                2,
                harness.Sink.Calls.Count(call => call.StartsWith("notInLineup:", StringComparison.Ordinal)));
        });
    }

}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestratorLineupTests`
Expected: FAIL — no `notInLineup:` call is ever recorded.

- [ ] **Step 3: Write the implementation**

Add the one-shot set to the fields in `src/OnDeck.Core/AppOrchestrator.cs`:

```csharp
    private readonly HashSet<int> _notifiedNotInLineup = [];
```

Wire the lineup handler in the constructor, next to the other two:

```csharp
        _monitor.OnLineupUpdate = gamePk => RunGuarded(() => ReconcileLineupNotificationsAsync(gamePk));
```

In `FetchScheduleAndStartMonitoringAsync`, clear the set right after `_states.Reset();`:

```csharp
        _notifiedNotInLineup.Clear();
```

...and reconcile each seeded game, inside the seeding loop's `if` block right after the assignment:

```csharp
                _monitor.LineupPlayerIds[game.Id] = lineup;
                await ReconcileLineupNotificationsAsync(game.Id);
```

Add the reconciler at the end of the class:

```csharp
    /// <summary>
    /// Fires a one-shot "not in lineup" notification for active-roster hitters whose team is
    /// playing in the given game but who are not on the posted lineup card.
    /// </summary>
    private async Task ReconcileLineupNotificationsAsync(int gamePk)
    {
        if (_games.FirstOrDefault(candidate => candidate.Id == gamePk) is not { } game) return;
        if (!_monitor.LineupPlayerIds.TryGetValue(gamePk, out var lineup)) return;

        // Don't notify once the game has started - too late to act on a bench swap. Prefer
        // live feed state when available, otherwise fall back to the scheduled start.
        if (_monitor.LatestFeeds.TryGetValue(gamePk, out var feed))
        {
            if (feed.GameState is "Live" or "Final") return;
        }
        else if (game.StartTime <= _time.GetUtcNow())
        {
            return;
        }

        var fantraxUrl = Uri.TryCreate(RosterUrl, UriKind.Absolute, out var parsed) ? parsed : null;
        var matchup = $"{game.AwayTeam} @ {game.HomeTeam}";

        // Snapshot the roster: the await below can outlive a resync that replaces it.
        foreach (var player in _roster.Players)
        {
            if (player.RosterStatus != RosterStatus.Active) continue;
            if (_notifiedNotInLineup.Contains(player.Id)) continue;
            if (game.SideFor(player) is not { } side) continue;
            if (!lineup.Excludes(player, side)) continue;

            _notifiedNotInLineup.Add(player.Id);

            await _notifications.NotifyNotInLineupAsync(
                player.Name, player.Id, gamePk, matchup, fantraxUrl);
        }
    }
```

Note the `foreach` walks `_roster.Players`, which `RosterManager` replaces wholesale on sync rather than mutating — the enumerator holds the old snapshot, matching Swift's array value semantics.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestrator`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/AppOrchestrator.cs windows/tests/OnDeck.Core.Tests/App/AppOrchestratorLineupTests.cs
git commit -m "phase 5: not-in-lineup reconciliation"
```

---

## Task 8: State transitions and race guards

**Files:**
- Modify: `src/OnDeck.Core/AppOrchestrator.cs`
- Create: `tests/OnDeck.Core.Tests/App/AppOrchestratorTransitionTests.cs`

**Spec:** `App/AppState.swift:391-493` (`isStillActive`, `handleStateTransition`, `streamURL`, `formatGameString`).

**Interfaces:**
- Consumes: `RunGuarded` (Task 6), all five `INotificationSink` notify methods and the two synchronous purges (Task 1).
- Produces on `AppOrchestrator`: `HandleStateTransitionAsync(int playerId, PlayerState? oldState, PlayerState newState)`, `IsStillActive(int playerId, PlayerState.ActiveRole role)`, `StreamUrlFor(int gamePk)`, `FormatGameString(PlayerState.GameContext)`, plus the dispatch from `HandleStateChange`.

**The race guard:** after every `await` on a notification send, re-check the player's state. If it moved on during the send, purge — otherwise the toast sticks in the Action Center with nothing behind it. This is the reason Core forbids `ConfigureAwait(false)`: the continuation must come back to the same context that owns `PlayerStates`.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/App/AppOrchestratorTransitionTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorTransitionTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private const string GameString = "Giants 1 - Dodgers 2";

    private static PlayerState.GameContext Context(
        PlayerState.ActiveRole role = PlayerState.ActiveRole.Batting, int gamePk = 1) =>
        new(gamePk, role, "Bot 3", "Dodgers", "Giants", 119, 137, 2, 1, 1, 2, 1, false, false, false);

    private static PlayerState Active(PlayerState.ActiveRole role = PlayerState.ActiveRole.Batting) =>
        new PlayerState.Active(Context(role));

    private static OrchestratorHarness Harness() =>
        new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(901, "Blake Snell", positions: "SP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, exclusiveCallSign: "Peacock"));

    [Fact]
    public void Transition_ToBattingNotifiesWithTheGameStringInningAndStreamLink()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.Equal(
                $"batting:101:1:{GameString}:Bot 3:https://www.peacocktv.com/sports/mlb",
                Assert.Single(harness.Sink.Calls.Where(call => call.StartsWith("batting:", StringComparison.Ordinal))));
        });
    }

    [Fact]
    public void Transition_ToPitchingNotifies()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();

            Assert.Equal(
                $"pitching:901:1:{GameString}:Bot 3:https://www.peacocktv.com/sports/mlb",
                Assert.Single(harness.Sink.Calls.Where(call => call.StartsWith("pitching:", StringComparison.Ordinal))));
        });
    }

    [Fact]
    public void Transition_ActiveToActiveDoesNotResend()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            // Same role, updated count/score - the Mac only notifies on entering active.
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.Single(harness.Sink.Calls.Where(call => call.StartsWith("batting:", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void Transition_PurgesBattingWhenTheStateChangesDuringTheSend()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.Sink.DuringNotify = async () =>
            {
                harness.Sink.DuringNotify = null;       // only interfere with the first send
                await Task.Yield();
                harness.States.Update(101, new PlayerState.Upcoming(FirstPitch));
            };

            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.Contains("purgeBatting:101:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_PurgesPitchingWhenTheStateChangesDuringTheSend()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.Sink.DuringNotify = async () =>
            {
                harness.Sink.DuringNotify = null;
                await Task.Yield();
                harness.States.Update(901, new PlayerState.Upcoming(FirstPitch));
            };

            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgePitching:901:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_KeepsTheNotificationWhenTheStateHoldsThroughTheSend()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.Sink.DuringNotify = async () => await Task.Yield();

            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.DoesNotContain("purgeBatting:101:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_OutOfBattingPurgesAndReportsTheAtBatResult()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();
            harness.Monitor.LastPlayDescriptions[101] = "Mookie Betts doubles (12) on a line drive";

            harness.States.Update(101, new PlayerState.Upcoming(FirstPitch));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgeBatting:101:1", harness.Sink.Calls);
            Assert.Contains(
                "atBatResult:101:Mookie Betts doubles (12) on a line drive", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_OutOfBattingWithoutADescriptionOnlyPurges()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            harness.States.Update(101, new PlayerState.Upcoming(FirstPitch));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgeBatting:101:1", harness.Sink.Calls);
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("atBatResult:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_OutOfPitchingOnlyPurges()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();
            harness.Monitor.LastPlayDescriptions[901] = "strikeout swinging";

            harness.States.Update(901, new PlayerState.Upcoming(FirstPitch));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgePitching:901:1", harness.Sink.Calls);
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("atBatResult:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_PitcherPulledPurgesAndReportsTheResult()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();

            harness.States.Update(
                901, new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(1)));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgePitching:901:1", harness.Sink.Calls);
            Assert.Contains(
                "pitchingResult:901:Blake Snell has been pulled from the game", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_HitterSubstitutedSendsNothing()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();
            var callsBefore = harness.Sink.Calls.Count;

            harness.States.Update(
                101, new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(1)));
            await SingleThreadedContext.Settle();

            Assert.Equal(callsBefore, harness.Sink.Calls.Count);
        });
    }

    [Fact]
    public void Transition_GameOverPurgesBothRoles()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();

            harness.States.SetGameOver([101, 901], 1);
            await SingleThreadedContext.Settle();

            Assert.Contains("purgeBatting:101:1", harness.Sink.Calls);
            Assert.Contains("purgePitching:901:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_IgnoresUnavailablePlayers()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(198, "On The IL", statusId: 3)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.States.Update(198, Active());
            await SingleThreadedContext.Settle();

            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("batting:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_IgnoresBenchPlayersWhenTheyAreHidden()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "On The Bench", statusId: 2)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.Settings.HideBenchPlayers = true;

        harness.RunStarted(async app =>
        {
            harness.States.Update(199, Active());
            await SingleThreadedContext.Settle();

            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("batting:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_IgnoresPlayersWhoAreNotOnTheRoster()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(555, Active());
            await SingleThreadedContext.Settle();

            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("batting:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_FallsBackToTheMlbTvLinkWithoutAnExclusiveBroadcast()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.EndsWith("https://www.mlb.com/tv/g1", harness.Sink.Calls.First(
                call => call.StartsWith("batting:", StringComparison.Ordinal)));
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestratorTransitionTests`
Expected: FAIL — no `batting:`/`pitching:` calls are recorded.

- [ ] **Step 3: Write the implementation**

Dispatch the transition from `HandleStateChange` in `src/OnDeck.Core/AppOrchestrator.cs`:

```csharp
    private void HandleStateChange(int playerId, PlayerState? oldState, PlayerState newState)
    {
        SchedulePlayerListRebuild();
        RunGuarded(() => HandleStateTransitionAsync(playerId, oldState, newState));
    }
```

Add the transition handler and its helpers at the end of the class:

```csharp
    // MARK: - Notifications

    private bool IsStillActive(int playerId, PlayerState.ActiveRole role) =>
        _states.PlayerStates.GetValueOrDefault(playerId) is PlayerState.Active active
        && active.Context.Role == role;

    private async Task HandleStateTransitionAsync(int playerId, PlayerState? oldState, PlayerState newState)
    {
        if (_roster.Players.FirstOrDefault(candidate => candidate.Id == playerId) is not { } player) return;
        if (player.IsUnavailable) return;
        if (_hideBenchPlayers && player.IsOnBench) return;

        switch (newState)
        {
            case PlayerState.Active { Context: var context } when oldState is not PlayerState.Active:
            {
                var gameString = FormatGameString(context);
                var streamUrl = StreamUrlFor(context.GamePk);

                if (context.Role == PlayerState.ActiveRole.Pitching)
                {
                    await _notifications.NotifyPitchingAsync(
                        player.Name, player.Id, context.GamePk, gameString, context.Inning, streamUrl);

                    // Race guard: state may have changed during the async send.
                    if (!IsStillActive(playerId, PlayerState.ActiveRole.Pitching))
                    {
                        _notifications.PurgePitching(context.GamePk, playerId);
                    }
                }
                else
                {
                    await _notifications.NotifyBattingAsync(
                        player.Name, player.Id, context.GamePk, gameString, context.Inning, streamUrl);

                    if (!IsStillActive(playerId, PlayerState.ActiveRole.Batting))
                    {
                        _notifications.PurgeBatting(context.GamePk, playerId);
                    }
                }

                break;
            }

            case PlayerState.Upcoming when oldState is PlayerState.Active { Context: var context }:
            {
                if (context.Role == PlayerState.ActiveRole.Batting)
                {
                    _notifications.PurgeBatting(context.GamePk, playerId);

                    if (_monitor.LastPlayDescriptions.TryGetValue(playerId, out var description))
                    {
                        await _notifications.NotifyAtBatResultAsync(
                            player.Name, player.Id, description, StreamUrlFor(context.GamePk));
                    }
                }
                else
                {
                    _notifications.PurgePitching(context.GamePk, playerId);
                }

                break;
            }

            case PlayerState.Inactive { Reason: PlayerState.InactiveReason.Substituted }
                when oldState is PlayerState.Active { Context: var context }:
            {
                if (context.Role != PlayerState.ActiveRole.Pitching) break;

                _notifications.PurgePitching(context.GamePk, playerId);
                await _notifications.NotifyPitchingResultAsync(
                    player.Name,
                    player.Id,
                    $"{player.Name} has been pulled from the game",
                    StreamUrlFor(context.GamePk));
                break;
            }

            case PlayerState.Inactive { Reason: PlayerState.InactiveReason.GameOver }
                when oldState is PlayerState.Active { Context: var context }:
            {
                if (context.Role == PlayerState.ActiveRole.Batting)
                {
                    _notifications.PurgeBatting(context.GamePk, playerId);
                }
                else
                {
                    _notifications.PurgePitching(context.GamePk, playerId);
                }

                break;
            }
        }
    }

    private Uri? StreamUrlFor(int gamePk) =>
        _games.FirstOrDefault(game => game.Id == gamePk) is { } match ? StreamLinkRouter.Url(match) : null;

    private static string FormatGameString(PlayerState.GameContext context) =>
        $"{context.AwayTeam} {context.AwayScore} - {context.HomeTeam} {context.HomeScore}";
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestrator`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/AppOrchestrator.cs windows/tests/OnDeck.Core.Tests/App/AppOrchestratorTransitionTests.cs
git commit -m "phase 5: state transition notifications with post-await race guards"
```

---

## Task 9: Pre-game, daily and resume refreshes

**Files:**
- Modify: `src/OnDeck.Core/AppOrchestrator.cs`
- Create: `tests/OnDeck.Core.Tests/App/AppOrchestratorScheduleTests.cs`

**Spec:** `App/AppState.swift:495-520` (`schedulePreGameRefresh`), `:522-556` (`handleSystemResume`), `:558-588` (`scheduleDailyRefresh`).

**Interfaces:**
- Consumes: `ResyncRosterAsync` (Task 4), `RunGuarded` (Task 6).
- Produces on `AppOrchestrator`: `public Task HandleSystemResumeAsync()`, `SchedulePreGameRefresh()`, `ScheduleDailyRefresh()`, `internal TimeSpan TimeUntilNextEightAm()`, plus the two new call sites.

**The gotcha this task exists to preserve:** `schedulePreGameRefresh` must skip when the refresh window has already passed. Resyncing after games have started restarts monitoring, which cancels in-flight requests and reschedules another refresh — an infinite restart loop.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/App/AppOrchestratorScheduleTests.cs`:

```csharp
namespace OnDeck.Core.Tests.App;

public class AppOrchestratorScheduleTests
{
    [Fact]
    public void PreGameRefresh_ResyncsFifteenMinutesBeforeTheFirstGame()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddMinutes(20)));

        harness.RunStarted(async app =>
        {
            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));

            harness.Time.Advance(TimeSpan.FromMinutes(5));
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void PreGameRefresh_DoesNotFireEarly()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddMinutes(20)));

        harness.RunStarted(async app =>
        {
            harness.Time.Advance(TimeSpan.FromMinutes(4));
            await SingleThreadedContext.Settle();

            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void PreGameRefresh_IsSkippedWhenTheFirstGameAlreadyStarted()
    {
        // The infinite-restart gotcha: resyncing after start cancels in-flight requests and
        // reschedules itself forever.
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddMinutes(-5)));

        harness.RunStarted(async app =>
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await SingleThreadedContext.Settle();

            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void PreGameRefresh_UsesTheEarliestGame()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(201, "Rafael Devers", fantraxTeam: "BOS")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddHours(4)))
            .AddGame(OrchestratorHarness.GameOf(
                2, OrchestratorHarness.Now.AddMinutes(20), home: "Boston Red Sox",
                away: "New York Yankees", homeTeamId: 111, awayTeamId: 147));

        harness.RunStarted(async app =>
        {
            harness.Time.Advance(TimeSpan.FromMinutes(5));
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void DailyRefresh_FiresAtTheNextEightAm()
    {
        // 14:00 UTC start - the next 8 AM is 18 hours out. No games, so nothing else resyncs.
        var harness = new OrchestratorHarness().AddPlayer(101, "Mookie Betts");

        harness.RunStarted(async app =>
        {
            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));

            harness.Time.Advance(TimeSpan.FromHours(18));
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void DailyRefresh_FiresTheSameMorningWhenStartedBeforeEight()
    {
        var harness = new OrchestratorHarness(new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero))
            .AddPlayer(101, "Mookie Betts");

        harness.RunStarted(async app =>
        {
            harness.Time.Advance(TimeSpan.FromHours(5));
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void TimeUntilNextEightAm_IsLocalAndNeverInThePast()
    {
        var beforeEight = new OrchestratorHarness(new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero));
        beforeEight.Run(app =>
        {
            Assert.Equal(TimeSpan.FromHours(5), app.TimeUntilNextEightAm());
            return Task.CompletedTask;
        });

        var afterEight = new OrchestratorHarness(new DateTimeOffset(2026, 8, 8, 9, 30, 0, TimeSpan.Zero));
        afterEight.Run(app =>
        {
            Assert.Equal(TimeSpan.FromHours(22.5), app.TimeUntilNextEightAm());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void HandleSystemResumeAsync_InvalidatesTimecodesAndResyncs()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddHours(5)));

        harness.RunStarted(async app =>
        {
            var feed = harness.SeedFeed(1, seeded => seeded.TimeStamp = "20260808_140000");

            await app.HandleSystemResumeAsync();
            await SingleThreadedContext.Settle();

            Assert.Null(feed.TimeStamp);
            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void HandleSystemResumeAsync_DebouncesWithinThirtySeconds()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddHours(5)));

        harness.RunStarted(async app =>
        {
            await app.HandleSystemResumeAsync();
            harness.Time.Advance(TimeSpan.FromSeconds(20));
            await app.HandleSystemResumeAsync();
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));

            harness.Time.Advance(TimeSpan.FromSeconds(31));
            await app.HandleSystemResumeAsync();
            await SingleThreadedContext.Settle();

            Assert.Equal(3, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void HandleSystemResumeAsync_DoesNothingWithoutARosterUrlOrTeam()
    {
        var harness = new OrchestratorHarness().AddPlayer(101, "Mookie Betts");

        harness.RunStarted(async app =>
        {
            var before = harness.Http.CountRequests("fxpa/req");
            harness.Settings.RosterUrl = "";

            await app.HandleSystemResumeAsync();
            await SingleThreadedContext.Settle();

            Assert.Equal(before, harness.Http.CountRequests("fxpa/req"));
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppOrchestratorScheduleTests`
Expected: build failure — `HandleSystemResumeAsync` and `TimeUntilNextEightAm` do not exist.

- [ ] **Step 3: Write the implementation**

Add the two cancellation sources and the debounce stamp to the fields in `src/OnDeck.Core/AppOrchestrator.cs`:

```csharp
    private CancellationTokenSource? _preGameRefresh;
    private CancellationTokenSource? _dailyRefresh;
    private DateTimeOffset _lastResumeTime = DateTimeOffset.MinValue;
```

Schedule the daily refresh at the very end of `StartAsync`, after `await FetchScheduleAndStartMonitoringAsync();`:

```csharp
        ScheduleDailyRefresh();
```

Schedule the pre-game refresh at the very end of `FetchScheduleAndStartMonitoringAsync`, after the lineup-seeding loop:

```csharp
        SchedulePreGameRefresh();
```

Add the three methods at the end of the class:

```csharp
    // MARK: - Pre-game refresh (15 min before the first game)

    private void SchedulePreGameRefresh()
    {
        _preGameRefresh?.Cancel();
        _preGameRefresh?.Dispose();
        _preGameRefresh = null;

        if (_games.Count == 0) return;

        var earliestStart = _games.Min(game => game.StartTime);
        var delay = earliestStart - TimeSpan.FromMinutes(15) - _time.GetUtcNow();

        // Already past the refresh window - monitoring is running. Resyncing here restarts
        // monitoring, which cancels in-flight requests and reschedules this: an infinite loop.
        if (delay <= TimeSpan.Zero) return;

        var refresh = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _preGameRefresh = refresh;

        RunGuarded(async () =>
        {
            try
            {
                await Task.Delay(delay, _time, refresh.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ResyncRosterAsync();
        });
    }

    // MARK: - Sleep/wake and unlock recovery

    public async Task HandleSystemResumeAsync()
    {
        // Debounce: skip if recovery ran within the last 30 seconds.
        var now = _time.GetUtcNow();
        if (now - _lastResumeTime <= TimeSpan.FromSeconds(30)) return;
        _lastResumeTime = now;

        if (RosterUrl.Length == 0 || EffectiveTeamId is null) return;

        _monitor.InvalidateTimecodes();
        await ResyncRosterAsync();
    }

    // MARK: - Daily refresh (8 AM)

    private void ScheduleDailyRefresh()
    {
        _dailyRefresh?.Cancel();
        _dailyRefresh?.Dispose();

        var refresh = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _dailyRefresh = refresh;

        RunGuarded(async () =>
        {
            while (!refresh.Token.IsCancellationRequested)
            {
                var interval = TimeUntilNextEightAm();
                if (interval < TimeSpan.FromSeconds(60)) interval = TimeSpan.FromSeconds(60);

                try
                {
                    await Task.Delay(interval, _time, refresh.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await ResyncRosterAsync();
            }
        });
    }

    /// <summary>Time until the next 8 AM local — today's if it hasn't passed, else tomorrow's.</summary>
    internal TimeSpan TimeUntilNextEightAm()
    {
        var now = _time.GetLocalNow();
        var eightAm = new DateTimeOffset(now.Year, now.Month, now.Day, 8, 0, 0, now.Offset);
        var next = now.Hour < 8 ? eightAm : eightAm.AddDays(1);
        return next - now;
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test windows/OnDeck.slnx`
Expected: PASS across the whole solution, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/AppOrchestrator.cs windows/tests/OnDeck.Core.Tests/App/AppOrchestratorScheduleTests.cs
git commit -m "phase 5: pre-game, daily and system-resume refresh scheduling"
```

---

## Done criteria

- [ ] `dotnet test windows/OnDeck.slnx` → `Failed: 0`
- [ ] `grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj` → `0`
- [ ] `grep -rn "ConfigureAwait" windows/src/OnDeck.Core` → no matches
- [ ] Single-file publish still green:
  ```bash
  dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true
  ```
- [ ] Every member of the `AppOrchestrator` and `INotificationSink` contracts in `PORT_PLAN.md` exists with the contract's name and type
- [ ] `git status --short` → clean
- [ ] This plan's **Deviations** section filled in, and the phase's rows appended to `windows/HANDOFF.md` §8
- [ ] `windows/HANDOFF.md` §9 replaced with a Phase 6 hand-off (the toast-activation spike is the entry point)

## Deviations from the Swift original

Fill in during execution. Known going in:

| Deviation | Why |
|---|---|
| `ActivePlayers` added to the `AppOrchestrator` contract | `MenuBarView.swift` renders an "Active Now" section from `appState.activePlayers`; the contract in `PORT_PLAN.md` listed only the other three lists. Additive — `HasActivePlayers` is derived from it |
| `ParsedLeagueId` / `UrlHasTeamId` / `EffectiveTeamId` are public | `SettingsView.swift` and the flyout footer read all three off `AppState`; Phases 7-8 need them |
| `PlayerDisplay` carries the whole `LiveFeedData` | `LivePlayerRow` reads a dozen feed fields (score, bases, count, outs, inning, half); passing the snapshot avoids inventing a parallel projection |
| `BattingProximity` / `LineupInfo` are `Kind` + `Value` structs, not record hierarchies | A nested case type named `OnDeck` inside namespace `OnDeck.Core` makes the bare identifier ambiguous at type-name position |
| `DelayIndicator` replaces `delayIcon`'s SF Symbol names | Core classifies; the shell picks the icon |
| `NotificationManager.requestPermission()` not called from `StartAsync` | Shell concern — Phase 9's `ToastService` |
| `MemoryPressureRelief.releaseReclaimablePages()` dropped from the resume path | macOS-only, per the master plan |
| `FloatingPanel` auto-open on launch dropped from startup | Shell concern — Phase 7 reads `ISettingsStore.AlwaysOpenPopout` |
| Transition/notification work runs through `RunGuarded` (catch + `Debug.WriteLine`) | The sink is shell-implemented and the toast API can throw; a failed notification must not tear down the pipeline. Swift's detached `Task` has the same effect by default |
| `SettingsChanged()` is an explicit call, not a property `didSet` | Core has no observation framework; the shell writes `ISettingsStore` then calls it |


