# onDeck Windows Port — Handoff

**As of:** 2026-08-08, end of Phase 6. Branch: `main`.

**State:** `OnDeck.Core` is **complete** and the shell **runs** — tray icon, light-dismissing
flyout, context menu, settings on disk, all driven by the real engine. 522 tests green (500 Core +
22 App), working tree clean, single-file publish verified. The toast spike passed, so Phase 9's
stack is settled. Phase 7 is the flyout's real content.

---

## 1. Read these first, in this order

1. `windows/PORT_PLAN.md` — the master decomposition. Phases, porting map, cross-phase interface
   contracts, parity checklist, resolved decisions. **The interface contracts in there are binding**
   — later phases depend on those exact shapes.
2. `CLAUDE.md` (repo root, **gitignored** via `C:/Users/brian/.gitignore_global:13` — it exists
   locally but will not be on a fresh clone). Holds the gotchas the port must preserve.
3. The Swift source for the phase you're on. **The Swift file named in each phase is the
   authoritative spec** — read it before writing anything.
4. `windows/plans/*.md` — the five executed phase plans (Phases 0–5). Each has a "Deviations"
   section recording where the C# port intentionally differs from Swift and why.

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
│   ├── ISettingsStore.cs, INotificationSink.cs, AppOrchestrator.cs, DisplayRules.cs
│   ├── Models/               Player, PlayerState, Game+GameLineup, LiveFeedData+stats,
│   │                         PlayerDisplay (+BattingProximity, LineupInfo, DelayIndicator)
│   ├── Networking/           MlbStatsApi, FantraxApi, FantraxModels, LiveFeedDecoder, DiffPatchResult
│   ├── Utilities/            LiveFeedPatcher, PatchOperation, UnknownPatchLogger, TeamMapping,
│   │                         NameCleaner, FantraxUrlParser, StreamLinkRouter, HeadshotCache,
│   │                         BaseballCalendar
│   └── Managers/             RosterManager, ScheduleManager, StateManager, GameMonitor
├── src/OnDeck.App/           net10.0-windows WPF — still the bare template
└── tests/OnDeck.Core.Tests/  net10.0, xunit v2 + Microsoft.Extensions.TimeProvider.Testing
    ├── SingleThreadedContext.cs      pumping single-threaded SynchronizationContext fixture
    ├── RecordingNotificationSink.cs  INotificationSink double with an ordered call log
    ├── Networking/RoutingHttpMessageHandler.cs   URL-routed HTTP double
    └── App/OrchestratorHarness.cs    composes managers + routes + orchestrator
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
| **Phase 5:** `AppOrchestrator.ActivePlayers` added to the contract | `MenuBarView.swift` renders an "Active Now" section; `PORT_PLAN.md` listed only the other three lists. Additive — `HasActivePlayers` derives from it |
| **Phase 5:** `ParsedLeagueId` / `UrlHasTeamId` / `EffectiveTeamId` are public | `SettingsView.swift` and the flyout footer read all three off `AppState`; Phases 7–8 need them |
| **Phase 5:** `PlayerDisplay` carries the whole `LiveFeedData` | `LivePlayerRow` reads a dozen feed fields (score, bases, count, outs, inning, half); passing the snapshot beats inventing a parallel projection |
| **Phase 5:** `BattingProximity` / `LineupInfo` are `Kind` + `Value` structs, not record hierarchies | A nested case type named `OnDeck` inside namespace `OnDeck.Core` makes the bare identifier ambiguous at type-name position |
| **Phase 5:** `DelayIndicator` replaces `delayIcon`'s SF Symbol names | Core classifies; the shell picks the icon |
| **Phase 5:** extra `UpdatePlayerLists()` after schedule-lineup seeding | Swift builds lists *before* seeding and gets away with it because `@Observable` views re-read `GameMonitor` at render. Our rows are snapshots, so without it every UPCOMING row shows `LineupInfo.Unknown` until the next state change |
| **Phase 5:** notification work runs through `RunGuarded` (catch + `Debug.WriteLine`) | The sink is shell-implemented and the toast API can throw; a failed notification must not tear down the transition pipeline |
| **Phase 5:** `requestPermission()`, `MemoryPressureRelief`, `FloatingPanel` auto-open not ported into Core | Shell concerns (Phases 7/9) or macOS-only |

| **Phase 6:** WPF-UI dropped for .NET 10's native Fluent theme + `DwmSetWindowAttribute` | The framework now covers what WPF-UI was chosen for; one less dependency and a smaller exe. User-confirmed |
| **Phase 6:** `App/System/` is `App/Platform/` | A namespace named `System` nested under `OnDeck.App` shadows the global `System` inside WPF's generated `App.g.cs`. The layout in `PORT_PLAN.md` does not compile as written |
| **Phase 6:** flyout anchors on the cursor, not `Shell_NotifyIconGetRect` | That call needs the hwnd and icon id Hardcodet keeps private. The cursor is over the icon whenever it's clicked; device pixels are converted to DIPs explicitly so scaling stays right |
| **Phase 6:** `SettingsStore` landed here, not Phase 8 | The composition root cannot build `AppOrchestrator` without an `ISettingsStore`. Phase 8 is now purely the settings UI |
| **Phase 6:** tray icons drop Tabler's stitch strokes at all sizes | Cluttered even at 128 px, mud at 16. Circle + seams reads as a baseball everywhere we render it |

`OnDeck.Core` has `<InternalsVisibleTo Include="OnDeck.Core.Tests" />` for the `internal` seams on
`GameMonitor` (`TrackGames`, `NextEventDelay`, `SelectGamesToPoll`, `PollSingleGameAsync`,
`ProcessFeed`), `DisplayRules` (the whole class), and `AppOrchestrator.TimeUntilNextEightAm`.

## 8b. Phase 7a status (done) and the one open issue

**Phase 7a is complete and committed:** the flyout now uses the work area of the monitor holding
the tray (`Platform/MonitorWorkArea.cs`), `TeamLogoCache` is in Core beside `HeadshotCache`, and
`Views/DisplayFormatting.cs` holds the dot / glyph / badge / trailing-text rules from
`MenuBarView.swift`. 543 tests green. Plan: `plans/2026-08-08-phase-7a-flyout-foundations.md`.

**Open, cosmetic, do not let it block 7b:** the flyout backdrop renders opaque instead of acrylic.
Full write-up, everything already tried, and a warning about the unreliable screen-capture
verification method: **`windows/ACRYLIC-OPEN-ISSUE.md`**. Read it before touching the backdrop —
one plausible-looking fix (removing `ThemeMode="System"`) was tried, wrongly believed to work, and
reverted.

**Manual verification results, 2026-08-08** (Windows 11 build 26200, single monitor, bottom
taskbar):

| Check | Result |
|---|---|
| Tray icon appears; flyout opens anchored to it; light-dismiss; context menu | **Pass** |
| Icon swaps white↔dark on a live Windows theme change, no restart | **Pass** — `ThemeWatcher`'s change path is now exercised |
| Second launch adds no second tray icon (exits code 0, one instance remains) | **Pass** |
| Acrylic backdrop | **Fail** — see `ACRYLIC-OPEN-ISSUE.md` |
| Second monitor | **Not testable** — no second display on this machine. The code fix is in but unverified |
| Display scaling 100/125/150/200%; docked taskbar edges; Quit from the menu | **Not run** |

## 9. Next up — Phase 7b: the flyout's real content

*(Windows PC. Phases 7–11 need a human at the keyboard for the manual Win11 checks.)*

**Spec:** `onDeck/Views/MenuBarView.swift`. The sorting and filter rules are already ported and
tested in Core (`DisplayRules`, `AppOrchestrator`) — Phase 7 is presentation only. Every row field
the Swift view reads is already resolved onto `PlayerDisplay`; do not recompute any of it in XAML.
`Views/DisplayFormatting.cs` (Phase 7a) already maps those fields to dots, glyphs and badges.

**Write a 7b plan first**, per the workflow in §2. Phase 7 was split because one plan covering both
halves would have padded the XAML half with vague instructions; 7a was the testable foundations
and is done.

**Correction to `PORT_PLAN.md`:** its Phase 7 row says the row control shows a *headshot*. It does
not — `MenuBarView.swift` renders team logos in the score block and no player headshots at all.
`HeadshotCache` exists for notification images only. The parity-checklist line about headshots in
the flyout and floating panel is wrong.

Build: `PlayerRow` control (headshot, name, state dot, `StatLine`), UPCOMING / IN GAME / DONE
sections, PPD label, rain/delay icon from `PlayerDisplay.Delay` + tooltip, stream-link click →
`Process.Start`, not-in-lineup flag (hitters only), footer buttons (Settings, Fantrax, Refresh with
idle/spinning/done/failed off `ResyncRosterAsync`'s bool, Float, Quit). Then `FloatingPanelWindow`:
always-on-top no-activate borderless window reusing the same section views, drag-by-background,
frame saved to settings, toggled by Float, auto-opened at launch when `AlwaysOpenPopout`.

**Replace the placeholder** in `FlyoutWindow.xaml` (a one-line counts summary) with the real
sections, and add Float + Settings to the tray context menu once their windows exist.

### Phase 6 debts to clear in Phase 7

1. **Multi-monitor placement is wrong.** `FlyoutWindow.ShowAt` uses `SystemParameters.WorkArea`,
   which is always the *primary* monitor's. Use the work area of the monitor containing the anchor.
2. **`ThemeWatcher`'s change path has never run.** Verify the tray icon swaps white↔dark when the
   Windows colour mode changes with the app running.
3. **Unknown whether acrylic or the solid fallback fired.** If the flyout is a flat `#202020`
   rectangle, `DwmSetWindowAttribute` refused it on this build — record the build number.
4. The rest of the Task 7 matrix in `plans/2026-08-08-phase-6-shell-skeleton.md`: display scaling,
   docked taskbar edges, double-launch, Quit leaving no process.

### What the shell must honour when it wires up Core

- **Construct `AppOrchestrator` on the Dispatcher thread.** The constructor captures
  `SynchronizationContext.Current` and posts the coalesced list rebuild to it. Building it on a pool
  thread silently drops the single-thread guarantee the race guards depend on.
- `StateChanged` fires on that same context, so binding can update directly — no `Dispatcher.Invoke`
  needed if Core was constructed correctly.
- The four list properties are immutable snapshots replaced wholesale. Bind to the property, don't
  hold a reference to the list.
- `SettingsChanged()` after any `ISettingsStore` write; it re-reads settings and rebuilds locally
  with no network.
- `ToastService` (Phase 9) implements `INotificationSink` and checks the per-type toggles itself —
  Core calls the sink unconditionally.

### Test infrastructure available to later phases

- `SingleThreadedContext.Run(async () => …)` — pumping single-threaded context; `Settle()` yields
  32 times so queued continuations drain before assertions.
- `RoutingHttpMessageHandler` — routes canned responses by URL substring. Use this rather than
  `StubHttpMessageHandler` whenever concurrent requests are in play.
- `OrchestratorHarness` — declares players/games once and derives the Fantrax, MLB-search, schedule
  and cached-roster payloads from them; `Run`/`RunStarted` wrap the context and clean up.

## 10. After Phase 7 — Phases 8–11

Settings window (8), Notifications (9), system integration & ship (10), parity QA (11). See
`PORT_PLAN.md` for each phase's scope and the parity checklist.

**Phase 9 starts by bumping `OnDeck.App` to `net10.0-windows10.0.17763.0`** — the toast compat APIs
are not exposed on the bare `net10.0-windows` the app targets today. The spike project already does
this; copy its csproj settings.

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
