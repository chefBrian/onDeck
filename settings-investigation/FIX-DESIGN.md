# On Deck - Settings Memory Spike Investigation

**Branch**: `memory-probe-2` (will spin off a child branch when implementation starts)
**Status**: Design of record. Phase 1 hypothesis test pending.

## Problem

Every open of the Settings window causes a transient +230 MB spike in `phys_footprint`. Most of the growth releases within ~2s of closing, but the allocator retains a portion per open, ratcheting the baseline upward over a session. Behavior observed repeatedly; not a first-open cache-fill cost.

Evidence: during a 70-minute observation of the `memory-probe-2` debug build, `phys_footprint_peak` hit 361 MB. User attributed this to a Settings open; footprint then settled around 129 MB with 31 MB reclaimable (Fix C territory). Recurring every-open cost matches SwiftUI window-lifecycle allocation shape, not steady-state polling growth.

## Goal

Identify the mechanism that allocates ~230 MB per Settings open in onDeck's process, then eliminate or reduce it. Fix must not degrade Settings functionality.

## Approach: C -> A

The investigation proceeds from the strongest hypothesis (cheap to test) to an instrumented measurement only if the hypothesis fails.

## Phase 1: Activation-policy hypothesis test

Prime suspect is `SettingsView.onAppear` and `.onDisappear` flipping `NSApplication.activationPolicy` between `.accessory` and `.regular`:

```swift
.onAppear  { NSApplication.shared.setActivationPolicy(.regular) }
.onDisappear { NSApplication.shared.setActivationPolicy(.accessory) }
```

The flip transitions onDeck from menu-bar-only mode to a standard app, loading AppKit window-management, app-switcher, and Dock-tile infrastructure that accessory apps defer. Every-open cost + ~2s release cycle + multi-hundred-MB transient all fit this shape.

**Test:**
1. Comment out both `setActivationPolicy` calls in `SettingsView.swift`
2. Rebuild (Debug)
3. Capture baseline `phys_footprint` via `MemoryPressureRelief.currentFootprintMB()`
4. Open Settings via the menu bar footer Settings button
5. Capture footprint immediately after window appears
6. Capture footprint after waiting ~500 ms for the view body to settle
7. Close Settings
8. Capture footprint after ~3 s (allow system release)
9. Restore the activation-policy calls before moving on

**Outcomes:**
- Spike drops to <50 MB: hypothesis confirmed. Skip Phase 2, go to Phase 3A.
- Spike reduced but still >100 MB: partial hypothesis. Run Phase 2 to find the remainder.
- Spike unchanged (>150 MB): hypothesis wrong. Run Phase 2.

Additionally note Settings window behavior: does it appear focused? Accept keyboard input? Dismiss properly? These signals feed Phase 3's fix choice.

## Phase 2: Instrumented measurement (fallback)

If Phase 1 did not resolve the spike, add DEBUG-only `phys_footprint` logging at key points in `SettingsView` via `os.Logger(subsystem: "dev.bjc.onDeck", category: "memory")`. Code guarded by `#if DEBUG`; compiles out of Release.

**Log points:**
- `onAppear` entry
- Immediately after `setActivationPolicy(.regular)`
- After a 300 ms `Task.sleep` inside `onAppear` (post-render)
- `onDisappear` entry
- After `setActivationPolicy(.accessory)`
- After a 3 s delay (post-release)
- After a `MemoryPressureRelief.releaseReclaimablePages(reason: "settings close")` call

Each log line emits `phys_footprint` MB and the delta from the previous log point. The largest delta locates the culprit phase.

**Secondary narrowing (if Phase 2 logs do not point to a single phase):** binary-search by commenting out Form sections in `SettingsView.body` (Fantrax Roster, Display, Notifications, Links) one at a time, rebuild, remeasure. Not expected to be needed.

## Phase 3: Fix, by finding

Three branches, tried in order of increasing cost.

### 3A - Flip was obsolete

If Phase 1 eliminated the spike AND Settings still opens focused with keyboard input working, the flip was cargo-culted. Fix: delete both `setActivationPolicy` calls. One-line change.

### 3B - Flip is load-bearing but reducible

If Phase 1 eliminated the spike but Settings no longer focuses or accepts keyboard input correctly, the flip was doing real work but the full accessory->regular transition is overkill. Try replacing the flip with:

```swift
.onAppear { NSApp.activate(ignoringOtherApps: true) }
```

Remeasure. If focus is restored without the +230 MB cost, ship this. If not, iterate through alternatives (window-level `.makeKey()` calls, explicit `NSApp.keyWindow` handling).

### 3C - Window machinery is intrinsic to having a separate SwiftUI Settings scene

If Phase 2 reveals that SwiftUI's `Settings` scene loads expensive infrastructure regardless of activation-policy manipulation, migrate Settings into the `MenuBarExtra` popup as an inline panel. Click the footer's Settings button -> the popup swaps `MenuBarView` for `SettingsView` (or a toggle overlay), close button returns to `MenuBarView`. No separate `NSWindow`, no `Settings` scene, no activation-policy flip.

**3C trade-offs to resolve during implementation:**
- Popup width currently sized for roster rows (~340 pt); Settings renders at 450x400. Options: widen popup dynamically when Settings is showing, or condense Settings to fit narrower.
- Team Picker may feel cramped at narrower widths.
- `MenuBarExtra` popup auto-dismisses on focus loss; pasting the Fantrax League URL from another app breaks this model. Need an explicit "sticky" mode while Settings is showing.
- TextField keyboard input inside `MenuBarExtra` requires a small AppKit workaround (observed in community reports; not blocking).

Pattern is well-precedented in menu-bar apps (1Password, Bartender, iStat Menus all ship in-popup settings).

## Scope boundaries

In scope:
- The +230 MB per-open spike in onDeck's own process
- Changes to `SettingsView.swift` and, if 3C, to `MenuBarView.swift`

Out of scope:
- Other memory topics (polling churn, notification daemon memory, URLSession retention) - covered by prior fixes or deferred diagnostics
- Multi-provider Settings UX (Yahoo, CBS, etc.) - future work; Settings shape may need to support providers later, but this fix is about memory, not layout

## Acceptance criteria

- Per-Settings-open `phys_footprint` delta drops from ~230 MB to under 30 MB (roughly an order of magnitude reduction; below noise)
- Settings window/panel still opens, focuses, accepts keyboard input, and saves settings correctly
- No regression in `phys_footprint` during normal polling (measure before+after against a baseline slate run)

## Risk

- Phase 1 hypothesis could be wrong. Mitigation: Phase 2 instrumentation gives us an evidence-based fallback without committing to any fix.
- 3C is a real refactor; risk of breaking non-memory behavior (keyboard focus, dismissal, Team Picker UX). Mitigation: only reached if 3A and 3B both fail.

## Decisions made during brainstorming

- Measurement tool: reuse `MemoryPressureRelief.currentFootprintMB()` (already `internal`). No new helper.
- Instrumentation scope: permanent DEBUG-guarded logs (not throwaway), so Settings can be re-measured if it grows later (e.g. when multi-provider is added).
- UX constraint: preserve current Settings functionality. Fixes that would change Settings behavior (losing focus, losing keyboard input) are rejected.
- Flip-once-per-session was rejected: would permanently promote onDeck to a regular app (Dock icon, Cmd+Tab visibility) for the rest of the session, which conflicts with the menu-bar-only design.
