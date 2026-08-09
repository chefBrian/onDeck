# Open issue: the flyout backdrop is opaque, not acrylic

**Status: UNRESOLVED**, after two sessions. Cosmetic only — both windows are fully usable.
Rewritten 2026-08-08 at the end of Phase 7b, because the original write-up's central premise
turned out to be **false** and was sending people down the wrong path.

## Symptom, precisely

- **Flyout:** opaque on open. Hitting the footer **Refresh** makes it turn translucent, and it
  stays translucent. Confirmed by the repo owner, with a screenshot showing text from the window
  behind bleeding through the top of the flyout.
- **Floating panel:** opaque **always**. Refresh never helps it.
- Rounded corners work on both, always.

The Refresh reproduction is the single most valuable clue in this document. Whatever is wrong, the
mechanism *does* work on this machine — something at open time prevents it, and a content change
after the fact fixes it.

## The premise that was wrong

The first write-up assumed the backdrop was **not being applied**, and every remedy it lists is
aimed at getting DWM to accept it. Phase 7b instrumented the actual call site and disproved that:

```
[Flyout/init] bgWas=#00FFFFFF size=300x782 visible=True hr=0x00000000 corners=0x00000000
[Flyout/show] bgWas=#00FFFFFF size=300x782 visible=True hr=0x00000000 corners=0x00000000
```

So at both init and show, **before** anything is re-applied:

- `DWMWA_SYSTEMBACKDROP_TYPE` = `DWMSBT_TRANSIENTWINDOW` → **`S_OK`**
- `DWMWA_WINDOW_CORNER_PREFERENCE` → **`S_OK`**, and visibly works
- `HwndSource.CompositionTarget.BackgroundColor` is **already `#00FFFFFF`** — fully transparent,
  not reverting to opaque as was suspected
- `DwmExtendFrameIntoClientArea(-1,-1,-1,-1)` is already being called inside `ApplyAcrylic`

**DWM accepts the backdrop and WPF's surface is already transparent, and it still renders opaque.**
A zero HRESULT is not evidence the backdrop is visible. Don't treat it as one.

## Tried and failed

From the first session:

1. `DwmExtendFrameIntoClientArea` with `-1` margins before setting the attribute. *(Still in the
   code — it is a prerequisite, not a fix.)*
2. `HwndSource.CompositionTarget.BackgroundColor = Colors.Transparent`. *(Still in the code. The
   instrumentation shows it was already transparent anyway.)*
3. `Background="Transparent"` on the `Window` with no background on the inner `Border`.
4. Re-applying the backdrop at `DispatcherPriority.Loaded`.
5. **Removing `ThemeMode="System"` from `App.xaml`.** Briefly believed to be the fix; it was not.
   The "evidence" was a pixel sample whose coordinates had drifted onto the red test window.
   **Do not remove it again on the strength of that claim.**

Added in Phase 7b — all five failed, all now reverted so the code doesn't carry dead speculation:

6. Re-applying `BackgroundColor` + both DWM attributes after `Show()` and `UpdateLayout()`, on the
   theory that `SizeToContent` rebuilds the composition target and it returns opaque. Disproved by
   the log above: it was never opaque.
7. `Root.InvalidateVisual()` to force a repaint after setting the attributes.
8. Re-applying on every `SizeChanged`.
9. `SetWindowPos(..., SWP_FRAMECHANGED)` after setting the attributes — the OS-level equivalent of
   the resize that Refresh causes. This was the best-reasoned attempt and still did nothing.
10. Setting `WS_EX_NOACTIVATE` on the panel **before** the backdrop instead of after, in case the
    ex-style change dropped the DWM attributes. *(Kept — it is more correct regardless — but it
    did not make the panel translucent.)*

## The strongest untested lead

**What does the footer Refresh do that a repaint, a resize and a frame change do not?**

It runs a **`Storyboard`** — the spinner rotation on the Refresh glyph. An active WPF animation
puts the render thread into continuous presentation, which is a different presentation path from a
static window's one-shot paint.

The floating panel corroborates this. Its refresh button is the *header* one, which Phase 7b
deliberately built **without** a spinner (see the Deviations table in
`plans/2026-08-08-phase-7b-flyout-content.md`) — and the panel is exactly the window where Refresh
never helps. Same content change, same resize, no animation, no translucency.

**Test it cheaply first:** give the floating panel's header refresh a spinner storyboard, or
attach any always-running animation to the flyout, and see whether the backdrop appears. If it does,
the fix is to keep a trivial always-running animation alive (or find whichever WPF presentation
mode it selects and select it directly).

Also still untested from the original write-up:

- `WindowChrome` with a non-zero `GlassFrameThickness` instead of `WindowStyle="None"` — a
  frameless window is a plausible reason DWM has nothing to composite into.
- `DWMSBT_MAINWINDOW` (2, Mica) to establish whether *any* backdrop type composites at all, which
  separates "this window can't have a backdrop" from "acrylic specifically is refused".
- A minimal WPF window with no tray icon, no `Topmost`, no `ShowInTaskbar="False"`, to rule out
  interactions between those.

## A warning about verification method

`Graphics.CopyFromScreen` (BitBlt) was used to sample the flyout automatically in the first
session. **It is not trustworthy here** and cost several wrong conclusions: hard-coded capture
coordinates silently drift when the flyout moves, so samples land on whatever is behind it.

If you automate this again: get the window rectangle from the OS (`GetWindowRect` on the hwnd),
**save the PNG and actually look at it**, and only then sample pixels inside it. Better still, ask
the repo owner — that is how the Refresh clue was found in the first place.

## If the next attempt also fails

Stop, and make the solid surface deliberate: pick a colour matching Win11 flyouts, set it
unconditionally, delete the acrylic path, and update `PORT_PLAN.md`'s tech-stack note. The master
plan always allowed the solid-colour fallback ("Acrylic degrades to a solid brush where DWM
refuses it"). Three sessions of chasing a cosmetic effect is more than it is worth.

Note that the current behaviour — opaque until you hit Refresh, then translucent — is *worse* than
a consistent solid surface, so this fallback is a real improvement, not a concession.
