# onDeck Windows Port — Handoff

**As of:** 2026-08-08, end of Phase 4. Branch: `main`. 32 commits since `33e8031`.

**State:** `OnDeck.Core` is complete except `AppOrchestrator`. 362 tests green, working tree clean,
single-file publish verified. No shell work has started.

---

## 1. Read these first, in this order

1. `windows/PORT_PLAN.md` — the master decomposition. Phases, porting map, cross-phase interface
   contracts, parity checklist, resolved decisions. **The interface contracts in there are binding**
   — later phases depend on those exact shapes.
2. `CLAUDE.md` (repo root, **gitignored** via `C:/Users/brian/.gitignore_global:13` — it exists
   locally but will not be on a fresh clone). Holds the gotchas the port must preserve.
3. The Swift source for the phase you're on. **The Swift file named in each phase is the
   authoritative spec** — read it before writing anything.
4. `windows/plans/*.md` — the four executed phase plans. Each has a "Deviations" section recording
   where the C# port intentionally differs from Swift and why.

## 2. Workflow in use

Per `PORT_PLAN.md`'s own instruction, each phase gets a detailed TDD plan written at execution time,
then executed:

1. Read the Swift spec files for the phase.
2. `superpowers:writing-plans` → save to `windows/plans/YYYY-MM-DD-phase-N-<name>.md`.
   Every task gets real test code and real implementation code — no placeholders.
3. `superpowers:executing-plans` → work tasks in order. Per task: write failing test → run it and
   confirm the failure → implement → run and confirm pass → commit.
4. End of phase: full `dotnet test` + the single-file publish check.

Subagents are **not** used in this project (session rule). Inline execution only.

## 3. Environment

- **.NET 10 SDK 10.0.302**, installed machine-wide at `C:\Program Files\dotnet`.
- **`windows/NuGet.config` is load-bearing.** The machine-wide `NuGet.Config`
  (`%APPDATA%\NuGet\NuGet.Config`) has an empty `<packageSources>`, which clears the implicit
  nuget.org default and makes every restore fail `NU1100`. The solution-scoped config re-adds
  nuget.org. Don't delete it, and don't "fix" it by editing the global file.
- `dotnet new sln` produced **`OnDeck.slnx`** (the .NET 10 default XML format), not `OnDeck.sln` as
  the master plan's layout says.

### Commands (run from repo root)

```bash
dotnet test windows/OnDeck.slnx
dotnet test windows/tests/OnDeck.Core.Tests --filter FullyQualifiedName~SomeTests
dotnet build windows/OnDeck.slnx

dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

Note: a bare `dotnet test` from the repo root fails — there's no project there. Always pass the
solution or project path.

## 4. User preferences (standing)

- **Never append `Co-Authored-By` or any AI-attribution trailer to commits.** Global rule.
- Work is committed **directly to `main`**, no feature branch.
- `CLAUDE.md` says "Don't build or deploy for the user". That is scoped to the **macOS Xcode app**
  (it sits among the `xcodebuild` / `/Applications` commands). Running `dotnet build`/`test` for the
  port is expected — TDD can't verify anything otherwise. Don't launch or install the Windows app.

## 5. What's built

```
windows/
├── OnDeck.slnx, Directory.Build.props, NuGet.config, .gitignore
├── PORT_PLAN.md, HANDOFF.md, plans/
├── src/OnDeck.Core/          net10.0, ZERO package references — keep it that way
│   ├── ISettingsStore.cs
│   ├── Models/               Player, PlayerState, Game+GameLineup, LiveFeedData+stats
│   ├── Networking/           MlbStatsApi, FantraxApi, FantraxModels, LiveFeedDecoder, DiffPatchResult
│   ├── Utilities/            LiveFeedPatcher, PatchOperation, UnknownPatchLogger, TeamMapping,
│   │                         NameCleaner, FantraxUrlParser, StreamLinkRouter, HeadshotCache,
│   │                         BaseballCalendar
│   └── Managers/             RosterManager, ScheduleManager, StateManager, GameMonitor
├── src/OnDeck.App/           net10.0-windows WPF — still the bare template
└── tests/OnDeck.Core.Tests/  net10.0, xunit v2 + Microsoft.Extensions.TimeProvider.Testing
```

`Microsoft.Extensions.TimeProvider.Testing` is a **test-project-only** dependency. `OnDeck.Core`
must keep zero package references — verify with
`grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj` (expect 0).

## 6. Conventions established

**Naming.** Swift `lowerCamelCase` → C# `PascalCase`. Swift `ID` suffix → `Id` (`homeTeamID` →
`HomeTeamId`; `gamePk` stays `GamePk`). Swift `Date` → `DateTimeOffset` everywhere. Swift nested
types stay nested *unless* they'd collide with a property name.

**Swift value semantics need hand-written equality.** C# records compare collection members by
*reference*; Swift `Set`/`Array` are value types. `Player`, `Game`, `GameLineup`, `LiveFeedData` and
the three stats types all implement `Equals`/`GetHashCode` by hand using `SetEquals`/`SequenceEqual`.
Any new type holding a collection needs the same treatment.

**Swift enum-with-associated-values** → closed record hierarchy with a private constructor
(`PlayerState`, `PlayerState.InactiveReason`, `DiffPatchResult`).

**Async.** No `ConfigureAwait(false)` anywhere in Core — the single-logical-thread model requires
continuations return to the captured context. Swift `withTaskGroup` → `Task.WhenAll`.

**Testing.** `StubHttpMessageHandler` (records requests + bodies, replays queued responses, repeats
the last once drained). `FakeTimeProvider` for anything clock-driven. Tests assert **request URLs and
POST bodies**, not just response parsing — the `hydrate` terms, timecode formats and the Fantrax
`period` param are what break silently.

### Three traps that already bit

1. **`Uri.ToString()` unescapes for display.** Assert percent-encoding against `AbsoluteUri`.
2. **Raw string literals:** JSON fixtures with `}}` runs collide with `$$"""…{{x}}…"""`
   interpolation. Use `$$$"""…{{{x}}}…"""` when the JSON has doubled closing braces.
3. **`JsonElement` lifetime.** Elements are only valid while their `JsonDocument` is alive. If a
   parsed value outlives the document, `.Clone()` it. See §7.

## 7. Bug found and fixed during the port

**`PatchOperation` held `JsonElement`s into a disposed `JsonDocument`.**
`MlbStatsApi.FetchDiffPatchAsync` parses the response under `using var document = …` and returns the
ops; every `PatchOperation.Value` then pointed into freed memory, so all patch *values* were garbage.
Phase 3's diffPatch tests missed it because they asserted only op **paths**, which are extracted to
`string` at parse time. It surfaced in Phase 4 the first time `GameMonitor` applied a patch
end-to-end and a score came back `0` instead of `4`.

Fixed in `PatchOperation.TryParse` (`rawValue.Clone()`), regression test
`MlbStatsApiDiffPatchTests.FetchDiffPatchAsync_OpValuesOutliveTheParsedDocument`.

Live impact had it shipped: polling would silently stop updating scores, counts and batters while
looking healthy.

## 8. Deviations from the Swift original

Each phase plan has its own list; the ones with ongoing consequences:

| Deviation | Why |
|---|---|
| `PlayerPosition` / `RosterStatus` are **namespace-level**, not nested in `Player` | CS0102 forbids a nested type sharing a name with the `RosterStatus` property; Swift allows it only because `rosterStatus` differs by case |
| `TeamMapping.Abbreviation` is deterministic | Swift built its reverse map from a `Dictionary`, whose order is randomized per process, so `Athletics` could resolve to `ATH` **or** `OAK` between launches. C# drives it from an ordered list; `ATH` always wins |
| `LiveFeedData` is a mutable **class** + explicit deep `Clone()`; `LiveFeedPatcher.Apply` returns the patched copy | Swift patches a struct through `inout`. The clone preserves the guarantee that partial state never escapes |
| `UnknownPatchLogger` keeps in-memory entries + `Debug.WriteLine`, no CSV/rotation | Master plan: log target becomes `ILogger`/Debug |
| `LiveFeedData` + `LiveFeedDecoder` landed in **Phase 2**, not Phase 3 | The patcher's anchor test compares patched state against decoder output; weakening it would have lost the field-mapping check |
| `BaseballCalendar` landed in **Phase 3**, not Phase 4 | `FantraxApi`'s period detection needs `baseballDate()` |
| `FantraxAPI.findScorers` **not ported** | Dead code in Swift — only self-referencing; `fetchRoster` uses the direct table walk. It also hardcoded `statusId: 1` |
| `MemoryStats`, `MemoryPressureRelief` **not ported** | macOS-only, per master plan |
| Netflix stream URL gains a trailing slash | .NET's `Uri` normalizes a bare authority; same destination |
| `GameMonitor.TrackGames` added (internal) | Splits state reset from launching the coordinator so the pure scheduling functions are testable without the loop concurrently consuming milestones. `StartMonitoring` = `TrackGames` + loop |

`OnDeck.Core` has `<InternalsVisibleTo Include="OnDeck.Core.Tests" />` for the `internal` seams on
`GameMonitor` (`TrackGames`, `NextEventDelay`, `SelectGamesToPoll`, `PollSingleGameAsync`,
`ProcessFeed`).

## 9. Next up — Phase 5: AppOrchestrator

**Spec:** `onDeck/App/AppState.swift` (logic portions, ~450 lines) and `onDeck/Views/MenuBarView.swift`
(**sorting and filter rules only** — the UI itself is Phase 7).

**Public surface is already fixed** by `PORT_PLAN.md` §"Cross-Phase Interface Contracts" — copy
`AppOrchestrator` and `INotificationSink` from there verbatim, don't redesign. `ISettingsStore`
already exists in Core and matches the contract.

What Phase 5 must cover:

- Section grouping + **In Game sort** (batting band / notBatting band / pitcher ranking) and the
  **Done statLine filter** — derive `PlayerDisplay`'s fields from what `MenuBarView.swift` actually
  consumes; don't invent them.
- Bench filter + `SettingsChanged()` doing a **local rebuild with no network** (the `hideBenchPlayers`
  `didSet` analog).
- **15-min-before-first-game resync** — `AppState.swift:497-520`. Must **not** call resync when games
  have already started: that causes an infinite restart loop cancelling in-flight requests.
- 8AM daily re-sync; 30s resume debounce.
- Team-picker state: `FetchTeamsAsync` / `AvailableTeams` / `IsLoadingTeams` / `TeamsError`.
- `ResyncRosterAsync` returning success (drives the flyout's 4-state Refresh button).
- **Not-in-lineup reconciliation** — `AppState.swift:176, 238-301`: one-shot `notifiedNotInLineup`
  set, don't-notify-once-game-started guard, `onLineupUpdate`/`onGameStart` wiring including
  game-start `PurgeNotInLineupAsync` and `PurgeAllAsync` on every schedule refresh.
- Transition handling calling `INotificationSink` with **`isStillActive` re-check after every await**
  — re-check state after any async notification send and purge if it changed, or stale notifications
  stick. This is the reason for the no-`ConfigureAwait(false)` rule.
- Immutable list snapshots: `IReadOnlyList` replaced wholesale, never mutated in place. The coalesced
  rebuild becomes a dirty-flag + posted continuation on the same context.

Test with `FakeTimeProvider` throughout and a mock `INotificationSink` asserting notify/purge
sequences.

**Likely need:** a single-threaded `SynchronizationContext` test fixture, so the race-guard semantics
are exercised as they run in the WPF `Dispatcher`. Nothing so far has needed one.

## 10. After Phase 5 — the shell (Phases 6–11)

Phases 6–11 are WPF and **need a human at the keyboard** for the manual Win11 verification steps
(light/dark taskbar, 100/150% scaling, multi-monitor, toast click-through with the app dead, Focus
Assist).

**Phase 6 opens with a de-risking spike that must run before any `ToastService` code exists:** publish
a hello-world single-file exe, fire a toast via `ToastNotificationManagerCompat` with launch args,
click it with the app running **and** not running, and confirm `OnActivated` fires with args both
ways. `Microsoft.Toolkit.Uwp.Notifications` is archived/maintenance-mode. If the spike fails, switch
to the Windows App SDK `AppNotificationManager` fallback **then**, not later.

Also unstarted: icon assets. Tabler Icons `ball-baseball` (MIT), recoloured white/dark/green and
rendered to a multi-res `.ico` (16/20/24/32).

## 11. Verification before claiming a phase done

```bash
dotnet test windows/OnDeck.slnx          # expect: Failed: 0
dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj   # expect: 0
git status --short                       # expect: clean
```

Then update the phase plan's Deviations section with anything that diverged from the Swift original,
and append the phase's row to §8 of this file.
