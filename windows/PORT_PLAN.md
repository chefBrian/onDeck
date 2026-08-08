# onDeck Windows Port - Master Plan

> **For agentic workers:** This is the master decomposition plan. Each phase below must be
> expanded into a detailed TDD task plan (superpowers:writing-plans) at execution time, then
> executed via superpowers:subagent-driven-development or superpowers:executing-plans.
> The Swift source file named in each phase is the authoritative spec for that phase -
> read it before writing the phase plan.

**Goal:** Feature-parity Windows port of the onDeck menu bar app as a WPF system-tray app, shipped as a single self-contained .exe.

**Architecture:** Two-project split. `OnDeck.Core` (cross-platform net10.0 class library) holds the entire engine - models, MLB/Fantrax API clients, JSON-patch polling, state machine - ported 1:1 from the Swift `Managers/`, `Models/`, `Networking/`, `Utilities/` layers, testable on macOS and Windows. `OnDeck.App` (net10.0-windows, WPF) is the Windows shell: tray icon, flyout window, floating panel, toasts, system-event handling.

**Tech Stack:**
- .NET 10 (current LTS), C# with `Nullable` enabled
- WPF + [WPF-UI (lepo.co)](https://github.com/lepoco/wpfui) for Fluent styling/acrylic
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) for the tray icon
- `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 (`ToastNotificationManagerCompat`) for toasts - works in unpackaged single-file apps with no extra runtime. Package is archived/maintenance-mode, so a Phase 6 spike de-risked it: **spike PASSED 2026-08-08** (hot and cold activation, arguments intact - see `spikes/ToastActivationSpike/FINDINGS.md`). The Windows App SDK `AppNotificationManager` fallback is **not** needed. Requires `net10.0-windows10.0.17763.0` on `OnDeck.App`.
- xunit for tests; `System.TimeProvider` + `Microsoft.Extensions.TimeProvider.Testing` for schedule testing; injected `HttpMessageHandler` for network testing

## Global Constraints

- `OnDeck.Core` must have **zero** Windows-specific dependencies - it builds and tests on macOS.
- **Core concurrency model: single logical thread, mirroring Swift's `@MainActor`.** The Swift engine (`AppState`, `GameMonitor`, `StateManager`) is `@MainActor`-serialized; the race guard (`isStillActive` re-check after each await) and the coalesced list rebuild (dirty-flag + next-tick task) are only correct under single-threaded serialization. The port preserves this: all Core entry points, callbacks, and state mutations run on one `SynchronizationContext` - the WPF `Dispatcher` in the app, a single-threaded test context in tests. No `ConfigureAwait(false)` anywhere in Core. The coalesced rebuild becomes a dirty-flag + posted continuation on the same context. `AppOrchestrator` list properties are published as immutable snapshots (`IReadOnlyList` replaced wholesale, never mutated in place).
- Single-file publish must stay green throughout:
  `dotnet publish src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`
- No `PublishTrimmed` - WPF does not support trimming.
- `EnableWindowsTargeting=true` in `OnDeck.App` so the shell compile-checks on macOS.
- Mirror Swift names 1:1 where possible (`GameMonitor`, `LiveFeedPatcher`, `RosterManager`...) so the two codebases stay cross-referenceable.
- All gotchas in the repo root `CLAUDE.md` apply (diffPatch dict-fallback, Fantrax `period` semantics, `statusId` meanings, stable notification IDs, purge-after-await pattern).
- macOS-only diagnostics (`MemoryStats`, `MemoryPressureRelief`) are **not ported**.

---

## Solution Layout

```
windows/
├── OnDeck.sln
├── Directory.Build.props          # net10.0, Nullable, ImplicitUsings, LangVersion
├── .gitignore                     # bin/, obj/, publish/
├── PORT_PLAN.md                   # this file
├── src/
│   ├── OnDeck.Core/
│   │   ├── Models/                # Player.cs, Game.cs, PlayerState.cs
│   │   ├── Networking/            # MlbStatsApi.cs, FantraxApi.cs
│   │   ├── Managers/              # RosterManager.cs, ScheduleManager.cs,
│   │   │                          #   GameMonitor.cs, StateManager.cs
│   │   ├── Utilities/             # LiveFeedPatcher.cs, TeamMapping.cs, NameCleaner.cs,
│   │   │                          #   FantraxUrlParser.cs, StreamLinkRouter.cs, HeadshotCache.cs
│   │   └── AppOrchestrator.cs     # portable logic extracted from AppState.swift
│   └── OnDeck.App/
│       ├── App.xaml / App.xaml.cs # startup, single-instance guard (named mutex)
│       ├── Tray/                  # TrayIconService.cs, ThemeWatcher.cs
│       ├── Windows/               # FlyoutWindow.xaml, FloatingPanelWindow.xaml, SettingsWindow.xaml
│       ├── Views/                 # PlayerRow, section list controls (shared by flyout + floating panel)
│       ├── Notifications/         # ToastService.cs
│       ├── System/                # SystemEventsWatcher.cs, StartupManager.cs
│       ├── SettingsStore.cs       # JSON at %APPDATA%\onDeck\settings.json
│       └── Assets/                # baseball .ico variants (see Decisions: Tabler ball-baseball)
└── tests/
    └── OnDeck.Core.Tests/
        ├── Fixtures/              # translated from LiveFeedPatcherFixtures.swift
        └── *.cs
```

## Porting Map

| Swift source (spec) | Lines | C# target | Nature |
|---|---|---|---|
| `Models/Player.swift`, `Game.swift`, `PlayerState.swift` | 122 | `Core/Models/` records + enum (mirror cases 1:1) | Mechanical |
| `Utilities/TeamMapping.swift`, `NameCleaner.swift`, `FantraxURLParser.swift`, `StreamLinkRouter.swift` | 171 | `Core/Utilities/` static classes | Mechanical |
| `Utilities/LiveFeedPatcher.swift` + tests + fixtures | 1,009 | `Core/Utilities/LiveFeedPatcher.cs` + xunit tests | Mechanical; keep custom dict-fallback handling, do NOT swap for a generic RFC 6902 library |
| `Utilities/UnknownPatchLogger.swift` | 108 | `Core/Utilities/UnknownPatchLogger.cs` | Mechanical; log target becomes `ILogger`/Debug |
| `Networking/MLBStatsAPI.swift` | 553 | `Core/Networking/MlbStatsApi.cs` (`HttpClient` + `System.Text.Json`) | Mechanical; keep `hydrate=lineups`, timecode formats |
| `Networking/FantraxAPI.swift` | 197 | `Core/Networking/FantraxApi.cs` | Mechanical; keep `period` param behavior; includes `FetchTeams` for the team picker |
| `Managers/RosterManager.swift` | 146 | `Core/Managers/RosterManager.cs` | Mechanical; UserDefaults roster-cache blob → `ISettingsStore.RosterCacheJson`; exposes `IsSyncing`, `LastSyncDate`, `Error`, `Players` (consumed by Settings UI) |
| `Managers/ScheduleManager.swift`, `StateManager.swift` | 76 | `Core/Managers/` | Mechanical |
| `Managers/GameMonitor.swift` | 490 | `Core/Managers/GameMonitor.cs` | Near-mechanical; Task + CancellationToken loop on the Core context, TimeProvider for sleeps; owns the 2h/1h/30m pre-game milestone one-shots (`preGameMilestones`, GameMonitor.swift:24-28); preserve seed-after-start rule; exposes `lastPlayDescriptions` (feeds result notifications) |
| `Utilities/HeadshotCache.swift` | 43 | `Core/Utilities/HeadshotCache.cs` | Rewrite small; NSImage → raw file cache, WPF/toasts load from path |
| `App/AppState.swift` (logic portions) | ~450 | `Core/AppOrchestrator.cs` | Redesign-in-shape: 15-min-before-first-game resync (AppState.swift:497-520, incl. the no-resync-after-start infinite-restart gotcha), 8AM daily re-sync, 30s resume debounce, team-picker state, transition handling with `isStillActive` race guards |
| `App/AppState.swift` (platform portions) + `OnDeckApp.swift` | ~170 | `App/App.xaml.cs`, `System/SystemEventsWatcher.cs` | Rewrite: NSWorkspace wake/unlock → `SystemEvents.PowerModeChanged` + `SessionSwitch` |
| `Notifications/NotificationManager.swift` | 201 | `App/Notifications/ToastService.cs` (implements `INotificationSink`) | Rewrite against toast API. Five notification types: batting/pitching/notInLineup (stable tag = `batting-<gamePk>-<playerID>` etc., purged on transition) AND atBatResult/pitchingResult (no stable ID, 30s auto-dismiss → toast `ExpirationTime`). notInLineup toasts also set `Group = "notInLineup-<gamePk>"` so `RemoveGroup` replicates the Mac ID-prefix purge (`History.Remove(tag)` is exact-match). Click opens the toast's `clickURL` via launch args (stream link / Fantrax page), NOT the flyout. ToastService checks the per-type enable toggles from `ISettingsStore` before sending (mirrors Mac, where NotificationManager reads the toggles). Windows auto-removes clicked toasts (macOS workaround not needed). |
| `Views/MenuBarView.swift` (list UI) | ~900 | `App/Windows/FlyoutWindow` + `App/Views/` | Rewrite: same sections/sorting/filters, Fluent visuals; footer buttons incl. 4-state Refresh (idle/spinning/done/failed off `ResyncRosterAsync` result) |
| `Views/MenuBarView.swift` (`FloatingPanel`, :1002-1072) | ~70 | `App/Windows/FloatingPanelWindow` | Rewrite: pinnable always-on-top borderless window reusing the flyout content views; `Topmost`, no-activate, drag-by-background, frame persisted in settings (autosave equivalent), toggled by Float button, auto-opened at launch when `AlwaysOpenPopout` |
| `Views/SettingsView.swift` | 130 | `App/Windows/SettingsWindow` | Rewrite small: roster URL + team picker (loading/error states), sync status + Sync Now, display toggles, five notification toggles, links |
| (none - menu bar label) | - | `App/Tray/TrayIconService.cs` | New: white/dark/green .ico swap; `ThemeWatcher` reads `SystemUsesLightTheme`, listens for `WM_SETTINGCHANGE`; multi-res ico for DPI; tooltip shows active player names (from `MenuBarTitleText`); right-click context menu (Open, Float, Settings, Refresh, Quit) |
| `Utilities/MemoryStats*.swift`, `MemoryPressureRelief.swift` | 210 | - | Not ported |

## Cross-Phase Interface Contracts

Later phases depend on these exact shapes; define them in the phase that creates them and do not rename:

```csharp
// Core - consumed by the shell (Phase 6+)
public sealed class AppOrchestrator
{
    // Player lists - immutable snapshots, replaced wholesale on the Core context
    public IReadOnlyList<PlayerDisplay> UpcomingPlayers { get; }
    public IReadOnlyList<PlayerDisplay> InGamePlayers { get; }   // pre-sorted per MenuBarView rules
    public IReadOnlyList<PlayerDisplay> DonePlayers { get; }     // statLine-filtered
    public bool HasActivePlayers { get; }                        // drives green tray icon
    public string MenuBarTitleText { get; }                      // "A | B | C +2" - tray tooltip

    // Sync / refresh state (Settings window + flyout Refresh button)
    public bool IsSyncing { get; }
    public DateTimeOffset? LastSyncDate { get; }
    public string? SyncError { get; }
    public int LoadedPlayerCount { get; }

    // Team picker state (Settings window)
    public IReadOnlyList<FantraxTeam> AvailableTeams { get; }
    public bool IsLoadingTeams { get; }
    public string? TeamsError { get; }

    public event Action? StateChanged;                           // fired on the Core context

    public Task StartAsync(CancellationToken ct);
    public Task<bool> ResyncRosterAsync();                       // false on failure (drives Refresh button states)
    public Task FetchTeamsAsync();
    public Task HandleSystemResumeAsync();                       // 30s debounce inside
    public void SettingsChanged();                               // re-read ISettingsStore, rebuild lists locally
                                                                 // (hideBenchPlayers didSet analog - no network)
}

// Core - implemented by the shell (ToastService), injected into Core.
// Mirrors NotificationManager.swift 1:1: Core calls it directly from its
// transition/reconcile logic, exactly as AppState does on Mac, so the
// isStillActive race-guard purges stay inside Core. Implementations check
// the per-type ISettingsStore toggles (as Mac's NotificationManager does)
// and no-op when disabled.
public interface INotificationSink
{
    Task NotifyBattingAsync(string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl);
    Task NotifyPitchingAsync(string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl);
    Task NotifyAtBatResultAsync(string playerName, int playerId, string description, Uri? streamUrl);
    Task NotifyPitchingResultAsync(string playerName, int playerId, string description, Uri? streamUrl);
    Task NotifyNotInLineupAsync(string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl);
    void PurgeBatting(int gamePk, int playerId);
    void PurgePitching(int gamePk, int playerId);
    Task PurgeNotInLineupAsync(int gamePk);   // game-scoped: players never in the lineup have no transition to hang this on
    Task PurgeAllAsync();                     // schedule refresh / day rollover
}

// Core - implemented by the shell, injected into Core.
// Complete persisted surface (Swift keys in comments):
public interface ISettingsStore
{
    string? RosterUrl { get; set; }            // rosterURL
    string? SelectedTeamId { get; set; }       // selectedTeamID
    bool HideBenchPlayers { get; set; }        // hideBenchPlayers
    bool AlwaysOpenPopout { get; set; }        // alwaysOpenPopout
    bool NotifyBatting { get; set; }           // notifyBatting, default true
    bool NotifyPitching { get; set; }          // notifyPitching, default true
    bool NotifyAtBatResult { get; set; }       // notifyAtBatResult, default true
    bool NotifyPitchingResult { get; set; }    // notifyPitchingResult, default true
    bool NotifyNotInLineup { get; set; }       // notifyNotInLineup, default true
    string? RosterCacheJson { get; set; }      // RosterManager cache blob
}
// Shell-only (not in the interface Core sees): floating-panel frame, launch-at-login.
```

`PlayerDisplay` is defined in the Phase 5 plan from what `MenuBarView.swift` actually consumes - derive fields from the Swift source, don't invent.

---

## Phases

Each phase = one detailed plan, written at execution time with full TDD steps. Each ends with all tests green and a commit.

**Phase 0 - Scaffold.** Create `windows/` solution per layout above: `dotnet new` projects, `Directory.Build.props`, `windows/.gitignore` (`bin/`, `obj/`, `publish/`), xunit test project referencing Core. Verify: `dotnet build` and `dotnet test` pass (on either OS); `dotnet publish` single-file recipe produces an exe.

**Phase 1 - Models & small utilities.** Spec: `Models/*.swift`, `TeamMapping.swift`, `NameCleaner.swift`, `FantraxURLParser.swift`, `StreamLinkRouter.swift`. Port with tests for: ATH/OAK → Athletics, name-suffix stripping (-P/-H/-DH), period stripping, Fantrax URL parse (leagueID/teamID), every StreamLinkRouter callSign route.

**Phase 2 - LiveFeedPatcher.** Spec: `LiveFeedPatcher.swift` + `LiveFeedPatcherTests.swift` + `LiveFeedPatcherFixtures.swift`. Translate fixtures first, then tests, then implementation until green. Must preserve: RFC 6902 ops, dict-instead-of-array full-feed fallback, UnknownPatchLogger behavior.

**Phase 3 - API clients.** Spec: `MLBStatsAPI.swift`, `FantraxAPI.swift`. DTOs via `System.Text.Json` source generation. Tests with canned JSON via injected `HttpMessageHandler`: schedule+lineups parse, feed/live parse, diffPatch request URL formation (`startTimecode`/`endTimecode` from `metaData.timeStamp` `YYYYMMDD_HHMMSS` UTC), Fantrax roster parse incl. `period` and `statusId` mapping, `fetchTeams` parse.

**Phase 4 - Managers.** Spec: `RosterManager.swift`, `ScheduleManager.swift`, `StateManager.swift`, `GameMonitor.swift`. TimeProvider-driven tests: 10s poll cadence, diffPatch → full-feed fallback on error, phase-transition handling, seed-after-startMonitoring rule, **2h/1h/30m pre-game milestone one-shots with per-game completion tracking** (GameMonitor.swift:24-28), `lastPlayDescriptions` capture, roster cache round-trip through `ISettingsStore.RosterCacheJson`.

**Phase 5 - AppOrchestrator.** Spec: `AppState.swift` (logic portions), `MenuBarView.swift` (sorting/filter rules only). Extract: section grouping + In Game sort (batting band / notBatting band / pitcher ranking), Done statLine filter, bench filter + `SettingsChanged()` local rebuild, **15-min-before-first-game resync** (skip if already started - the infinite-restart gotcha), 8AM daily re-sync, resume debounce, team-picker state (`FetchTeamsAsync`/`AvailableTeams`/`IsLoadingTeams`/`TeamsError`), `ResyncRosterAsync` success result, transition handling calling `INotificationSink` with `isStillActive` re-check semantics under the single-context concurrency model, **not-in-lineup reconciliation** (one-shot `notifiedNotInLineup` set, don't-notify-once-game-started guard, `onLineupUpdate`/`onGameStart` wiring incl. game-start `PurgeNotInLineupAsync` and `PurgeAllAsync` on every schedule refresh - AppState.swift:176, 238-301). Heaviest phase; TimeProvider tests throughout (mock `INotificationSink` to assert notify/purge sequences).

**Phase 6 - Shell skeleton + platform spikes.** *(Windows PC from here on.)*
- ~~**Entry spike (do first): toast activation on unpackaged single-file exe.**~~ **DONE, PASSED** (2026-08-08). Hot activation fired in-process; cold activation cold-started the exe with `-ToastActivated -Embedding` and delivered arguments intact 184 ms after launch. Keep `Microsoft.Toolkit.Uwp.Notifications`. Findings that bind later phases are in `spikes/ToastActivationSpike/FINDINGS.md` - notably that no Start Menu shortcut/AUMID entry is created (activation routes through the registered CLSID alone), and that the single-instance guard must not kill a `-ToastActivated` launch before its activation is handled.
- Single-instance guard (named mutex + activate-existing) - needed this early because toast activation can re-launch the exe.
- Tray icon: white/dark/green variants + ThemeWatcher + DPI-correct ico; tooltip bound to `MenuBarTitleText`; right-click context menu (Open, Float, Settings, Refresh, Quit).
- FlyoutWindow: borderless, positioned above tray corner from `Shell_NotifyIconGetRect`, light-dismiss on `Deactivated`, acrylic backdrop via WPF-UI **with a solid-color fallback** (acrylic availability is Win11-version-sensitive).
- Manual verify on Win11: light+dark taskbar, 100%/150% scaling, multi-monitor.

**Phase 7 - Flyout UI + floating panel.** Spec: `MenuBarView.swift`. PlayerRow control (headshot, name, state, statLine), UPCOMING/IN GAME/DONE sections, PPD label, rain/delay icon + tooltip, stream-link click → `Process.Start`, not-in-lineup flag (hitters only), footer buttons (Settings, Fantrax, Refresh with idle/spinning/done/failed states, Float, Quit). **FloatingPanelWindow**: always-on-top no-activate borderless window reusing the same section views, drag-by-background, frame saved/restored via settings, toggled by Float button, auto-open at launch when `AlwaysOpenPopout`. Bind everything to AppOrchestrator; `StateChanged` marshaled via `Dispatcher` (no-op if Core already runs on it).

**Phase 8 - Settings.** Spec: `SettingsView.swift`. SettingsWindow over `SettingsStore` (JSON, `%APPDATA%\onDeck\settings.json`, atomic write): roster URL field (fetch teams on submit), team picker with loading/error states, sync status + Sync Now (disabled while syncing / no team), display toggles (hide bench, always-open popout), five notification toggles, GitHub links.

**Phase 9 - Notifications.** Spec: `NotificationManager.swift`. ToastService implements `INotificationSink` on the stack proven in the Phase 6 spike. All five types: batting/pitching with tag = stable ID (`History.Remove(tag)` on purge); notInLineup with stable tag + `Group = "notInLineup-<gamePk>"` (`RemoveGroup` implements `PurgeNotInLineupAsync`); atBatResult/pitchingResult with no stable tag and `ExpirationTime` = +30s (auto-dismiss analog); `PurgeAllAsync` clears history + scheduled toasts. Headshot image from local cache path. Per-type enable toggles checked inside each sink method via `ISettingsStore`. Click → open the toast's click URL (stream link for batting/pitching/results, Fantrax roster page for notInLineup) - NOT just foregrounding the app. Manual verify: purge-on-state-change, game-start purge, click-through both app-running and app-dead, Focus Assist behavior full-screen.

**Phase 10 - System integration & ship.** `SystemEventsWatcher` (`PowerModeChanged.Resume`, `SessionSwitch.SessionUnlock` → `HandleSystemResumeAsync`), `StartupManager` (HKCU Run key, settings toggle), publish recipe → exe tested on a machine/VM without .NET installed.

**Phase 11 - Parity QA.** Run Mac and Windows apps side by side over live game days against the checklist below; fix gaps.

## Parity Checklist

- [ ] Tray: white baseball idle (dark variant on light taskbar), green when any player active; crisp at 100/125/150/200% scaling
- [ ] Tray tooltip shows active player names ("A | B | C +2"); right-click menu: Open, Float, Settings, Refresh, Quit
- [ ] Flyout opens on tray click anchored at tray corner; light-dismisses; acrylic (or fallback) look
- [ ] Floating panel: toggles from Float button, stays on top without stealing focus, drags by background, remembers position across restarts, auto-opens at launch when the setting is on
- [ ] UPCOMING: lineup status once managers submit cards (hours pre-game), PPD games remain visible with label, rain/delay icon with tooltip
- [ ] IN GAME sort: batting band, notBatting band, pitchers ranked explicitly
- [ ] DONE: only players whose statLine matches role
- [ ] Bench filter respects `hideBenchPlayers` + `rosterStatus` in all sections
- [ ] Not-in-lineup flag: hitters only
- [ ] Headshots render in flyout, floating panel, and toasts; prefetched on roster sync
- [ ] Stream links: Peacock / Apple TV / ESPN / Netflix / TBS / MLB.TV fallback
- [ ] Toasts: batting/pitching/notInLineup fire at same moments as macOS; stale ones purged on state transition; at-bat/pitching results fire on transition out of active and self-expire after ~30s
- [ ] Toast click opens the stream link (batting/pitching/results) or Fantrax page (not-in-lineup), app running or not; clicked toasts clear from Action Center
- [ ] Not-in-lineup toasts for a game purged when that game goes live; all toasts purged on schedule refresh / day rollover
- [ ] Each of the five notification toggles suppresses its type
- [ ] Toggling Hide bench players re-filters all sections immediately (no network sync)
- [ ] Footer Refresh button shows idle/spinning/done/failed off actual sync result
- [ ] Settings: team picker loads/error states, Sync Now disabled while syncing, last-synced timestamp
- [ ] Polling: 10s diffPatch when idle (~2 bytes), full-feed fallback on error/transition
- [ ] Pre-game one-shots at 2h/1h/30m; 15-min pre-first-game resync (skipped if game already started); no polling loops between events
- [ ] 8AM daily roster re-sync
- [ ] Sleep/wake/unlock recovery with 30s debounce
- [ ] Fantrax: today's period requested (not next), statusId 1/2/3/9 handled
- [ ] Ohtani dedup, same-name disambiguation by team, periods-in-names stripping
- [ ] Single exe runs on clean Windows 11 without .NET installed
- [ ] Idle memory stable over a full game day (target ~100MB working set; self-contained WPF typically idles 80-120MB - a modest miss is not a bug)

## Decisions (formerly Open Questions - all resolved 2026-08-08)

1. **Icon assets:** Tabler Icons `ball-baseball` (MIT license). Recolor to white/dark/green and render to multi-res .ico (16/20/24/32) in Phase 6. SF Symbols are Apple-licensed and stay macOS-only.
2. **Architecture targets:** win-x64 only. ARM Windows runs it under x64 emulation; add a win-arm64 publish later only on demand.
3. **Launch at login:** Settings toggle writing the HKCU Run key, **default off** (Phase 10 `StartupManager`).
4. **App identity:** display name `onDeck`, `onDeck.exe`, toast AUMID `dev.bjc.onDeck` (matches the Mac bundle ID). Settings at `%APPDATA%\onDeck\`.
5. **Code signing:** ship unsigned; recipients click "More info → Run anyway" once per download. Revisit (e.g. Azure Trusted Signing, ~$10/mo) only if distribution widens beyond league-mates.

## Resolved (deliberate deviations from macOS)

- **`menuBarTitle` text**: renders icon-only on macOS in practice (MenuBarExtra `Label` with an icon suppresses the text; user-confirmed). Windows equivalent: same info in the tray tooltip via `MenuBarTitleText`. Not a gap.
- **Clicked-toast cleanup**: Windows removes activated toasts automatically; the explicit `removeDeliveredNotifications` workaround from `NotificationDelegate.didReceive` is not ported.
