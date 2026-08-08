# Toast activation spike — result

**Run:** 2026-08-08, Windows 11 Home 10.0.26200, .NET 10.0.302, single-file self-contained win-x64.
**Verdict: PASS.** `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 handles toast activation in an
unpackaged single-file WPF app, both with the app running and with it dead. **No Windows App SDK
fallback is needed** — Phase 9's `ToastService` can be written against this stack.

## What was tested

A hello-world single-file exe (`onDeckToastSpike.exe`, published with `PublishSingleFile` +
`IncludeNativeLibrariesForSelfExtract` + `EnableCompressionInSingleFile`) sends a toast carrying
`action=viewStream;url=https://www.mlb.com/tv/g776543` and logs every process start and activation
to `%TEMP%\ondeck-spike\activation.log`. The log file — not the window — is the evidence, because
the interesting case starts a brand new process.

## Evidence

```
19:48:39.021 pid=19140  START     wasToastActivated=False argv=[]
19:49:00.946 pid=19140  SENT      toast with argument action=viewStream;url=...
19:49:02.594 pid=19140  ACTIVATED argument="action=viewStream;url=..." parsed={action=viewStream, url=...}
19:49:32.995 pid=19140  SENT      toast with argument action=viewStream;url=...
19:49:36.006 pid=19140  EXIT      quitting so the next activation lands in a cold process
19:49:38.009 pid=21112  START     wasToastActivated=True argv=[-ToastActivated -Embedding]
19:49:38.193 pid=21112  ACTIVATED argument="action=viewStream;url=..." parsed={action=viewStream, url=...}
```

- **App running** (19:49:02): `OnActivated` fired in-process, 1.6 s after the toast was sent.
- **App dead** (19:49:38): Windows cold-started the exe with `-ToastActivated -Embedding`,
  `WasCurrentProcessToastActivated()` returned true, and `OnActivated` fired 184 ms after start.
- Arguments survived both round trips byte-for-byte and `ToastArguments.Parse` read them back.

## Findings that change later phases

1. **No Start Menu shortcut, no AUMID registry entry is created.** Activation routes purely through
   `HKCU\Software\Classes\CLSID\{clsid}\LocalServer32` = `"<exe path>" -ToastActivated`, which the
   Toolkit writes on first `Show()`. The master plan assumed shortcut/AUMID registration might be
   involved. It isn't — Phase 9 has less setup, and Phase 10's ship story doesn't need an installer
   to create a shortcut. The toast still displays the app name ("onDeckToastSpike") without one.
2. **The registered path is the published exe path.** Moving or deleting the exe leaves a dangling
   registration. Phase 10 should re-register on launch (the Toolkit does this automatically on
   `Show()`) and call `ToastNotificationManagerCompat.Uninstall()` if we ever add an uninstaller.
3. **`net10.0-windows10.0.17763.0` is required**, not the bare `net10.0-windows` that
   `OnDeck.App` currently targets — the compat APIs are only exposed on a Windows 10 TFM.
   Phase 9 must bump `OnDeck.App`'s TFM and keep `EnableWindowsTargeting` working.
4. **Activation arrives on a background thread.** The handler must marshal to the Dispatcher, which
   is also the context `AppOrchestrator` was constructed on.
5. `-Embedding` accompanies `-ToastActivated` on the cold-start command line. The single-instance
   guard (Phase 6) must not treat a toast-activation launch as a duplicate instance to be killed
   before the activation is handled.

## Re-running it

```bash
dotnet publish windows/spikes/ToastActivationSpike -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

Run the exe, click **Send toast** then the toast body (test A); click **Send toast, then quit in 3s**,
wait for exit, then click the toast (test B). **Uninstall toast registration** removes the CLSID
entry afterwards.
