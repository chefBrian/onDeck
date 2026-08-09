# onDeck Windows Port — Handoff

**As of:** 2026-08-08, end of Phase 7b. Branch: `main`.

**State:** `OnDeck.Core` is **complete** and the shell has its **real UI** — tray icon, flyout with
the four player sections and footer, floating panel with a persisted frame, settings on disk, all
driven by the real engine. **598 tests green (509 Core + 89 App)**, working tree clean, single-file
publish verified. The toast spike passed, so Phase 9's stack is settled. Phase 8 is the Settings
window.

**Phase 7b was the first time the shell ran against live data**, and it flushed out two bugs that
no test could have caught (§7b, §8b). The app has been run end to end against a real Fantrax roster
on a live game day and renders correctly. One cosmetic issue is open and parked by owner decision:
the acrylic backdrop (`ACRYLIC-OPEN-ISSUE.md`).

---

## 1. Read these first, in this order

1. `windows/PORT_PLAN.md` — the master decomposition. Phases, porting map, cross-phase interface
   contracts, parity checklist, resolved decisions. **The interface contracts in there are binding**
   — later phases depend on those exact shapes.
2. `CLAUDE.md` (repo root, **gitignored** via `C:/Users/brian/.gitignore_global:13` — it exists
   locally but will not be on a fresh clone). Holds the gotchas the port must preserve.
3. The Swift source for the phase you're on. **The Swift file named in each phase is the
   authoritative spec** — read it before writing anything.
4. `windows/plans/*.md` — the executed phase plans (Phases 0–7b). Each has a "Deviations" section
   recording where the C# port intentionally differs from Swift and why, and 7b also has an
   "Execution notes" section for corrections found while running the plan.
5. `windows/ACRYLIC-OPEN-ISSUE.md` — **only** if you intend to touch the flyout/panel backdrop.
   Two sessions have failed at it; the document exists to stop a third from repeating them.

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

**Kill the app before building or testing.** A running `OnDeck.App.exe` holds a lock on
`OnDeck.Core.dll`, so `OnDeck.App.Tests` fails to build — and `dotnet test` still exits after
reporting **`Passed!` for `OnDeck.Core.Tests` alone**, which reads exactly like a green run. The
`MSB3027`/`MSB3021` copy errors are buried above it. Always check that **two** `Passed!` lines came
back, one per test project:

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx 2>&1 | grep -E "Passed!|Failed!|error MSB"
```

## 4. User preferences (standing)

- **Never append `Co-Authored-By` or any AI-attribution trailer to commits.** Global rule.
- Work is committed **directly to `main`**, no feature branch.
- `CLAUDE.md` says "Don't build or deploy for the user". That is scoped to the **macOS Xcode app**
  (it sits among the `xcodebuild` / `/Applications` commands). Running `dotnet build`/`test` for the
  port is expected — TDD can't verify anything otherwise.
- **Launching the built Windows app locally to verify is allowed; installing it is not.** Run it
  from `bin/Debug/net10.0-windows/`. Phase 7b did this routinely and it is how both live bugs were
  found. Kill it again before the next build (§3).
- **Don't trust automated screen capture for visual checks.** It produced confidently wrong
  conclusions in Phase 7a. Ask the owner to look — that is how the acrylic Refresh clue surfaced.
  A useful trick: launching the exe a second time makes the running instance open the flyout
  (`SingleInstance.SignalExistingInstance`), so you can exercise the render path without clicking.

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
├── src/OnDeck.App/           net10.0-windows WPF
│   ├── App.xaml(.cs)         composition root; palette application; Float wiring
│   ├── SettingsStore.cs      ISettingsStore + the shell-only FloatingPanelFrame
│   ├── Platform/             DwmBackdrop, MonitorWorkArea, ShellLog, SingleInstance,
│   │                         TrayGeometry, ExternalLink
│   ├── Tray/                 TrayIconService, ThemeWatcher, TrayIconVariant
│   ├── Views/                ThemePalette, DisplayFormatting, RowViewModels, FlyoutSections
│   │                         (+ FlyoutInputFactory), RefreshButtonModel, TeamLogoStore,
│   │                         FlyoutContent.xaml, FooterBar.xaml
│   └── Windows/              FlyoutWindow, FloatingPanelWindow, FlyoutPositioner,
│                             FloatingPanelPlacement
├── tests/OnDeck.Core.Tests/  net10.0, xunit v2 + Microsoft.Extensions.TimeProvider.Testing
│   ├── SingleThreadedContext.cs      pumping single-threaded SynchronizationContext fixture
│   ├── RecordingNotificationSink.cs  INotificationSink double with an ordered call log
│   ├── Networking/RoutingHttpMessageHandler.cs   URL-routed HTTP double
│   └── App/OrchestratorHarness.cs    composes managers + routes + orchestrator
└── tests/OnDeck.App.Tests/   net10.0-windows, same stack
    └── StubHttpMessageHandler.cs     its own copy — the two test projects don't reference
                                      each other
```

`Microsoft.Extensions.TimeProvider.Testing` (10.8.0, pinned the same in both test projects) is a
**test-project-only** dependency. `OnDeck.Core` must keep zero package references — verify with
`grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj` (expect 0).

**WPF's implicit usings omit `System.IO`** (it would collide with `System.Windows.Shapes.Path`), so
any file in `OnDeck.App` or `OnDeck.App.Tests` touching `Path`/`File`/`Directory` needs an explicit
`using System.IO;`.

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

### Four traps that already bit (engine)

1. **`Uri.ToString()` unescapes for display.** Assert percent-encoding against `AbsoluteUri`.
2. **Raw string literals:** JSON fixtures with `}}` runs collide with `$$"""…{{x}}…"""`
   interpolation. Use `$$$"""…{{{x}}}…"""` when the JSON has doubled closing braces.
3. **`JsonElement` lifetime.** Elements are only valid while their `JsonDocument` is alive. If a
   parsed value outlives the document, `.Clone()` it. See §7.
4. **`HttpClient` sends no `User-Agent`; `URLSession` always does.** Fantrax's edge answers **403**
   to a request with no UA at all — it doesn't inspect the value, only its presence. Every roster
   sync failed until `FantraxApi` set one. Any platform difference where macOS supplies a default
   that .NET does not is a candidate for this class of bug. See §7b.

### XAML traps from Phase 7b

These are all **silent** — they compile, run, and look merely "a bit off":

1. **An explicit `Style` on a control replaces the Fluent theme's implicit style wholesale.**
   `RowButton` set `Template` but not `Foreground`, so `Foreground` fell back to `Control`'s
   default (`SystemColors.ControlTextBrush`, black) and every run inheriting it — player name,
   both scores, the count — rendered black on the dark flyout. **If you style a control, set
   `Foreground` on the style.**
2. **Segoe UI has no Medium weight.** `FontWeight="Medium"` silently falls back to Regular, so text
   meant to match Swift's SF Pro Medium renders a weight light. Use the `UiFont` resource
   (`Segoe UI Variable Text, Segoe UI`), which has real Medium and SemiBold.
3. **SwiftUI `.caption` is 10pt on macOS, not 11.** `.body` (the default for an unadorned `Text`)
   is 13. Both were wrong in the first cut of the templates. When porting a Swift view, translate
   every `.font()` explicitly rather than letting WPF's default 12 stand in.
4. **`<DataTemplate.Triggers>` must be a direct child of `DataTemplate`**, a sibling of the root
   element — not nested inside it. Nesting gives `MC3015` at build time.
5. **Don't rely on inherited `Foreground` inside a templated control** (see 1). Every text run in
   `FlyoutContent.xaml` sets its own.

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

## 7b. Second bug found by the port — Fantrax 403s a request with no User-Agent

Found in Phase 7b, the first time the shell ran against the **real** Fantrax API with a roster
configured. Every sync failed with `Roster sync failed: Fantrax API returned HTTP 403`.

Isolated with a 2×2 (method × client), because two things differed at once:

| | `getStandings` | `getTeamRosterInfo` |
|---|---|---|
| `HttpClient`, no UA | **403** | **403** |
| `HttpClient`, any UA | 200 | 200 |

So the method was irrelevant and the `User-Agent` was everything. `onDeck/1.0`, `curl/8.0` and a
browser string all work — the value is not inspected, only its presence — so `FantraxApi` sends an
honest `onDeck/1.0` rather than impersonating a browser. MLB's `statsapi` and `midfield` endpoints
are unaffected (200 with or without).

Why Swift never hit it: `URLSession` always sends a default UA
(`onDeck/1.0 CFNetwork/… Darwin/…`); .NET's `HttpClient` sends none unless told to.

Fixed in `FantraxApi.PostRequestAsync`; regression test
`FantraxApiTeamsTests.FetchTeamsAsync_SendsAUserAgent`. The header lives in Core beside the request
it belongs to, not in the composition root, so the test travels with it.

**Live impact had it shipped:** the app would have installed, launched, shown a tray icon and
never loaded a single player.

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
| **Phase 7b:** Settings footer button + tray Settings item deferred to Phase 8 | `TrayIconService`'s own doc comment sets the convention — a button ships with the window it opens. A button that does nothing is worse than one that isn't there |
| **Phase 7b:** no `matchedGeometryEffect` row-reorder animation (`MenuBarView.swift:181`) | WPF has no equivalent primitive; rows are replaced wholesale each rebuild |
| **Phase 7b:** colours come from an app-owned `ThemePalette`, not WPF Fluent's resource keys | A `DynamicResource` naming a key that isn't there resolves to null and renders invisible, with no error at build or run time — the same silent-failure class as the acrylic bug |
| **Phase 7b:** palette is driven by `AppsUseLightTheme`; the tray icon still uses `SystemUsesLightTheme` | They are separate registry values, and "light apps, dark taskbar" is the Windows 11 default pairing |
| **Phase 7b:** floating panel's close/refresh controls fall back to the empty state's header | Swift renders no header when every list is empty, leaving the panel closable only from the Float button. A borderless window with no taskbar entry needs its own close affordance |
| **Phase 7b:** `TeamLogoStore` sits between the rows and Core's `TeamLogoCache` | Rows rebuild every 10 s during a live game; the path lookup must be synchronous and the fetch must de-duplicate, or a missing logo is re-requested on every rebuild |
| **Phase 7b:** rows carry a logo **file path**, not an `ImageSource` | WPF's built-in converter turns a path into an image, which keeps the row records plain data and unit-testable |
| **Phase 7b:** floating-panel frame persisted outside `ISettingsStore` (`SettingsStore.FloatingPanelFrame`) | `PORT_PLAN.md` already scopes it as shell-only; Core has no business knowing a window exists |
| **Phase 7b:** `FloatingPanelPlacement` adds an on-screen check macOS gets for free | `setFrameUsingName` returns false for an unusable frame; Windows has no equivalent, and the panel has no taskbar button to recover it with |
| **Phase 7b:** floating header's refresh shows a static glyph while syncing, not a spinner | Swift uses a 14 pt `ProgressView`. The tick/cross outcome still shows; a second rotation storyboard for a 12 px glyph isn't worth it. The footer's Refresh does spin |
| **Phase 7b:** `#if DEBUG` memory overlay not ported | `MemoryStats` is macOS-only and explicitly out of scope in `PORT_PLAN.md` |

`OnDeck.Core` has `<InternalsVisibleTo Include="OnDeck.Core.Tests" />` for the `internal` seams on
`GameMonitor` (`TrackGames`, `NextEventDelay`, `SelectGamesToPoll`, `PollSingleGameAsync`,
`ProcessFeed`), `DisplayRules` (the whole class), and `AppOrchestrator.TimeUntilNextEightAm`.
`OnDeck.App` gained the same for `OnDeck.App.Tests` in Phase 7b, for `TeamLogoStore.DrainAsync`.

## 8b. Phase 7 status and the one open issue

**Phase 7a (done):** the flyout uses the work area of the monitor holding the tray
(`Platform/MonitorWorkArea.cs`), `TeamLogoCache` is in Core beside `HeadshotCache`, and
`Views/DisplayFormatting.cs` holds the dot / glyph / badge / trailing-text rules from
`MenuBarView.swift`. Plan: `plans/2026-08-08-phase-7a-flyout-foundations.md`.

**Phase 7b (done):** the real content. Five plain-class layers, each unit-tested — `ThemePalette`,
`RowViewModels`, `FlyoutSections`, `RefreshButtonModel`, `TeamLogoStore` — under two XAML views,
`FlyoutContent` (shared verbatim by both windows) and `FooterBar`, plus `FloatingPanelWindow` with
a persisted frame. **XAML holds no logic**: templates bind plain record properties, and there is
not a single `IValueConverter` in the shell. 598 tests green. Plan:
`plans/2026-08-08-phase-7b-flyout-content.md`.

**7b also carried a visual-parity pass after the owner compared it side by side with the Mac app.**
Every fix in it was traced to a specific line of `MenuBarView.swift` rather than to taste — the
type sizes, the Segoe-UI-has-no-Medium fallback, the black `Foreground` inheritance, and the
hardcoded `MaxHeight` that forced scrolling. See the XAML traps in §6; that list is the durable
part.

**Open, cosmetic, now two sessions deep:** the backdrop renders opaque instead of acrylic.
**`windows/ACRYLIC-OPEN-ISSUE.md` was rewritten at the end of 7b** because its central premise was
wrong: instrumentation proved DWM *accepts* the backdrop (`S_OK`) and WPF's composition target is
*already* transparent — so every remedy aimed at "make DWM accept it" was aimed at the wrong layer.
Five more fixes were tried in 7b and all failed; they are listed there so nobody repeats them.

The live clue: **pressing the footer Refresh makes the flyout translucent**, and the floating panel
— whose refresh button is the only one without a spinner animation — never becomes translucent.
That points at an active WPF `Storyboard` changing the presentation path, which is the first thing
to test next. Read that document before touching the backdrop.

**Manual verification results, 2026-08-08** (Windows 11 build 26200, single monitor, bottom
taskbar). Phase 7a rows kept; 7b rows appended:

| Check | Result |
|---|---|
| Tray icon appears; flyout opens anchored to it; light-dismiss; context menu | **Pass** |
| Icon swaps white↔dark on a live Windows theme change, no restart | **Pass** — `ThemeWatcher`'s change path is now exercised |
| Second launch adds no second tray icon (exits code 0, one instance remains) | **Pass** |
| Acrylic backdrop | **Fail** — see `ACRYLIC-OPEN-ISSUE.md` |
| Second monitor | **Not testable** — no second display on this machine. The code fix is in but unverified |
| Display scaling 100/125/150/200%; docked taskbar edges; Quit from the menu | **Not run** |
| **7b:** flyout renders the real sections against a live roster | **Pass** — ACTIVE NOW / IN GAME / UPCOMING / DONE, stat lines, score + logos, bases, count, outs, footer |
| **7b:** base diamond turns green when the row's own player is the runner | **Pass** — seen on a live game |
| **7b:** type matches `MenuBarView.swift` (name 13 Medium, captions 10, bases 14pt) | **Pass** after the Segoe UI Variable fix |
| **7b:** name / scores / count render light on the dark surface | **Pass** after the `RowButton` `Foreground` fix |
| **7b:** flyout grows to the monitor rather than scrolling at a fixed height | **Pass** |
| **7b:** acrylic backdrop | **Fail, unresolved** — opaque until Refresh is pressed, then translucent; the panel is always opaque. See `ACRYLIC-OPEN-ISSUE.md` |
| **7b:** floating panel opens, drags, persists its frame, auto-opens | **Not run** |

**To point the app at a roster before Phase 8 exists**, hand-write
`%APPDATA%\onDeck\settings.json` — there is no Settings UI yet:

```json
{ "rosterUrl": "https://www.fantrax.com/fantasy/league/<leagueId>/home",
  "selectedTeamId": "<teamId>", "hideBenchPlayers": false }
```

A league `/home` URL carries no teamId, so `selectedTeamId` is required. Enumerate the league's
teams the way `FetchTeamsAsync` does — POST `getStandings` to
`https://www.fantrax.com/fxpa/req?leagueId=<leagueId>` and collect every `{teamId, content}` pair.
**Send a User-Agent** or it 403s (§7b).

## 9. Next up — Phase 8: the Settings window

*(Windows PC. Phases 8–11 need a human at the keyboard for the manual Win11 checks.)*

**Spec:** `onDeck/Views/SettingsView.swift`. Write the plan first, per the workflow in §2.

Build `SettingsWindow` over the existing `SettingsStore`: roster URL field (fetch teams on submit
via `FetchTeamsAsync`), team picker with `IsLoadingTeams` / `TeamsError` states, sync status +
Sync Now (disabled while `IsSyncing` or with no team), display toggles (hide bench, always-open
popout), the five notification toggles, GitHub links. Call `SettingsChanged()` after any write —
it re-filters every section locally with no network.

**Phase 7b deliberately deferred two things to this phase**, because a button that opens nothing is
worse than one that isn't there yet:

1. The **Settings footer button** in `Views/FooterBar.xaml` — first in the row, gear glyph
   ``, matching the other `FooterButton`-styled buttons; raise a `SettingsRequested` event
   like `FantraxRequested`.
2. The **Settings item in the tray context menu** (`Tray/TrayIconService.cs`), between Float and
   Refresh, mirroring the existing `FloatRequested` wiring.

**What Phase 8 can reuse rather than reinvent:**

- `ThemePalette` publishes the `OnDeck.*` brushes app-wide; bind `{DynamicResource}` to those and
  read the XAML traps in §6 before styling any control.
- `RefreshButtonModel` already implements idle→spinning→done/failed→idle over
  `ResyncRosterAsync`; Sync Now is the same machine with a different presentation.
- `AppOrchestrator` already exposes everything the window needs: `AvailableTeams`,
  `IsLoadingTeams`, `TeamsError`, `IsSyncing`, `LastSyncDate`, `SyncError`, `LoadedPlayerCount`,
  `ParsedLeagueId`, `UrlHasTeamId`, `EffectiveTeamId`, and `FetchTeamsAsync`. Don't add to Core.
- Keep the testable logic in a plain class with tests in `OnDeck.App.Tests` and let the XAML bind
  to it — that split is what made 7b's rules verifiable, and the untested XAML is exactly where
  7b's visual bugs lived.

**Unlike Phase 7b, Phase 8 is fully self-verifiable**: the settings window is reachable and
exercisable without a live game. The manual checks that need a human are the visual ones.

**Correction to `PORT_PLAN.md`:** its Phase 7 row says the row control shows a *headshot*. It does
not — `MenuBarView.swift` renders team logos in the score block and no player headshots at all, and
that is what shipped. `HeadshotCache` exists for notification images only. The parity-checklist line
about headshots in the flyout and floating panel is wrong.

### Phase 6 debts — status after Phase 7

1. **Multi-monitor placement.** *Fixed in 7a* (`Platform/MonitorWorkArea.ForDevicePoint`), still
   unverified — no second display on this machine.
2. **`ThemeWatcher`'s change path.** *Verified in 7a.* 7b added `AppsUseLightTheme` alongside it,
   which drives the flyout palette and rides the same change event.
3. **Acrylic vs solid fallback.** *Answered:* the attribute returns `S_OK` and the surface is opaque
   anyway, so the fallback never fires. Still open — `ACRYLIC-OPEN-ISSUE.md`.
4. **Double-launch.** *Verified in 7b* — second launch exits 0 and one instance remains.
5. Still not run: display scaling 100/125/150/200%, docked taskbar edges, Quit leaving no process.

### Outstanding QA carried into Phase 8

Built but never exercised by a human. None of it blocks Phase 8; roll it into the next manual pass:

- **Floating panel behaviour end to end** — opens from Float (footer *and* tray), stays on top,
  does not steal focus, drags by background, remembers its frame across a restart, auto-opens when
  `alwaysOpenPopout` is true. Only its opaque backdrop has been observed.
- **UPCOMING and DONE sections against live data** — seen briefly and looked right, but the badge
  states (red dot / green tick / batting-order number) and the PPD label have not been checked
  against a game that is actually postponed or has a filed lineup card.
- **Stream-link click** → opens the right service per `StreamLinkRouter`.
- **Hover states, and the flyout at 125/150/200% scaling.**
- Display scaling, docked taskbar edges, second monitor, Quit leaving no process (from Phase 6).

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

## 10. After Phase 8 — Phases 9–11

Notifications (9), system integration & ship (10), parity QA (11). See `PORT_PLAN.md` for each
phase's scope and the parity checklist.

**Phase 9 starts by bumping `OnDeck.App` to `net10.0-windows10.0.17763.0`** — the toast compat APIs
are not exposed on the bare `net10.0-windows` the app targets today. The spike project already does
this; copy its csproj settings.

## 11. Verification before claiming a phase done

```bash
# Kill the app first - a running instance locks OnDeck.Core.dll and OnDeck.App.Tests
# then silently fails to build while the run still prints Passed! for Core alone (§3).
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"

dotnet test windows/OnDeck.slnx          # expect: TWO "Passed!" lines, Failed: 0 on both
dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj   # expect: 0
git status --short                       # expect: clean
```

Then update the phase plan's Deviations section with anything that diverged from the Swift original,
and append the phase's row to §8 of this file.

**Run the app and look at it before calling a UI phase done.** Phase 7b passed every automated gate
above while the flyout still had black-on-black text, a weight-too-light font and premature
scrolling — none of which any test could see. The automated gates prove the engine; only a human
proves the shell.
