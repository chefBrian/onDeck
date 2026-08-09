# Open issue: the flyout backdrop is opaque, not acrylic

**Status: UNRESOLVED.** Cosmetic only — the flyout is fully usable. Do not let it block Phase 7b.
Written 2026-08-08 so the next person does not repeat the dead ends below.

## Symptom

`FlyoutWindow` should have a translucent acrylic backdrop. It renders as a flat, opaque grey
rounded rectangle. Confirmed by the repo owner visually against a full-screen red test window:
**no red tint at all**, in both light and dark Windows modes.

Rounded corners **do** work, so `DwmSetWindowAttribute` is reaching the window.

## What is known for certain

- `DWMWA_WINDOW_CORNER_PREFERENCE` (33) → `S_OK`, and visibly works.
- `DWMWA_SYSTEMBACKDROP_TYPE` (38) = `DWMSBT_TRANSIENTWINDOW` (3) → **`S_OK`**, and visibly does
  nothing. Logged at every flyout open to `%LOCALAPPDATA%\onDeck\shell.log`.
- Windows 11 build **26200**, far above the 22621 floor for that attribute.
- The opaque grey is **not** the code's fallback: the fallback only paints when the HRESULT is
  non-zero, and it is zero. Something else is painting the surface.

## Already tried, did not fix it

1. `DwmExtendFrameIntoClientArea` with `-1` margins before setting the backdrop attribute.
2. `HwndSource.CompositionTarget.BackgroundColor = Colors.Transparent`.
3. `Background="Transparent"` on the `Window` and no background on the inner `Border`.
4. Re-applying the backdrop at `DispatcherPriority.Loaded`, after the theme finishes.
5. **Removing `ThemeMode="System"` from `App.xaml`.** This was briefly believed to be the fix — it
   was not. The "evidence" was a pixel sample whose coordinates had drifted onto the red test
   window rather than the flyout. `ThemeMode` has been restored; **do not** remove it again on the
   strength of that claim.

## A warning about verification method

`Graphics.CopyFromScreen` (BitBlt) was used to sample the flyout automatically. **It is not
trustworthy here** and cost several wrong conclusions:

- Hard-coded capture coordinates silently drift when the flyout moves, so samples land on whatever
  is behind it. Every "measurement" below deserves suspicion unless the saved PNG was actually
  looked at.
- BitBlt may also fail to reproduce DWM composition regardless.

If you automate this again: **save the PNG and view it**, locate the flyout rectangle in the image
first, and only then sample pixels inside it. Better still, ask a human.

## Suggested next steps

- Try `WindowChrome` with a non-zero `GlassFrameThickness` instead of `WindowStyle="None"`;
  borderless windows are a plausible reason DWM ignores the backdrop.
- Try `DWMSBT_MAINWINDOW` (2, Mica) to see whether any backdrop type composites at all — that
  isolates "this window can't have a backdrop" from "acrylic specifically is refused".
- Compare against a minimal WPF window with no tray icon and no `Topmost`, to rule out
  `Topmost`/`ShowInTaskbar="False"`/`ShowActivated` interactions.
- If none of it works, make the solid surface deliberate: pick a colour matching Win11 flyouts,
  set it unconditionally, and delete the acrylic path. The master plan always allowed the
  solid-colour fallback.
