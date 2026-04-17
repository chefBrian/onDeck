# Phase 1 - Interim Finding (Task 2, condition B)

Captured after running 3 open/close cycles with the Task 1 instrumentation (flip ENABLED, the current-behavior baseline).

## Condition B raw data (idle baseline: 70 MB)

| Cycle | onAppear entry | after onAppear flip | 500ms post-render | onDisappear entry | after onDisappear flip | 3s post-close | post-relief |
|---|---|---|---|---|---|---|---|
| 1 | 297 MB | 297 MB (+0) | 297 MB (+0) | 297 MB | 297 MB (+0) | 69 MB (-228) | 69 MB |
| 2 | 300 MB | 300 MB (+0) | 297 MB (-3) | 301 MB | 301 MB (+0) | 69 MB (-232) | 69 MB |
| 3 | 298 MB | 298 MB (+0) | 298 MB (+0) | 298 MB | 298 MB (+0) | 69 MB (-229) | 69 MB |

Mean peak-open delta from idle (70 MB): ~228 MB across all 3 cycles. Post-close always returns to 69 MB (-1 MB from idle; within measurement noise). **No retention ratcheting** - the allocator releases cleanly within the 3s post-close window.

## Key observation

**The +230 MB spike is already present at `SettingsView.onAppear` entry.** The flip call inside SettingsView's lifecycle hooks shows a 0 MB delta in both directions. The flip is not the proximate cause in SettingsView; it is the proximate cause upstream.

Looking at MenuBarView footer Settings button action:

```swift
footerButton(systemIcon: "gear", label: "Settings") {
    dismissMenu()
    NSApp.setActivationPolicy(.regular)   // <- real spike trigger
    NSApp.activate()
    openSettings()
}
```

The button's `setActivationPolicy(.regular)` call happens BEFORE `openSettings()`, which means by the time `SettingsView.onAppear` fires, the expensive AppKit infrastructure is already loaded. The `SettingsView.onAppear` flip is a redundant no-op because the app is already `.regular`.

Only `SettingsView.onDisappear`'s flip back to `.accessory` is load-bearing - it's what triggers the ~3s asynchronous release of the loaded infrastructure (-228 to -232 MB per cycle).

## Implications for the plan

- The hypothesis (activation-policy flip causes the spike) is **confirmed** but the proof point shifts: the flip in SettingsView is just the restore mechanism; the spike originates at the MenuBarView button's flip.
- `SETTINGS_FLIP_ACTIVATION_POLICY` must gate BOTH sites (the button's flip in MenuBarView + SettingsView's bookend calls) to properly test "no flip anywhere" in Task 3.
- Made the constant `internal` (was `private`) so MenuBarView can reference it from the same module.
- Added instrumentation to the button tap (pre-flip + post-flip footprint snapshots) so Task 2's re-run will see the spike within the log rather than having to infer it from `onAppear` entry state.
- No retention ratcheting observed. If Task 3 shows the spike disappears with the flip disabled, Phase 3A (delete the flip entirely) is the clear fix path.

## Artifacts

Raw log captured at: `/tmp/settings-condition-B.log` (3 cycles, 24 entries).
