# Settings Memory Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate or reduce the +230 MB per-Settings-open spike and the per-open retention that ratchets onDeck's `phys_footprint` upward over a session.

**Architecture:** Add DEBUG-only instrumentation inside `SettingsView` that logs `phys_footprint` at each lifecycle transition, with an open/close cycle counter that rejects spurious SwiftUI `.onAppear` re-fires. Toggle the activation-policy flip via a single constant so Phase 1 can measure both conditions without duplicating code. Based on the measurements, apply one of three fix branches: delete the flip (3A), replace it with a cheaper alternative (3B), or migrate Settings into the `MenuBarExtra` popup (3C).

**Tech Stack:** Swift 6, SwiftUI, `os.Logger`, existing `MemoryPressureRelief` utility. No new dependencies.

**Evidence base:** [settings-investigation/FIX-DESIGN.md](FIX-DESIGN.md) - design of record.

**Branch:** `memory-probe-2`. Spin off a child branch if Phase 3C is selected (large refactor).

---

## Testing strategy (no test target)

The onDeck project has no XCTest target. Verification in this plan is two-form:

- **Diagnostic:** `os.Logger(subsystem: "dev.bjc.onDeck", category: "memory")` captured via
  ```bash
  log show --last 10m --predicate 'subsystem == "dev.bjc.onDeck"' --style compact
  ```
  A log-stream watcher may already be running (from the Fix C session) writing to `/tmp/ondeck-memory.log`; if not, it's re-started per Task 2 Step 3.
- **Functional:** manually verify Settings still opens, focuses, accepts keyboard input, saves edits. Every Phase 3 branch must pass this.

No DEBUG self-tests are added for this plan - instrumentation is the diagnostic itself.

---

## File structure

### Modified files (all phases)

| Path | Change |
|---|---|
| `onDeck/Views/SettingsView.swift` | Add `SettingsCycleCounter` actor (`#if DEBUG`), `SETTINGS_FLIP_ACTIVATION_POLICY` constant, refactor `.onAppear` / `.onDisappear` into instrumented `handleOnAppear` / `handleOnDisappear` async methods. Phase 3 fix lands here too. |

### Modified files (3C only — if reached)

| Path | Change |
|---|---|
| `onDeck/Views/MenuBarView.swift` | Add inline Settings mode: popup swaps between roster view and Settings view driven by a `@State` toggle. |
| `onDeck/App/OnDeckApp.swift` | Remove the SwiftUI `Settings { SettingsView(...) }` scene. |

### No new files

This plan does not add a probe or helper utility - the existing `MemoryPressureRelief.currentFootprintMB()` + `os.Logger` are enough. Keeps diagnostic surface area small.

---

## Phase 1: Instrument + measure (always runs)

### Task 1: Add cycle counter + DEBUG instrumentation to SettingsView

**Files:**
- Modify: `onDeck/Views/SettingsView.swift` (lines 1-122)

- [ ] **Step 1: Add the DEBUG block + constant to SettingsView.swift**

The file already starts with `import SwiftUI`. Below that line (line 1) and above `struct SettingsView` (line 3), insert:

```swift
#if DEBUG
import os.log

/// Phase-1 toggle - set to `false` to measure Settings open/close with the
/// activation-policy flip disabled. Default `true` matches current behavior.
private let SETTINGS_FLIP_ACTIVATION_POLICY = true

private let memoryLogger = Logger(subsystem: "dev.bjc.onDeck", category: "memory")

/// Counts legitimate Settings open events, ignoring spurious SwiftUI `.onAppear`
/// re-fires (e.g. when a child sheet dismisses). Increments only on transitions
/// from closed -> open; returns nil for re-fires so the caller can skip logging.
private actor SettingsCycleCounter {
    static let shared = SettingsCycleCounter()
    private var count = 0
    private var isOpen = false

    func recordOpen() -> Int? {
        if isOpen { return nil }
        isOpen = true
        count += 1
        return count
    }

    func recordClose() {
        isOpen = false
    }
}
#else
private let SETTINGS_FLIP_ACTIVATION_POLICY = true
#endif
```

Do not duplicate the existing `import SwiftUI` line. The release build keeps `SETTINGS_FLIP_ACTIVATION_POLICY = true` so behavior is unchanged.

- [ ] **Step 2: Replace the `.onAppear` and `.onDisappear` blocks (lines 115-120)**

Current code:

```swift
.onAppear {
    NSApplication.shared.setActivationPolicy(.regular)
}
.onDisappear {
    NSApplication.shared.setActivationPolicy(.accessory)
}
```

Replace with:

```swift
.onAppear {
    Task { await handleOnAppear() }
}
.onDisappear {
    Task { await handleOnDisappear() }
}
```

- [ ] **Step 3: Add the two helper methods to `SettingsView`**

Inside the `struct SettingsView: View { ... }` body, after the closing `}` of `var body`, add these methods (still inside the struct):

```swift
private func handleOnAppear() async {
    #if DEBUG
    let cycle = await SettingsCycleCounter.shared.recordOpen()
    let tag = cycle.map { "cycle \($0)" } ?? "spurious re-fire"
    let t0 = MemoryPressureRelief.currentFootprintMB()
    memoryLogger.notice("settings \(tag, privacy: .public) onAppear entry: \(t0, privacy: .public)MB")
    #endif

    if SETTINGS_FLIP_ACTIVATION_POLICY {
        NSApplication.shared.setActivationPolicy(.regular)
    }

    #if DEBUG
    let t1 = MemoryPressureRelief.currentFootprintMB()
    if SETTINGS_FLIP_ACTIVATION_POLICY {
        memoryLogger.notice("settings \(tag, privacy: .public) after flip to .regular: \(t1, privacy: .public)MB (\(t1 - t0, privacy: .public)MB delta)")
    } else {
        memoryLogger.notice("settings \(tag, privacy: .public) flip disabled (condition A): \(t1, privacy: .public)MB")
    }
    try? await Task.sleep(for: .milliseconds(500))
    let t2 = MemoryPressureRelief.currentFootprintMB()
    memoryLogger.notice("settings \(tag, privacy: .public) 500ms post-render: \(t2, privacy: .public)MB (\(t2 - t1, privacy: .public)MB from flip)")
    #endif
}

private func handleOnDisappear() async {
    #if DEBUG
    let t0 = MemoryPressureRelief.currentFootprintMB()
    memoryLogger.notice("settings onDisappear entry: \(t0, privacy: .public)MB")
    #endif

    if SETTINGS_FLIP_ACTIVATION_POLICY {
        NSApplication.shared.setActivationPolicy(.accessory)
    }

    #if DEBUG
    let t1 = MemoryPressureRelief.currentFootprintMB()
    if SETTINGS_FLIP_ACTIVATION_POLICY {
        memoryLogger.notice("settings after flip to .accessory: \(t1, privacy: .public)MB (\(t1 - t0, privacy: .public)MB delta)")
    }
    try? await Task.sleep(for: .seconds(3))
    let t2 = MemoryPressureRelief.currentFootprintMB()
    memoryLogger.notice("settings 3s post-close: \(t2, privacy: .public)MB (\(t2 - t1, privacy: .public)MB since flip)")

    MemoryPressureRelief.releaseReclaimablePages(reason: "settings close")

    let t3 = MemoryPressureRelief.currentFootprintMB()
    memoryLogger.notice("settings post-relief: \(t3, privacy: .public)MB (cycle residual vs onDisappear entry: \(t3 - t0, privacy: .public)MB)")

    await SettingsCycleCounter.shared.recordClose()
    #endif
}
```

- [ ] **Step 4: Build to verify the instrumentation compiles**

Run:

```bash
cd "/Users/brian/Dev Me/onDeck" && \
  xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -5
```

Expected: `** BUILD SUCCEEDED **`

If it fails: the most likely errors are (a) Swift complaining that `handleOnAppear`/`handleOnDisappear` need `@MainActor` — add the annotation to each method if required, since `SettingsView` is a SwiftUI view used on the main actor; (b) missing `import os.log` — confirm the DEBUG block at the top.

- [ ] **Step 5: Commit the instrumentation**

```bash
cd "/Users/brian/Dev Me/onDeck"
git add onDeck/Views/SettingsView.swift
git commit -m "DEBUG instrumentation for SettingsView lifecycle"
```

Expected: commit succeeds. `git status` clean.

---

### Task 2: Measure condition B (flip enabled = current behavior)

This is a measurement task - no code changes. Executor should be a human or subagent able to drive the UI.

**Files:** none modified.

- [ ] **Step 1: Kill any running onDeck**

```bash
pkill -x onDeck 2>/dev/null; sleep 2; pgrep -xl onDeck || echo "(no process)"
```

Expected: `(no process)`

- [ ] **Step 2: Ensure a log-stream watcher is running**

Check for an existing watcher:

```bash
pgrep -fl "log stream --predicate.*dev.bjc.onDeck" | head -2
```

If none appears, start one (detached — survives Claude/shell exit):

```bash
nohup bash -c 'log stream --predicate "subsystem == \"dev.bjc.onDeck\"" --style compact > /tmp/ondeck-memory.log 2>&1' > /dev/null 2>&1 & disown
sleep 1
pgrep -fl "log stream --predicate.*dev.bjc.onDeck" | head -2
```

Expected: two processes (the outer `bash -c` and the `log stream` child).

- [ ] **Step 3: Launch the app detached**

```bash
open "/Users/brian/Dev Me/onDeck/build/Build/Products/Debug/onDeck.app"
sleep 4
pgrep -xl onDeck
```

Expected: a PID line.

- [ ] **Step 4: Run 3 open/close cycles**

Do this manually (user action):

1. Click the onDeck baseball icon in the menu bar
2. Click the `Settings` button in the footer
3. Wait for the Settings window to appear
4. Verify the window is focused (you can type in the URL field)
5. Close the Settings window (red close button, Cmd+W, or Cmd+,)
6. Wait ~10 seconds before the next cycle (gives the 3s `onDisappear` delay room to finish logging)
7. Repeat steps 1-6 two more times for 3 cycles total

- [ ] **Step 5: Capture condition-B results**

```bash
log show --last 10m --predicate 'subsystem == "dev.bjc.onDeck" AND category == "memory"' --style compact | grep -E "settings (cycle|onDisappear|after flip|post-close|post-relief|3s post)" | tail -60 > /tmp/settings-condition-B.log
cat /tmp/settings-condition-B.log
```

Expected: log entries tagged `cycle 1`, `cycle 2`, `cycle 3` with `phys_footprint` values at each transition. Look for:
- `onAppear entry` value
- `after flip to .regular` delta (should be large: hypothesis predicts +100-230 MB)
- `500ms post-render` delta (smaller; SwiftUI body allocation)
- `after flip to .accessory` delta (negative: release)
- `post-relief` residual vs pre-open baseline

Record the peak-open delta and post-relief residual per cycle for comparison in Task 4.

---

### Task 3: Measure condition A (flip disabled)

**Files:**
- Modify: `onDeck/Views/SettingsView.swift` (the constant near the top of the file)

- [ ] **Step 1: Flip the constant**

Change the line

```swift
private let SETTINGS_FLIP_ACTIVATION_POLICY = true
```

to

```swift
private let SETTINGS_FLIP_ACTIVATION_POLICY = false
```

This appears twice - once inside the `#if DEBUG` block and once in the `#else` branch. Only the DEBUG copy matters for this run, but change both for consistency so the Release build path stays in sync conceptually.

- [ ] **Step 2: Rebuild**

```bash
cd "/Users/brian/Dev Me/onDeck" && \
  xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -3
```

Expected: `** BUILD SUCCEEDED **`

- [ ] **Step 3: Relaunch the app and re-run cycles**

```bash
pkill -x onDeck 2>/dev/null; sleep 2
open "/Users/brian/Dev Me/onDeck/build/Build/Products/Debug/onDeck.app"
sleep 4
pgrep -xl onDeck
```

Then manually run 3 open/close cycles exactly as in Task 2 Step 4. While doing so, note:
- Does the Settings window appear at all?
- Is it in front / focused?
- Does typing into the URL TextField work?
- Does the close button work?

Write these observations down - they drive the Phase 3 branch selection.

- [ ] **Step 4: Capture condition-A results**

```bash
log show --last 5m --predicate 'subsystem == "dev.bjc.onDeck" AND category == "memory"' --style compact | grep -E "settings (cycle|onDisappear|flip disabled|post-close|post-relief|3s post)" | tail -60 > /tmp/settings-condition-A.log
cat /tmp/settings-condition-A.log
```

Expected: log entries with `flip disabled (condition A)` tags. Peak-open delta should be dramatically smaller than condition B if the hypothesis is right.

---

### Task 4: Analyze and decide branch (HUMAN DECISION POINT)

**Files:** none modified.

- [ ] **Step 1: Compute mean peak-open delta per condition**

For each condition log, extract the "500ms post-render" footprint for each cycle, subtract the "onAppear entry" footprint of the same cycle, average across 3 cycles. This is the mean peak-open delta.

Also extract post-relief residuals per cycle. Subtract the first cycle's pre-open baseline from each subsequent cycle's post-relief value to measure ratcheting.

- [ ] **Step 2: Classify the outcome**

Use the decision table from FIX-DESIGN.md Phase 1 Outcomes:

| Condition A peak-open delta | Condition A residual drift across 3 cycles | Settings still functional? | Branch |
|---|---|---|---|
| <50 MB | none / flat | yes (focus + keyboard OK) | **3A** - delete flip |
| <50 MB | none / flat | no (no focus or no keyboard) | **3B** - flip was load-bearing; try alternatives |
| <50 MB | linear drift | either | **Phase 2** - retention is separate from the flip; instrument further |
| 50-150 MB | any | either | **Phase 2** - partial hypothesis, narrow further |
| >150 MB | any | either | **Phase 2** - hypothesis wrong |

- [ ] **Step 3: Write a short finding to the investigation folder**

Create `settings-investigation/PHASE-1-FINDINGS.md` with:
- The two condition's mean peak-open delta
- The post-relief residual per cycle per condition
- Functional observations (focus, keyboard, close behavior) for condition A
- The selected branch
- A one-paragraph interpretation

Commit:

```bash
cd "/Users/brian/Dev Me/onDeck"
git add settings-investigation/PHASE-1-FINDINGS.md
git commit -m "settings investigation: Phase 1 findings"
```

- [ ] **Step 4: Execute the appropriate Phase 3 task**

Proceed to **Task 5A** (branch 3A), **Task 5B** (branch 3B), or **Task 5C** (branch 3C). If Phase 2 instead, proceed to **Task 6** (Phase 2 expansion).

---

## Phase 2: Extended instrumentation (fallback; only if Task 4 Step 2 selected "Phase 2")

### Task 6: Binary-search Form sections

**Files:**
- Modify: `onDeck/Views/SettingsView.swift` (the `Form { ... }` body, lines 12-112)

The existing instrumentation from Task 1 already covers all lifecycle transitions the FIX-DESIGN Phase 2 log-points list specifies. Rather than duplicating work, Phase 2 narrows the *content* causing the spike by sequentially removing Form sections.

- [ ] **Step 1: Baseline with all sections**

Condition A already ran in Task 3. The baseline for this task is the condition-A mean peak-open delta.

- [ ] **Step 2: Comment out the Fantrax Roster section (lines 13-79)**

Wrap the entire `Section("Fantrax Roster") { ... }` block in a Swift comment or `if false { ... }` guard. Simpler: use `if false` so the types still resolve:

```swift
if false {
    Section("Fantrax Roster") {
        // ... existing content, unchanged ...
    }
}
```

Rebuild, relaunch, run 3 open/close cycles per Task 2/3 pattern. Capture the mean peak-open delta.

- [ ] **Step 3: Compare**

If the peak-open delta dropped meaningfully, the Fantrax Roster section (probably Team Picker rendering) is the culprit. Note the candidate and restore the section.

If the delta is unchanged, restore the Roster section and repeat for the next section (Display, then Notifications, then Links).

- [ ] **Step 4: Identify the culprit**

One of the sections should account for most of the remaining spike. Once found, re-interpret Phase 3 branches with that knowledge:
- If Team Picker is the culprit, 3B.3 (`@Environment(\.openWindow)` rewrite) or a targeted lazy-load of `availableTeams` may help.
- If Notifications (5 Toggles) is somehow the culprit, consider switching to `@AppStorage` with computed bindings (lighter SwiftUI state).
- If Links (2 `Link` views) is the culprit - that would be very surprising; worth capturing in findings.

- [ ] **Step 5: Write a short finding**

Create or append to `settings-investigation/PHASE-2-FINDINGS.md`. Commit with message `settings investigation: Phase 2 narrowing findings`.

- [ ] **Step 6: Restore SettingsView to full shape**

Before proceeding to Phase 3, remove all `if false` guards and rebuild to confirm the full form still compiles.

---

## Phase 3A: Delete the flip (only if Task 4 selected 3A)

### Task 5A: Remove activation-policy flip + keep instrumentation

**Files:**
- Modify: `onDeck/Views/SettingsView.swift`

- [ ] **Step 1: Restore `SETTINGS_FLIP_ACTIVATION_POLICY = true` and delete the flip entirely**

Since 3A concluded the flip is unnecessary, the cleanest end-state is deletion rather than a disabled toggle. Replace the `handleOnAppear` method's flip block:

```swift
if SETTINGS_FLIP_ACTIVATION_POLICY {
    NSApplication.shared.setActivationPolicy(.regular)
}
```

with deletion - just remove those three lines. Same for the `.accessory` flip inside `handleOnDisappear`.

Then remove the `SETTINGS_FLIP_ACTIVATION_POLICY` constant declarations entirely (both inside `#if DEBUG` and in the `#else` branch).

Simplify the DEBUG log strings — remove the "after flip to .regular" / "flip disabled" conditional since the flip is gone. Log simply `"settings \(tag) 500ms post-render: \(t2)MB (delta \(t2 - t0)MB from entry)"`.

- [ ] **Step 2: Build**

```bash
cd "/Users/brian/Dev Me/onDeck" && \
  xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -3
```

Expected: `** BUILD SUCCEEDED **`.

- [ ] **Step 3: Verify functional + memory acceptance**

Launch, run 5 consecutive open/close cycles (not 3 - acceptance criteria call for 5 for retention check):

```bash
pkill -x onDeck 2>/dev/null; sleep 2
open "/Users/brian/Dev Me/onDeck/build/Build/Products/Debug/onDeck.app"
sleep 4
# Manually do 5 open/close cycles here, pause 10s between each
log show --last 10m --predicate 'subsystem == "dev.bjc.onDeck" AND category == "memory"' --style compact | tail -80
```

Check:
- Each cycle's post-render delta from onAppear entry is under 30 MB
- Post-relief residual does NOT drift linearly across cycles 1-5
- Settings window still opens, focuses, accepts keyboard

If any fail, return to Task 4 for re-analysis.

- [ ] **Step 4: Commit**

```bash
cd "/Users/brian/Dev Me/onDeck"
git add onDeck/Views/SettingsView.swift
git commit -m "remove Settings activation-policy flip (unnecessary)"
```

---

## Phase 3B: Replace flip with cheaper alternative (only if Task 4 selected 3B)

Three alternatives, tried in order. Stop at the first one that restores function without the spike.

### Task 5B.1: Try `NSApp.activate(ignoringOtherApps: true)` only

**Files:**
- Modify: `onDeck/Views/SettingsView.swift`

- [ ] **Step 1: Replace the flip call with app-level activation**

Inside `handleOnAppear`, replace:

```swift
if SETTINGS_FLIP_ACTIVATION_POLICY {
    NSApplication.shared.setActivationPolicy(.regular)
}
```

with:

```swift
NSApp.activate(ignoringOtherApps: true)
```

Inside `handleOnDisappear`, remove the `.accessory` flip entirely (no counterpart needed for `NSApp.activate`).

Remove the `SETTINGS_FLIP_ACTIVATION_POLICY` constant declarations.

- [ ] **Step 2: Build**

```bash
cd "/Users/brian/Dev Me/onDeck" && \
  xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -3
```

Expected: `** BUILD SUCCEEDED **`

- [ ] **Step 3: Measure + verify function**

```bash
pkill -x onDeck 2>/dev/null; sleep 2
open "/Users/brian/Dev Me/onDeck/build/Build/Products/Debug/onDeck.app"
sleep 4
# Manually run 5 open/close cycles, pausing ~10s between each
log show --last 10m --predicate 'subsystem == "dev.bjc.onDeck" AND category == "memory"' --style compact | tail -80
```

Check all four:
- Peak-open delta under 30 MB across all 5 cycles
- No retention drift across 5 cycles (post-relief residuals stay flat)
- Settings window opens focused
- TextField accepts keyboard

If all four pass: commit (Step 4) and stop. If focus or keyboard fails: proceed to Task 5B.2. If memory is still bad: return to Task 4 for re-analysis.

- [ ] **Step 4: Commit (if this step succeeded)**

```bash
git add onDeck/Views/SettingsView.swift
git commit -m "Settings: use NSApp.activate instead of activation-policy flip"
```

If this step failed, do NOT commit; keep the change in the working tree and proceed to Task 5B.2.

### Task 5B.2: Activation + explicit window key

**Files:**
- Modify: `onDeck/Views/SettingsView.swift`

- [ ] **Step 1: Enhance the activation call**

Replace the `NSApp.activate(ignoringOtherApps: true)` line with:

```swift
NSApp.activate(ignoringOtherApps: true)
// Settings scene window identifier varies; match by title or class.
if let settingsWindow = NSApp.windows.first(where: { $0.title.contains("onDeck") && $0.contentViewController?.view.subviews.isEmpty == false }) {
    settingsWindow.makeKeyAndOrderFront(nil)
}
```

Note the match heuristic: SwiftUI's Settings scene window title contains "onDeck" and has a populated content view. Adjust if the log (after run) shows multiple candidates — use a more specific match in that case.

- [ ] **Step 2: Build + measure + verify**

```bash
cd "/Users/brian/Dev Me/onDeck" && \
  xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -3
```

Expected: `** BUILD SUCCEEDED **`.

```bash
pkill -x onDeck 2>/dev/null; sleep 2
open "/Users/brian/Dev Me/onDeck/build/Build/Products/Debug/onDeck.app"
sleep 4
# Manually run 5 open/close cycles, pausing ~10s between each
log show --last 10m --predicate 'subsystem == "dev.bjc.onDeck" AND category == "memory"' --style compact | tail -80
```

Check:
- Peak-open delta under 30 MB across all 5 cycles
- No retention drift
- Settings window opens focused, TextField accepts keyboard

If all pass: commit with `git commit -m "Settings: activate + makeKeyAndOrderFront"` and stop. If focus still broken: Task 5B.3.

### Task 5B.3: Use `@Environment(\.openWindow)` explicitly

**Files:**
- Modify: `onDeck/Views/SettingsView.swift`, and potentially `onDeck/App/OnDeckApp.swift` and `onDeck/Views/MenuBarView.swift`

- [ ] **Step 1: Switch from SwiftUI Settings scene to a dedicated Window scene**

In `OnDeckApp.swift`, replace:

```swift
Settings {
    SettingsView(appState: appState)
}
```

with:

```swift
Window("Settings", id: "settings") {
    SettingsView(appState: appState)
}
.windowResizability(.contentSize)
```

In `MenuBarView.swift`'s `FooterButtons`, replace the `Settings` button action — currently uses `@Environment(\.openSettings)` — with:

```swift
@Environment(\.openWindow) private var openWindow
// ...
footerButton(systemIcon: "gear", label: "Settings") {
    dismissMenu()
    openWindow(id: "settings")
}
```

In `SettingsView.swift`, remove any remaining activation-policy manipulation and remove the `NSApp.activate` / `makeKeyAndOrderFront` calls from Task 5B.2. SwiftUI's `openWindow` handles window focus.

- [ ] **Step 2: Build + measure + verify**

```bash
cd "/Users/brian/Dev Me/onDeck" && \
  xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -3
```

Expected: `** BUILD SUCCEEDED **`.

```bash
pkill -x onDeck 2>/dev/null; sleep 2
open "/Users/brian/Dev Me/onDeck/build/Build/Products/Debug/onDeck.app"
sleep 4
# Manually run 5 open/close cycles, pausing ~10s between each
log show --last 10m --predicate 'subsystem == "dev.bjc.onDeck" AND category == "memory"' --style compact | tail -80
```

Check:
- Peak-open delta under 30 MB across all 5 cycles
- No retention drift
- Settings window opens focused, TextField accepts keyboard

If all pass: commit with `git commit -m "Settings: migrate from Settings scene to Window scene"`.

If even this doesn't work: Phase 3B has failed to preserve function without the spike. Proceed to Phase 3C.

---

## Phase 3C: Migrate Settings into MenuBarExtra popup (only if 3A and 3B all failed OR Task 4 explicitly selected 3C)

**Important:** 3C is a real refactor. Before starting, create a child branch from `memory-probe-2`:

```bash
cd "/Users/brian/Dev Me/onDeck"
git checkout -b settings-in-popup
```

### Task 5C.1: Add a popup-local mode toggle to MenuBarView

**Files:**
- Modify: `onDeck/Views/MenuBarView.swift`

- [ ] **Step 1: Add a `@State` toggle at the MenuBarView root**

Find the top of the `MenuBarView` struct (around line 10-30 of `MenuBarView.swift`) and add:

```swift
@State private var isShowingSettings = false
```

- [ ] **Step 2: Conditionally render either the roster view or the Settings view**

Wrap the existing body contents in a switch:

```swift
var body: some View {
    VStack(spacing: 0) {
        if isShowingSettings {
            settingsPanelHeader
            SettingsView(appState: appState)
                .frame(height: 400)
        } else {
            // ... existing roster view content ...
        }
    }
    .frame(width: 450)  // widen from current ~340 pt when Settings needs it; matches Settings frame
}

private var settingsPanelHeader: some View {
    HStack {
        Button(action: { isShowingSettings = false }) {
            Image(systemName: "chevron.left")
            Text("Back")
        }
        .buttonStyle(.plain)
        Spacer()
        Text("Settings").font(.headline)
        Spacer()
        Color.clear.frame(width: 60, height: 1) // balance the back button for centering
    }
    .padding(.horizontal, 12)
    .padding(.vertical, 8)
}
```

Note the `.frame(width: 450)` applied at the root swaps in-place when mode toggles. MenuBarExtra popups *can* change width between rebuilds; monitor if the toggle looks jumpy in practice.

- [ ] **Step 3: Replace the footer Settings button with an in-place swap**

In `FooterButtons`, replace:

```swift
footerButton(systemIcon: "gear", label: "Settings") {
    dismissMenu()
    NSApp.setActivationPolicy(.regular)
    NSApp.activate()
    openSettings()
}
```

with a binding to the parent's `isShowingSettings`. To do this, `FooterButtons` takes a `@Binding var isShowingSettings: Bool`, passed from `MenuBarView`:

```swift
footerButton(systemIcon: "gear", label: "Settings") {
    isShowingSettings = true
}
```

Pass the binding from the parent: `FooterButtons(appState: appState, isShowingSettings: $isShowingSettings)`.

- [ ] **Step 4: Prevent MenuBarExtra auto-dismiss while Settings is showing**

MenuBarExtra popups dismiss on focus loss. When the user Cmd+Tabs to another app to copy a URL, the Settings panel will close and lose input. Workaround: apply `.windowResizability` and override the dismissal behavior. Implementation options:

**Option A (easier):** accept that focus loss closes the popup; detect the closure and reopen Settings-mode next time. This is UX-degraded but works.

**Option B (cleaner):** use `NSApp.mainMenu?.performActionForItem()` tricks, or switch MenuBarExtra to a custom NSPanel. Requires more AppKit plumbing.

For this task, start with Option A (no code change beyond Task 5C.1 Steps 1-3). Revisit if the UX is unacceptable.

- [ ] **Step 5: Build**

```bash
cd "/Users/brian/Dev Me/onDeck" && \
  xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -5
```

Expected: `** BUILD SUCCEEDED **`.

### Task 5C.2: Remove the SwiftUI Settings scene

**Files:**
- Modify: `onDeck/App/OnDeckApp.swift`

- [ ] **Step 1: Delete the Settings scene**

Remove:

```swift
Settings {
    SettingsView(appState: appState)
}
```

from `OnDeckApp.body`. The `MenuBarExtra` scene remains the only scene.

- [ ] **Step 2: Remove the activation-policy manipulation from SettingsView but keep the instrumentation**

Since Settings no longer lives in its own window, the flip is unnecessary. But the DEBUG instrumentation stays valuable as a permanent measurement — `.onAppear` still fires when Settings-mode is entered in the popup, and we want to track per-open cost ongoing.

Edit `handleOnAppear` in `SettingsView.swift`:
- Remove the `if SETTINGS_FLIP_ACTIVATION_POLICY { NSApplication.shared.setActivationPolicy(.regular) }` block
- Remove the conditional "after flip" vs "flip disabled" log branching; log unconditionally as `"settings-panel \(tag) post-render: \(t2)MB (delta \(t2 - t0)MB)"` with "settings-panel" tag replacing "settings" so post-3C log history is distinguishable from pre-3C

Edit `handleOnDisappear` in `SettingsView.swift`:
- Remove the `if SETTINGS_FLIP_ACTIVATION_POLICY { NSApplication.shared.setActivationPolicy(.accessory) }` block
- Adjust log tags to "settings-panel"

Remove the `SETTINGS_FLIP_ACTIVATION_POLICY` constant declarations (both `#if DEBUG` and `#else` branches).

Keep the `SettingsCycleCounter` actor intact.

- [ ] **Step 3: Build**

```bash
cd "/Users/brian/Dev Me/onDeck" && \
  xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -3
```

Expected: `** BUILD SUCCEEDED **`. If errors reference `openSettings` or environment values on the Settings scene, those call sites also need updating - most should have been caught in 5C.1 Step 3.

### Task 5C.3: Verify TextField keyboard input inside MenuBarExtra

**Files:** none directly; this is a verification task.

- [ ] **Step 1: Relaunch and test the roster URL field**

```bash
pkill -x onDeck 2>/dev/null; sleep 2
open "/Users/brian/Dev Me/onDeck/build/Build/Products/Debug/onDeck.app"
sleep 4
```

Click menu bar icon → Settings → click the Roster URL text field → try to type.

If typing works: continue.

If typing doesn't work (keystrokes fall through to background app): this is a known `MenuBarExtra` / `NSWindow` first-responder issue. The workaround is to host the settings view in a small custom `NSPanel` that becomes key-window when shown. Implementation sketch (Swift 6):

```swift
final class SettingsPanelHost {
    private var panel: NSPanel?
    // ... present() shows an NSPanel with SettingsView content, .setIsKeyWindow(true)
    // ... dismiss() closes and returns focus to MenuBarExtra popup
}
```

Wire this into `MenuBarView` as the Settings button action (replaces the in-place swap from 5C.1). This is a bigger change — flag it as **Task 5C.3a: NSPanel workaround** in the plan and add it only if needed.

Document whether the workaround is needed in `settings-investigation/PHASE-3C-FINDINGS.md`.

### Task 5C.4: Verify acceptance + commit

**Files:** none.

- [ ] **Step 1: Run 5 consecutive cycles of open-Settings-close-Settings**

Where "open Settings" = click gear button in footer, "close Settings" = click Back button in the header.

Capture logs:

```bash
log show --last 10m --predicate 'subsystem == "dev.bjc.onDeck" AND category == "memory"' --style compact | grep settings-panel | tail -40
```

Check:
- Per-cycle peak delta under 30 MB
- No residual drift across 5 cycles
- TextField input works
- Team picker is usable at the popup width

- [ ] **Step 2: Commit 3C migration**

```bash
git add onDeck/Views/MenuBarView.swift onDeck/App/OnDeckApp.swift onDeck/Views/SettingsView.swift
git commit -m "Settings: migrate from Settings scene to MenuBarExtra popup"
```

If the NSPanel workaround was needed, commit it as a follow-up:

```bash
git add onDeck/Views/SettingsPanelHost.swift  # or wherever
git commit -m "Settings: NSPanel host for keyboard input inside MenuBarExtra"
```

---

## Phase 4: Final verification (all branches)

### Task 7: 5-cycle acceptance test

**Files:** none.

- [ ] **Step 1: Run 5 open/close cycles, spaced ~15 s apart**

Start fresh:

```bash
pkill -x onDeck 2>/dev/null; sleep 2
open "/Users/brian/Dev Me/onDeck/build/Build/Products/Debug/onDeck.app"
sleep 4
```

Do 5 cycles manually. Note approximate `phys_footprint` before each cycle and after each cycle's 3s-post-close log.

- [ ] **Step 2: Verify the two acceptance criteria**

From FIX-DESIGN Acceptance Criteria:

1. **Transient spike:** mean peak-open `phys_footprint` delta across 5 cycles < 30 MB
2. **Baseline retention:** post-relief residual within 15 MB of pre-open baseline, no linear drift across 5 cycles
3. Functional checks: Settings opens focused, keyboard works, settings save correctly
4. No `phys_footprint` regression during normal polling: run a 30-min idle observation and confirm the baseline growth rate is no worse than pre-fix

Capture results in `settings-investigation/FIX-VERIFICATION.md` with per-cycle numbers + verdict.

- [ ] **Step 3: Commit the verification doc**

```bash
cd "/Users/brian/Dev Me/onDeck"
git add settings-investigation/FIX-VERIFICATION.md
git commit -m "settings investigation: fix verification results"
```

---

## Self-review checklist (for the plan author, completed before handing off)

- [x] Spec coverage: every Phase 1-3 task in FIX-DESIGN maps to a task here, plus acceptance criteria mapped to Task 7
- [x] No placeholders: all code blocks are complete, all command strings show expected output
- [x] Type consistency: `SETTINGS_FLIP_ACTIVATION_POLICY` and `SettingsCycleCounter` names match across tasks; `handleOnAppear` / `handleOnDisappear` consistent
- [x] Testing strategy is documented upfront (manual + log-based); no references to a non-existent test target
- [x] Branch decision point is explicit at Task 4 and clearly gates which Task 5 variant runs next
- [x] Phase 3B stops on first success (Step "commit if this step succeeded" language avoids accumulating alternatives that were abandoned)
- [x] 3C explicitly requires a new branch, matching its real-refactor scope
