# Resolved: the flyout backdrop is acrylic again

**Status: RESOLVED, 2026-08-09**, after three sessions. Both the flyout and the floating panel
now show live acrylic, from open, active or not. This file keeps the investigation because the
root cause is a machine/OS behaviour that could plausibly regress or recur in another form.

## Root cause

**`DWMWA_SYSTEMBACKDROP_TYPE` backdrops composite as their solid fallback colour for every
window of every app on this machine** (Windows 11 25H2, build 26200.8973) — while DWM still
composites the *accent-policy* blur (`SetWindowCompositionAttribute`, the Win10-era API) and the
system's own surfaces (taskbar) live. The app's DWM calls were always correct; the OS declined
to run the material.

Proved on 2026-08-09 by poking the live windows from outside the process and screenshotting
against a lime-green and a checkerboard backdrop window:

- `DwmGetWindowAttribute` confirmed acrylic (3) applied, `S_OK` — composited as uniform
  `#545454` with **zero** bleed of the lime green behind it. Setting Mica (2) from outside
  changed the solid to `#202020`: the visible pixels *were* the backdrop material, in fallback.
- Not activation-gated: a foreground flyout was identical to a never-activated one.
- Not window-shape-gated: a minimal WinForms control window showed the same fallback with
  `FormBorderStyle` None *and* Sizable, in a fresh process.
- Not the global toggle: transparency effects were ON (`EnableTransparency=1`), desktop PC,
  console session (not RDP).
- The same control window with `WCA_ACCENT_POLICY` / `ACCENT_ENABLE_ACRYLICBLURBEHIND`
  rendered live translucency immediately.

## Why the old symptoms looked the way they did

- **"Opaque on open, Refresh makes it translucent" (Phase 7b)** — on 2026-08-08 the machine
  still ran DWMSBT acrylic live at least some of the time; whatever kicked it (the storyboard
  was the best guess) stopped mattering when the OS stopped compositing the material at all.
  By 2026-08-09 Refresh did nothing, which is what exposed the machine-level cause.
- **The floating panel was always opaque** — consistent with (but not caused by) its
  `WS_EX_NOACTIVATE` style; the accent path composites for inactive windows, DWMSBT's policy
  engine treats them worse.
- Every earlier fix attempt targeted the app's calls. Nothing an app calls differently makes
  DWM run a backdrop it has decided to fall back.

## The fix

`DwmBackdrop.ApplyAcrylic` now sets the accent policy
(`SetWindowCompositionAttribute`, `WCA_ACCENT_POLICY`, state `ACCENT_ENABLE_BLURBEHIND`,
flags 0) instead of `DWMWA_SYSTEMBACKDROP_TYPE`. Plain blur, not the acrylic material
(state 4): acrylic lays its own dark saturating base over the blur, and that base swallowed
every tint change — 70%, 30% and 20% alpha all read as near-opaque to the owner. The tint is
`ThemePalette.BackdropTintAbgr` — the theme's `Surface` colour at **5% alpha, a whisper** —
in the accent policy's ABGR byte order, published through the palette's resource dictionary so
`App.ApplyPalette` can re-tint both windows on a live theme change via `RefreshBackdrop()`.
The darkening the owner wanted comes from an **app-side veil**: the root `Border` carries
`ThemePalette.BackdropVeil` (`Surface` at ~55% alpha, owner-tuned) over the blur — the acrylic look with the
veil's weight under our control, which the OS acrylic material never allowed (its baked-in base
swallowed every tint change between 70% and 20%). `CompositionTarget.BackgroundColor =
Transparent` is still required, and rounded corners still come from
`DWMWA_WINDOW_CORNER_PREFERENCE`. On accent failure the root `Border` falls back to the theme's
opaque `Surface` brush.

Notes for whoever touches this next:

- The accent API is undocumented but has been stable since Win10 1803 and is what the major
  WPF acrylic libraries ship. If a future Windows build breaks it, the solid fallback path
  already handles the failure code.
- WPF's Fluent theme (`ThemeMode="System"`) sets Mica (`DWMSBT_MAINWINDOW`) on windows by
  itself — `DwmGetWindowAttribute(38)` reading 2 on these windows is WPF's doing, harmless
  here because the accent wins visually, and it costs nothing while DWMSBT is fallback-only
  on this machine.
- Historical accent caveat: dragging a window with the acrylic state (4) lagged on some Win10
  builds; the plain-blur state (3) in use here never had that problem. If a future build
  renders state 3 as unfrosted glass (another historical quirk), state 4 with a low-alpha tint
  is the fallback — accepting its heavier base material.

## A warning about verification method (kept from the original)

`Graphics.CopyFromScreen` with hard-coded coordinates produced confidently wrong conclusions in
the first session. If you automate this again: take the rectangle from `GetWindowRect` on the
hwnd, **save the PNG and actually look at it**, and put a known bright pattern *behind* the
window — a uniform solid cannot distinguish heavy blur from opaque.
