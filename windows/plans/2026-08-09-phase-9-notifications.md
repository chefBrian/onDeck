# Phase 9: Notifications — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Subagents are not used on this project** (session rule) — inline execution only.

**Goal:** Replace `LoggingNotificationSink` with a real `ToastService : INotificationSink` on the stack the Phase 6 spike proved, so all five notification types fire, purge and click through on Windows the way they do on macOS.

**Architecture:** Four plain classes plus one thin platform adapter. `ToastPlanner` turns a Core call into a `ToastPlan` — title, body, tag, group, click URL, expiry — or into `null` when that type's toggle is off; it owns every string the user sees. `ToastActivation` encodes and decodes the click URL that rides in the toast's argument. `StartupPlan` decides what a given launch is *for* (shell, toast activation, test toasts, duplicate). `ToastService` wires those to `INotificationSink` and talks to an `IToastPresenter`, whose only real implementation calls `ToastContentBuilder` / `ToastNotificationManagerCompat`. Everything except that last adapter is unit-tested.

**Tech Stack:** WPF on `net10.0-windows10.0.17763.0` (a bump — see Task 1), `Microsoft.Toolkit.Uwp.Notifications` 7.1.3, xunit in `OnDeck.App.Tests`.

## Global Constraints

- **Do not add anything to `OnDeck.Core`.** `INotificationSink` is already the exact contract; Core calls it unconditionally and owns the `isStillActive` race-guard purges. Phase 9 implements the interface, it does not change it.
- `OnDeck.Core` keeps **zero** package references. Verify with `grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj` → `0`. The toast package goes on `OnDeck.App` only.
- **The per-type toggles are the sink's job.** Core calls every `Notify*` method unconditionally; each one checks its `ISettingsStore` flag and no-ops when off — mirroring `NotificationManager.swift:148,159,170,181,192`. Phase 8 already ships the five checkboxes that write those flags.
- **Kill the app before every build and test run.** From Task 1 the process is named **`onDeck`**, not `OnDeck.App`. A running instance locks `OnDeck.Core.dll`, `OnDeck.App.Tests` then silently fails to build, and `dotnet test` still prints `Passed!` for `OnDeck.Core.Tests` alone. **Always confirm TWO `Passed!` lines.**
- Single-file publish stays green:
  `dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`
- **Do not touch the DWM backdrop path**, and **do not remove `ThemeMode="System"` from `App.xaml`**. `windows/ACRYLIC-OPEN-ISSUE.md` is parked by owner decision.
- Commits go **directly to `main`**, one per task. **Never append `Co-Authored-By` or any AI-attribution trailer.**
- Commands run from the repo root (`c:\Users\brian\Code\onDeck`). A bare `dotnet test` there fails — always pass `windows/OnDeck.slnx`.
- Launching the built app locally to verify is allowed and expected. **Installing it is not.** **Don't trust automated screen capture** — ask the owner to look.

## The API, verified before this plan was written

A throwaway probe compiled against `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 on
`net10.0-windows10.0.17763.0`. These forms compile; use them exactly:

```csharp
new ToastContentBuilder()
    .AddText(title)
    .AddText(body)
    .AddArgument("url", url.AbsoluteUri)
    .AddAppLogoOverride(new Uri(imagePath), ToastGenericAppLogoCrop.Circle)
    .Show(toast =>
    {
        toast.Tag = tag;                                   // string
        toast.Group = group;                               // string
        toast.ExpirationTime = DateTimeOffset.Now + window; // DateTimeOffset?
    });

ToastNotificationManagerCompat.History.Remove(tag);
ToastNotificationManagerCompat.History.RemoveGroup(group);
ToastNotificationManagerCompat.History.Clear();

ToastArguments.Parse(argument);          // .Contains(key), indexer
ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
ToastNotificationManagerCompat.Uninstall();
```

**One trap the probe caught:** `ToastNotificationManagerCompat.OnActivated` is typed as the
library's own `OnActivated` delegate, **not** `Action<ToastNotificationActivatedEventArgsCompat>`.
A lambda converts implicitly; a variable or method group of type `Action<T>` gives
`CS0029: Cannot implicitly convert type 'System.Action<…>' to '…OnActivated'`. Subscribe with a
lambda:

```csharp
ToastNotificationManagerCompat.OnActivated += e => HandleActivation(e.Argument);
```

## Scope

**In:** the five notification types with their exact Swift copy and identifiers; tag-based purge for batting/pitching; `Group`-based purge for not-in-lineup; `ExpirationTime` as the 30 s auto-dismiss analogue; headshot images; click-through to the stream or Fantrax URL, app running *or* dead; the per-type toggles; the TFM bump and the `onDeck` assembly rename; the single-instance guard's toast-activation exception; a `--test-toast` switch so the whole path is checkable without waiting for a live at-bat.

**Out (deliberate):**
- **`requestPermission()` (`NotificationManager.swift:64-74`).** Windows has no per-app notification authorisation prompt; toasts are on unless the user disables them in Settings → Notifications. There is nothing to request and no `authorizationStatus` to gate on (`send` at `:77-81` gates on it; that guard has no Windows counterpart).
- **`NotificationDelegate.willPresent` (`:11-15`).** It exists so notifications show while the app is frontmost; Windows shows toasts regardless.
- **`removeDeliveredNotifications` on click (`:25`).** `PORT_PLAN.md` already resolved this: Windows removes an activated toast automatically.
- **`DismissalBag` (`:32-53`).** It is a hand-rolled timer pool because macOS has no expiry field. `ExpirationTime` replaces the whole class.
- **The HKCU Run key and the app/window icon** — Phase 10.

## Decision taken with the owner before writing this plan

**The assembly is renamed to `onDeck` in this phase**, not Phase 10. An unpackaged toast takes its
header from the exe, so without this every toast reads "OnDeck.App" where the Mac's reads "onDeck",
and the header is the one part of a toast no test can check. `PORT_PLAN.md` Decision 4 already
settles the identity (`onDeck`, `onDeck.exe`); this only chooses when to execute it. Consequences
carried by Task 1: the built exe becomes `onDeck.exe`, the kill command becomes
`Stop-Process -Name 'onDeck'`, and any toast COM registration written earlier against
`OnDeck.App.exe` goes stale — the Toolkit rewrites it on the next `Show()`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/OnDeck.App/OnDeck.App.csproj` | *(modify)* TFM bump, `SupportedOSPlatformVersion`, `AssemblyName`, toast package |
| `tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj` | *(modify)* same TFM, so it can still reference the app |
| `src/OnDeck.App/Notifications/ToastPlanner.cs` | `ToastPlan` + `ToastIds` + `ToastPlanner`: every user-visible string, every identifier, and the toggle gate |
| `src/OnDeck.App/Notifications/ToastActivation.cs` | The click URL's round trip through the toast argument, and the scheme allow-list |
| `src/OnDeck.App/Notifications/ToastPresenter.cs` | `IToastPresenter` + `WindowsToastPresenter` — the only file that touches the toast API |
| `src/OnDeck.App/Notifications/ToastService.cs` | `INotificationSink` over planner + presenter + `HeadshotCache` |
| `src/OnDeck.App/Notifications/LoggingNotificationSink.cs` | *(delete)* the Phase 5 stand-in it replaces |
| `src/OnDeck.App/Platform/StartupPlan.cs` | What a launch is for: shell, toast activation, test toasts, or duplicate |
| `src/OnDeck.App/App.xaml.cs` | *(modify)* the launch switch, `ToastService` in the composition root, activation → open URL |
| `tests/OnDeck.App.Tests/AppIdentityTests.cs` | The assembly name the toast header is derived from |
| `tests/OnDeck.App.Tests/ToastPlannerTests.cs` | Copy, identifiers, group, expiry, gating |
| `tests/OnDeck.App.Tests/ToastActivationTests.cs` | Round trip incl. query strings; malformed and hostile arguments |
| `tests/OnDeck.App.Tests/RecordingToastPresenter.cs` | `IToastPresenter` double with an ordered call log |
| `tests/OnDeck.App.Tests/ToastServiceTests.cs` | Every sink method's routing, suppression and purge target |
| `tests/OnDeck.App.Tests/StartupPlanTests.cs` | The launch matrix |

---

## Task 1: Target framework, assembly name, and the toast package

**Files:**
- Modify: `src/OnDeck.App/OnDeck.App.csproj`
- Modify: `tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj`
- Create: `tests/OnDeck.App.Tests/AppIdentityTests.cs`
- Modify: `windows/HANDOFF.md` (§3 commands, §4 launch path)

**Interfaces:**
- Produces: `OnDeck.App` targeting `net10.0-windows10.0.17763.0` with `AssemblyName` `onDeck` and a reference to `Microsoft.Toolkit.Uwp.Notifications` 7.1.3.
- Consumed by: every later task — none of the toast types resolve until this lands.

**Why this is its own task.** Two things change at once here, and both can break the build in ways
that look like each other: the TFM bump moves both projects, and the rename changes the output exe.
Landing them alone means a framework break can't be mistaken for a toast bug. It is also the one
task with no production code — its deliverable is that everything still builds, tests and publishes.

**Why the test project moves too.** `OnDeck.App.Tests` targets bare `net10.0-windows` and
project-references the app. A reference to a project with a *higher* platform version raises
`NETSDK1136`-class errors, so both move together.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/AppIdentityTests.cs`:

```csharp
namespace OnDeck.App.Tests;

public class AppIdentityTests
{
    [Fact]
    public void TheAssemblyIsNamedOnDeck()
    {
        // An unpackaged toast takes its header from the exe. Nothing else in the suite can see
        // that header, so this assertion stands in for it: rename the assembly and every toast
        // silently starts announcing itself as something else.
        // PORT_PLAN.md Decision 4: display name onDeck, onDeck.exe.
        var name = typeof(OnDeck.App.App).Assembly.GetName().Name;

        Assert.Equal("onDeck", name);
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App','onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~AppIdentityTests
```
Expected: FAIL — `Assert.Equal() Failure: Expected: onDeck, Actual: OnDeck.App`.

- [ ] **Step 3: Move the app project**

Replace the `PropertyGroup` and add the package in `src/OnDeck.App/OnDeck.App.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="Hardcodet.NotifyIcon.Wpf" Version="2.0.1" />
    <PackageReference Include="Microsoft.Toolkit.Uwp.Notifications" Version="7.1.3" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <!-- Microsoft.Toolkit.Uwp.Notifications' unpackaged-app compat layer (the
         ToastNotificationManagerCompat APIs) is only exposed on a Windows 10 TFM. -->
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>
    <!-- The toast header on an unpackaged app is the exe name (PORT_PLAN.md Decision 4). -->
    <AssemblyName>onDeck</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
```

Leave the existing `ProjectReference`, `Resource` and `InternalsVisibleTo` groups untouched.
`InternalsVisibleTo` still names `OnDeck.App.Tests` — that is the *test* assembly's name, which
does not change.

- [ ] **Step 4: Move the test project**

In `tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj`, change the TFM and add the platform floor:

```xml
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>
```

- [ ] **Step 5: Run the full suite and the publish check**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App','onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx 2>&1 | grep -E "Passed!|Failed!|error MSB|error NETSDK"
dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
ls windows/src/OnDeck.App/bin/Release/net10.0-windows10.0.17763.0/win-x64/publish/onDeck.exe
```
Expected: TWO `Passed!` lines; publish succeeds; `onDeck.exe` exists. Note the output path gained
the platform version — anything that hardcoded `net10.0-windows` needs updating, which is Step 6.

- [ ] **Step 6: Update the handoff's commands**

In `windows/HANDOFF.md` §3, replace the two occurrences of the kill command with the new process
name, and note the moved output path:

```bash
powershell -NoProfile -Command "Get-Process -Name 'onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
```

In §4, the launch path becomes
`windows/src/OnDeck.App/bin/Debug/net10.0-windows10.0.17763.0/onDeck.exe`.

- [ ] **Step 7: Commit**

```bash
git add windows/src/OnDeck.App/OnDeck.App.csproj windows/tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj windows/tests/OnDeck.App.Tests/AppIdentityTests.cs windows/HANDOFF.md
git commit -m "phase 9: target Windows 10 TFM and ship as onDeck.exe"
```

---

## Task 2: Toast plans — every string and identifier

**Files:**
- Create: `src/OnDeck.App/Notifications/ToastPlanner.cs`
- Create: `tests/OnDeck.App.Tests/ToastPlannerTests.cs`

**Interfaces:**
- Consumes: `OnDeck.Core.ISettingsStore`; `RecordingSettingsStore` (Phase 8) in the tests.
- Produces:
  - `sealed record ToastPlan` with `Title`, `Body`, `Tag`, `Group`, `ClickUrl`, `PlayerId`, `Expiry`
  - `static class ToastIds` with `Batting`, `Pitching`, `NotInLineup`, `NotInLineupGroup`
  - `sealed class ToastPlanner(ISettingsStore settings)` with `Batting`, `Pitching`, `AtBatResult`, `PitchingResult`, `NotInLineup`, each returning `ToastPlan?`
- Consumed by: Task 4's `ToastService`.

**Why this exists.** This class holds every word the user reads and every identifier the purge path
depends on. A typo in `"pitching-"` doesn't fail anything — it just means a stale "taking the mound"
toast never clears, which you would only notice on a live game, hours later, as a toast that won't
go away. Pinning the strings as literals in tests is the cheapest possible guard.

**The five identifiers**, from `NotificationManager.swift:129-143, 147-199` and CLAUDE.md's
stable-identifier note:

| Type | Tag | Group | Expiry |
|---|---|---|---|
| batting | `batting-<gamePk>-<playerId>` | — | — |
| pitching | `pitching-<gamePk>-<playerId>` | — | — |
| notInLineup | `notInLineup-<gamePk>-<playerId>` | `notInLineup-<gamePk>` | — |
| atBatResult | — | — | 30 s |
| pitchingResult | — | — | 30 s |

**Why the group.** `PurgeNotInLineupAsync` is game-scoped — players never in a lineup have no state
transition to hang a purge on, so the Mac sweeps every delivered id with the `notInLineup-<gamePk>-`
prefix (`:111-118`). `History.Remove` is exact-match, so the Windows equivalent is a `Group` and
`RemoveGroup`.

**Why results carry no tag.** Swift passes no identifier for them (`:169-189`), so each result is a
distinct notification and two at-bats in a row don't overwrite each other. `ExpirationTime` replaces
the `autoDismissAfter: 30` timer.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/ToastPlannerTests.cs`:

```csharp
using OnDeck.App.Notifications;

namespace OnDeck.App.Tests;

public class ToastPlannerTests
{
    private static readonly Uri Stream = new("https://www.mlb.com/tv/g776543");
    private static readonly Uri Fantrax = new("https://www.fantrax.com/fantasy/league/lg1/home");

    private static ToastPlanner Planner(RecordingSettingsStore? store = null) =>
        new(store ?? new RecordingSettingsStore());

    [Fact]
    public void BattingReadsLikeTheMacNotification()
    {
        var plan = Planner().Batting("Mookie Betts", 605141, 776543, "SF 1 - LAD 2", "Bot 3", Stream);

        Assert.NotNull(plan);
        Assert.Equal("Mookie Betts is batting", plan!.Title);
        Assert.Equal("SF 1 - LAD 2, Bot 3", plan.Body);
        Assert.Equal("batting-776543-605141", plan.Tag);
        Assert.Null(plan.Group);
        Assert.Equal(Stream, plan.ClickUrl);
        Assert.Equal(605141, plan.PlayerId);
        Assert.Null(plan.Expiry);
    }

    [Fact]
    public void PitchingReadsLikeTheMacNotification()
    {
        var plan = Planner().Pitching("Logan Webb", 657277, 776543, "SF 1 - LAD 2", "Top 4", Stream);

        Assert.NotNull(plan);
        Assert.Equal("Logan Webb is taking the mound", plan!.Title);
        Assert.Equal("SF 1 - LAD 2, Top 4", plan.Body);
        Assert.Equal("pitching-776543-657277", plan.Tag);
        Assert.Null(plan.Group);
        Assert.Null(plan.Expiry);
    }

    [Fact]
    public void NotInLineupCarriesAGameScopedGroup()
    {
        var plan = Planner().NotInLineup("Mookie Betts", 605141, 776543, "SF @ LAD", Fantrax);

        Assert.NotNull(plan);
        Assert.Equal("Mookie Betts is not in the lineup", plan!.Title);
        Assert.Equal("SF @ LAD", plan.Body);
        Assert.Equal("notInLineup-776543-605141", plan.Tag);

        // History.Remove is exact-match, so the Mac's id-prefix sweep becomes a group.
        Assert.Equal("notInLineup-776543", plan.Group);
        Assert.Equal(Fantrax, plan.ClickUrl);
    }

    [Fact]
    public void TheGroupIsThePrefixOfEveryTagInIt()
    {
        // If these ever drift apart, RemoveGroup silently stops matching and stale
        // not-in-lineup toasts survive first pitch.
        var plan = Planner().NotInLineup("Any Player", 12, 776543, "SF @ LAD", null);

        Assert.StartsWith(plan!.Group! + "-", plan.Tag);
    }

    [Fact]
    public void ResultsAreTitledWithJustThePlayerAndSelfExpire()
    {
        var atBat = Planner().AtBatResult("Mookie Betts", 605141, "Home run to left field", Stream);

        Assert.NotNull(atBat);
        Assert.Equal("Mookie Betts", atBat!.Title);
        Assert.Equal("Home run to left field", atBat.Body);
        Assert.Equal(TimeSpan.FromSeconds(30), atBat.Expiry);

        // No stable tag: two at-bats in a row must not overwrite each other.
        Assert.Null(atBat.Tag);
        Assert.Null(atBat.Group);
    }

    [Fact]
    public void PitchingResultsMatchAtBatResults()
    {
        var plan = Planner().PitchingResult(
            "Logan Webb", 657277, "Logan Webb has been pulled from the game", Stream);

        Assert.NotNull(plan);
        Assert.Equal("Logan Webb", plan!.Title);
        Assert.Equal("Logan Webb has been pulled from the game", plan.Body);
        Assert.Equal(TimeSpan.FromSeconds(30), plan.Expiry);
        Assert.Null(plan.Tag);
    }

    [Fact]
    public void EveryTypeCarriesThePlayerIdForItsHeadshot()
    {
        var planner = Planner();

        Assert.Equal(1, planner.Batting("A", 1, 9, "g", "i", null)!.PlayerId);
        Assert.Equal(2, planner.Pitching("B", 2, 9, "g", "i", null)!.PlayerId);
        Assert.Equal(3, planner.AtBatResult("C", 3, "d", null)!.PlayerId);
        Assert.Equal(4, planner.PitchingResult("D", 4, "d", null)!.PlayerId);
        Assert.Equal(5, planner.NotInLineup("E", 5, 9, "g", null)!.PlayerId);
    }

    [Fact]
    public void EachToggleSuppressesItsOwnTypeAndNoOther()
    {
        // Five near-identical guards: this is where a copy-paste slip makes one checkbox
        // silence the wrong alert.
        Assert.Null(Planner(new RecordingSettingsStore { NotifyBatting = false })
            .Batting("A", 1, 9, "g", "i", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyBatting = false })
            .Pitching("A", 1, 9, "g", "i", null));

        Assert.Null(Planner(new RecordingSettingsStore { NotifyPitching = false })
            .Pitching("A", 1, 9, "g", "i", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyPitching = false })
            .Batting("A", 1, 9, "g", "i", null));

        Assert.Null(Planner(new RecordingSettingsStore { NotifyAtBatResult = false })
            .AtBatResult("A", 1, "d", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyAtBatResult = false })
            .PitchingResult("A", 1, "d", null));

        Assert.Null(Planner(new RecordingSettingsStore { NotifyPitchingResult = false })
            .PitchingResult("A", 1, "d", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyPitchingResult = false })
            .AtBatResult("A", 1, "d", null));

        Assert.Null(Planner(new RecordingSettingsStore { NotifyNotInLineup = false })
            .NotInLineup("A", 1, 9, "g", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyNotInLineup = false })
            .Batting("A", 1, 9, "g", "i", null));
    }

    [Fact]
    public void TheTogglesAreReadAtSendTimeNotAtConstruction()
    {
        // The Settings window writes straight through to the store while the app runs.
        var store = new RecordingSettingsStore();
        var planner = new ToastPlanner(store);

        Assert.NotNull(planner.Batting("A", 1, 9, "g", "i", null));

        store.NotifyBatting = false;

        Assert.Null(planner.Batting("A", 1, 9, "g", "i", null));
    }

    [Fact]
    public void IdentifiersMatchTheDocumentedFormat()
    {
        Assert.Equal("batting-776543-605141", ToastIds.Batting(776543, 605141));
        Assert.Equal("pitching-776543-605141", ToastIds.Pitching(776543, 605141));
        Assert.Equal("notInLineup-776543-605141", ToastIds.NotInLineup(776543, 605141));
        Assert.Equal("notInLineup-776543", ToastIds.NotInLineupGroup(776543));
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ToastPlannerTests
```
Expected: build failure — `ToastPlanner`, `ToastPlan` and `ToastIds` do not exist.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Notifications/ToastPlanner.cs`:

```csharp
using OnDeck.Core;

namespace OnDeck.App.Notifications;

/// <summary>
/// One toast, as plain data. Everything the user reads and everything the purge path matches on,
/// resolved before anything Windows-specific is touched.
/// </summary>
public sealed record ToastPlan
{
    public required string Title { get; init; }

    public required string Body { get; init; }

    /// <summary>The stable identifier a purge matches. Null for results, which have none.</summary>
    public string? Tag { get; init; }

    /// <summary>Set only for not-in-lineup, whose purge is game-scoped.</summary>
    public string? Group { get; init; }

    public Uri? ClickUrl { get; init; }

    /// <summary>Drives the headshot lookup; the toast shows no image when it isn't cached.</summary>
    public int PlayerId { get; init; }

    /// <summary>Swift's <c>autoDismissAfter</c>. Null means the toast sits until dismissed.</summary>
    public TimeSpan? Expiry { get; init; }
}

/// <summary>
/// The stable notification identifiers, byte-for-byte as CLAUDE.md documents them and
/// <c>NotificationManager.swift:129-143</c> builds them. Core's purge calls match on these, so a
/// drift here shows up only as a live toast that won't clear.
/// </summary>
public static class ToastIds
{
    public static string Batting(int gamePk, int playerId) => $"batting-{gamePk}-{playerId}";

    public static string Pitching(int gamePk, int playerId) => $"pitching-{gamePk}-{playerId}";

    public static string NotInLineup(int gamePk, int playerId) =>
        $"notInLineup-{gamePk}-{playerId}";

    /// <summary>
    /// The group every not-in-lineup toast for a game shares. macOS sweeps delivered ids by
    /// prefix; <c>History.Remove</c> is exact-match, so Windows needs a real group to sweep.
    /// </summary>
    public static string NotInLineupGroup(int gamePk) => $"notInLineup-{gamePk}";
}

/// <summary>
/// Turns a Core notification call into a <see cref="ToastPlan"/>, or into <c>null</c> when that
/// type's toggle is off — the port of the <c>guard UserDefaults…</c> line that opens each of
/// <c>NotificationManager.swift:147-199</c>. Core calls the sink unconditionally; this is where
/// the user's preference is applied.
/// </summary>
public sealed class ToastPlanner(ISettingsStore settings)
{
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromSeconds(30);

    public ToastPlan? Batting(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        !settings.NotifyBatting
            ? null
            : new ToastPlan
            {
                Title = $"{playerName} is batting",
                Body = $"{game}, {inning}",
                Tag = ToastIds.Batting(gamePk, playerId),
                ClickUrl = streamUrl,
                PlayerId = playerId,
            };

    public ToastPlan? Pitching(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        !settings.NotifyPitching
            ? null
            : new ToastPlan
            {
                Title = $"{playerName} is taking the mound",
                Body = $"{game}, {inning}",
                Tag = ToastIds.Pitching(gamePk, playerId),
                ClickUrl = streamUrl,
                PlayerId = playerId,
            };

    public ToastPlan? AtBatResult(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        !settings.NotifyAtBatResult
            ? null
            : Result(playerName, playerId, description, streamUrl);

    public ToastPlan? PitchingResult(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        !settings.NotifyPitchingResult
            ? null
            : Result(playerName, playerId, description, streamUrl);

    public ToastPlan? NotInLineup(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl) =>
        !settings.NotifyNotInLineup
            ? null
            : new ToastPlan
            {
                Title = $"{playerName} is not in the lineup",
                Body = game,
                Tag = ToastIds.NotInLineup(gamePk, playerId),
                Group = ToastIds.NotInLineupGroup(gamePk),
                ClickUrl = fantraxUrl,
                PlayerId = playerId,
            };

    /// <summary>
    /// Both result types are identical but for the toggle gating them — Swift passes no
    /// identifier, so consecutive results stack instead of replacing each other.
    /// </summary>
    private static ToastPlan Result(
        string playerName, int playerId, string description, Uri? streamUrl) => new()
    {
        Title = playerName,
        Body = description,
        ClickUrl = streamUrl,
        PlayerId = playerId,
        Expiry = ResultLifetime,
    };
}
```

- [ ] **Step 4: Run and confirm the tests pass**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ToastPlannerTests
```
Expected: PASS — 10 tests.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Notifications/ToastPlanner.cs windows/tests/OnDeck.App.Tests/ToastPlannerTests.cs
git commit -m "phase 9: toast plans, identifiers and the per-type toggles"
```

---

## Task 3: The click URL's round trip

**Files:**
- Create: `src/OnDeck.App/Notifications/ToastActivation.cs`
- Create: `tests/OnDeck.App.Tests/ToastActivationTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Toolkit.Uwp.Notifications.ToastArguments`.
- Produces: `static class ToastActivation` with `const string UrlKey = "url"`, `string? Argument(Uri? url)`, `Uri? UrlFrom(string? argument)`.
- Consumed by: Task 4's presenter (writes) and Task 5's `App.xaml.cs` (reads).

**Why this exists.** `PORT_PLAN.md` is explicit that a toast click opens the link, not the app —
the stream for batting/pitching/results, the Fantrax page for not-in-lineup. The URL survives as a
string inside the toast's argument, through the Action Center, possibly across a process restart,
and comes back hours later. Two things can silently break it: a URL with a query string colliding
with the argument encoding, and an argument that comes back as something we never wrote.

**The scheme allow-list is deliberate.** `ExternalLink.Open` uses `ShellExecute`, which will happily
launch any registered protocol handler. The argument arrives from outside the process, so
`UrlFrom` returns a URL only for `http`/`https`. We only ever write those two, so nothing legitimate
is lost.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/ToastActivationTests.cs`:

```csharp
using OnDeck.App.Notifications;

namespace OnDeck.App.Tests;

public class ToastActivationTests
{
    [Fact]
    public void AUrlSurvivesTheRoundTrip()
    {
        var url = new Uri("https://www.mlb.com/tv/g776543");

        Assert.Equal(url, ToastActivation.UrlFrom(ToastActivation.Argument(url)));
    }

    [Fact]
    public void AUrlWithAQueryStringSurvivesTheRoundTrip()
    {
        // The argument format is itself key=value pairs joined by ';'. A stream link carrying
        // its own '=' and '&' is exactly the case that breaks a naive encoding, and the toast
        // is delivered hours before anyone clicks it.
        var url = new Uri("https://www.espn.com/watch?id=abc123&lang=en;x=1");

        Assert.Equal(url, ToastActivation.UrlFrom(ToastActivation.Argument(url)));
    }

    [Fact]
    public void NoUrlMeansNoArgument()
    {
        Assert.Null(ToastActivation.Argument(null));
    }

    [Fact]
    public void AnArgumentWithoutAUrlYieldsNothingToOpen()
    {
        Assert.Null(ToastActivation.UrlFrom("action=viewStream"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("=")]
    [InlineData("url=")]
    [InlineData("url=not a url")]
    [InlineData("url=/relative/path")]
    public void AnUnusableArgumentYieldsNothingToOpen(string? argument)
    {
        Assert.Null(ToastActivation.UrlFrom(argument));
    }

    [Theory]
    [InlineData("url=file:///C:/Windows/System32/calc.exe")]
    [InlineData("url=ms-settings:windowsupdate")]
    [InlineData("url=javascript:alert(1)")]
    public void OnlyWebSchemesAreFollowed(string argument)
    {
        // The argument comes back from outside the process and ends up at ShellExecute, which
        // launches any registered protocol handler. We only ever write http(s).
        Assert.Null(ToastActivation.UrlFrom(argument));
    }

    [Fact]
    public void PlainHttpIsFollowed()
    {
        var url = new Uri("http://example.com/game");

        Assert.Equal(url, ToastActivation.UrlFrom(ToastActivation.Argument(url)));
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ToastActivationTests
```
Expected: build failure — `ToastActivation` does not exist.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Notifications/ToastActivation.cs`:

```csharp
using Microsoft.Toolkit.Uwp.Notifications;

namespace OnDeck.App.Notifications;

/// <summary>
/// The click URL's round trip through a toast's argument string. <c>PORT_PLAN.md</c>: a toast
/// click opens its link — the stream for batting/pitching/results, the Fantrax page for
/// not-in-lineup — rather than merely foregrounding the app.
/// </summary>
public static class ToastActivation
{
    public const string UrlKey = "url";

    /// <summary>The argument to attach to a toast, or null when there is nothing to open.</summary>
    public static string? Argument(Uri? url) =>
        url is null ? null : new ToastArguments().Add(UrlKey, url.AbsoluteUri).ToString();

    /// <summary>
    /// The URL to open for an activation, or null if there isn't a usable one. Never throws: this
    /// runs on an OS callback, and a malformed argument must not take the process down.
    /// </summary>
    public static Uri? UrlFrom(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return null;

        try
        {
            var arguments = ToastArguments.Parse(argument);
            if (!arguments.Contains(UrlKey)) return null;
            if (!Uri.TryCreate(arguments[UrlKey], UriKind.Absolute, out var url)) return null;

            // This string arrives from outside the process and ends up at ShellExecute, which
            // launches any registered protocol handler. We only ever write http(s).
            return url.Scheme is "http" or "https" ? url : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run and confirm the tests pass**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ToastActivationTests
```
Expected: PASS — 15 cases.

If the query-string round trip fails, **do not** hand-roll escaping: `ToastArguments` is the
encoder the `ToastContentBuilder.AddArgument` path uses, so the fix belongs in how the value is
handed to it, not in a parallel encoder.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Notifications/ToastActivation.cs windows/tests/OnDeck.App.Tests/ToastActivationTests.cs
git commit -m "phase 9: toast click-through arguments"
```

---

## Task 4: ToastService over a presenter seam

**Files:**
- Create: `src/OnDeck.App/Notifications/ToastPresenter.cs`
- Create: `src/OnDeck.App/Notifications/ToastService.cs`
- Create: `tests/OnDeck.App.Tests/RecordingToastPresenter.cs`
- Create: `tests/OnDeck.App.Tests/ToastServiceTests.cs`
- Delete: `src/OnDeck.App/Notifications/LoggingNotificationSink.cs`

**Interfaces:**
- Consumes: `ToastPlanner`, `ToastPlan`, `ToastIds` (Task 2); `ToastActivation.Argument` (Task 3); `OnDeck.Core.INotificationSink`, `OnDeck.Core.ISettingsStore`, `OnDeck.Core.Utilities.HeadshotCache`.
- Produces:
  - `interface IToastPresenter` with `void Show(ToastPlan plan, string? imagePath)`, `void Remove(string tag)`, `void RemoveGroup(string group)`, `void Clear()`
  - `sealed class WindowsToastPresenter : IToastPresenter`
  - `sealed class ToastService(ISettingsStore settings, HeadshotCache headshots, IToastPresenter presenter) : INotificationSink`
- Consumed by: Task 5's composition root.

**Why the seam.** `ToastNotificationManagerCompat` is static and needs a real Windows notification
platform, so nothing that calls it can be unit-tested. Putting one interface in front of it moves
the whole of `ToastService` — which method purges which tag, which toggle suppresses which type,
whether the headshot is looked up — onto the tested side, and leaves an adapter with no branching
in it. That is the same trade `TeamLogoStore` made in 7b.

**Purges are not gated by toggles.** Swift doesn't gate them either: a toast shown before the user
turned a type off must still be purgeable. Only the `Notify*` methods consult the store.

- [ ] **Step 1: Write the test double**

Create `tests/OnDeck.App.Tests/RecordingToastPresenter.cs`:

```csharp
using OnDeck.App.Notifications;

namespace OnDeck.App.Tests;

/// <summary>
/// An <see cref="IToastPresenter"/> that records what it was asked to do, in order. The real one
/// talks to a static Windows API that cannot run in a test.
/// </summary>
public sealed class RecordingToastPresenter : IToastPresenter
{
    public List<(ToastPlan Plan, string? ImagePath)> Shown { get; } = [];

    public List<string> Removed { get; } = [];

    public List<string> RemovedGroups { get; } = [];

    public int Cleared { get; private set; }

    public ToastPlan LastShown => Shown[^1].Plan;

    public void Show(ToastPlan plan, string? imagePath) => Shown.Add((plan, imagePath));

    public void Remove(string tag) => Removed.Add(tag);

    public void RemoveGroup(string group) => RemovedGroups.Add(group);

    public void Clear() => Cleared++;
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/OnDeck.App.Tests/ToastServiceTests.cs`:

```csharp
using System.IO;
using System.Net.Http;
using OnDeck.App.Notifications;
using OnDeck.Core.Utilities;

namespace OnDeck.App.Tests;

public class ToastServiceTests : IDisposable
{
    private static readonly Uri Stream = new("https://www.mlb.com/tv/g776543");

    private readonly string _headshotDirectory = Path.Combine(
        Path.GetTempPath(), "ondeck-headshot-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingToastPresenter _presenter = new();
    private readonly RecordingSettingsStore _settings = new();

    public void Dispose()
    {
        if (Directory.Exists(_headshotDirectory))
        {
            Directory.Delete(_headshotDirectory, recursive: true);
        }
    }

    private ToastService Service() =>
        new(_settings, new HeadshotCache(new HttpClient(), _headshotDirectory), _presenter);

    private string WriteHeadshot(int playerId)
    {
        Directory.CreateDirectory(_headshotDirectory);
        var path = Path.Combine(_headshotDirectory, $"{playerId}.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    [Fact]
    public async Task BattingIsShownWithItsPlan()
    {
        await Service().NotifyBattingAsync(
            "Mookie Betts", 605141, 776543, "SF 1 - LAD 2", "Bot 3", Stream);

        Assert.Single(_presenter.Shown);
        Assert.Equal("Mookie Betts is batting", _presenter.LastShown.Title);
        Assert.Equal("batting-776543-605141", _presenter.LastShown.Tag);
    }

    [Fact]
    public async Task EveryTypeReachesThePresenterWhenItsToggleIsOn()
    {
        var service = Service();

        await service.NotifyBattingAsync("A", 1, 9, "g", "i", null);
        await service.NotifyPitchingAsync("B", 2, 9, "g", "i", null);
        await service.NotifyAtBatResultAsync("C", 3, "d", null);
        await service.NotifyPitchingResultAsync("D", 4, "d", null);
        await service.NotifyNotInLineupAsync("E", 5, 9, "g", null);

        Assert.Equal(5, _presenter.Shown.Count);
    }

    [Fact]
    public async Task ADisabledToggleShowsNothingAtAll()
    {
        _settings.NotifyBatting = false;

        await Service().NotifyBattingAsync("A", 1, 9, "g", "i", null);

        Assert.Empty(_presenter.Shown);
    }

    [Fact]
    public async Task ACachedHeadshotIsPassedAlong()
    {
        var path = WriteHeadshot(605141);

        await Service().NotifyBattingAsync("Mookie Betts", 605141, 776543, "g", "i", null);

        Assert.Equal(path, _presenter.Shown[0].ImagePath);
    }

    [Fact]
    public async Task AMissingHeadshotIsNotAnError()
    {
        await Service().NotifyBattingAsync("Mookie Betts", 605141, 776543, "g", "i", null);

        Assert.Single(_presenter.Shown);
        Assert.Null(_presenter.Shown[0].ImagePath);
    }

    [Fact]
    public void PurgingBattingRemovesItsTag()
    {
        Service().PurgeBatting(776543, 605141);

        Assert.Equal(new[] { "batting-776543-605141" }, _presenter.Removed);
        Assert.Empty(_presenter.RemovedGroups);
    }

    [Fact]
    public void PurgingPitchingRemovesItsTag()
    {
        Service().PurgePitching(776543, 657277);

        Assert.Equal(new[] { "pitching-776543-657277" }, _presenter.Removed);
    }

    [Fact]
    public async Task PurgingNotInLineupRemovesTheWholeGroup()
    {
        // Game-scoped: players never in the lineup have no transition to hang a per-player
        // purge on, so this has to sweep the group rather than a tag.
        await Service().PurgeNotInLineupAsync(776543);

        Assert.Equal(new[] { "notInLineup-776543" }, _presenter.RemovedGroups);
        Assert.Empty(_presenter.Removed);
    }

    [Fact]
    public async Task PurgingEverythingClearsTheHistory()
    {
        await Service().PurgeAllAsync();

        Assert.Equal(1, _presenter.Cleared);
    }

    [Fact]
    public async Task PurgesAreNotGatedByTheToggles()
    {
        // A toast shown before the user turned its type off must still be removable.
        _settings.NotifyBatting = false;
        _settings.NotifyNotInLineup = false;
        var service = Service();

        service.PurgeBatting(776543, 605141);
        await service.PurgeNotInLineupAsync(776543);
        await service.PurgeAllAsync();

        Assert.Single(_presenter.Removed);
        Assert.Single(_presenter.RemovedGroups);
        Assert.Equal(1, _presenter.Cleared);
    }
}
```

- [ ] **Step 3: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ToastServiceTests
```
Expected: build failure — `IToastPresenter` and `ToastService` do not exist.

- [ ] **Step 4: Implement the presenter**

Create `src/OnDeck.App/Notifications/ToastPresenter.cs`:

```csharp
using Microsoft.Toolkit.Uwp.Notifications;
using OnDeck.App.Platform;

namespace OnDeck.App.Notifications;

/// <summary>
/// The edge between a decided <see cref="ToastPlan"/> and Windows. It exists so
/// <see cref="ToastService"/> can be tested: <c>ToastNotificationManagerCompat</c> is static and
/// needs a live notification platform, so nothing calling it directly can run in a test.
/// </summary>
public interface IToastPresenter
{
    void Show(ToastPlan plan, string? imagePath);

    void Remove(string tag);

    void RemoveGroup(string group);

    void Clear();
}

/// <summary>
/// The real presenter. Deliberately branch-free beyond "is this field set" — every decision was
/// already made by <see cref="ToastPlanner"/>.
/// <para>
/// Every method swallows its exceptions. The toast API throws when the notification platform is
/// unavailable or COM registration fails, and a missed notification must never take down the poll
/// cycle that produced it — Core wraps sink calls in its own guard, but the purge methods are
/// called synchronously from the transition path.
/// </para>
/// </summary>
public sealed class WindowsToastPresenter : IToastPresenter
{
    public void Show(ToastPlan plan, string? imagePath)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(plan.Title)
                .AddText(plan.Body);

            if (plan.ClickUrl is { } url)
            {
                builder.AddArgument(ToastActivation.UrlKey, url.AbsoluteUri);
            }

            if (imagePath is not null)
            {
                // The circle crop is what Windows uses for a person; a headshot in a square
                // frame reads as a screenshot.
                builder.AddAppLogoOverride(new Uri(imagePath), ToastGenericAppLogoCrop.Circle);
            }

            builder.Show(toast =>
            {
                if (plan.Tag is { } tag) toast.Tag = tag;
                if (plan.Group is { } group) toast.Group = group;
                if (plan.Expiry is { } window) toast.ExpirationTime = DateTimeOffset.Now + window;
            });
        }
        catch (Exception exception)
        {
            ShellLog.Append($"[Toast] show failed for \"{plan.Title}\": {exception.Message}");
        }
    }

    public void Remove(string tag) =>
        Guarded(() => ToastNotificationManagerCompat.History.Remove(tag), $"remove {tag}");

    public void RemoveGroup(string group) =>
        Guarded(
            () => ToastNotificationManagerCompat.History.RemoveGroup(group),
            $"remove group {group}");

    public void Clear() =>
        Guarded(() => ToastNotificationManagerCompat.History.Clear(), "clear");

    private static void Guarded(Action work, string description)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            ShellLog.Append($"[Toast] {description} failed: {exception.Message}");
        }
    }
}
```

- [ ] **Step 5: Implement the service**

Create `src/OnDeck.App/Notifications/ToastService.cs`:

```csharp
using OnDeck.Core;
using OnDeck.Core.Utilities;

namespace OnDeck.App.Notifications;

/// <summary>
/// The Windows implementation of <see cref="INotificationSink"/> — the port of
/// <c>Notifications/NotificationManager.swift</c>. Core calls every method unconditionally and
/// owns the race-guard purges; this decides what a toast says, whether the user wants it, and
/// which identifier it carries.
/// <para>
/// <c>requestPermission</c> has no counterpart: Windows has no per-app notification authorisation
/// to ask for, so there is no <c>authorizationStatus</c> to gate sends on.
/// </para>
/// </summary>
public sealed class ToastService(
    ISettingsStore settings, HeadshotCache headshots, IToastPresenter presenter)
    : INotificationSink
{
    private readonly ToastPlanner _planner = new(settings);

    public Task NotifyBattingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        Present(_planner.Batting(playerName, playerId, gamePk, game, inning, streamUrl));

    public Task NotifyPitchingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        Present(_planner.Pitching(playerName, playerId, gamePk, game, inning, streamUrl));

    public Task NotifyAtBatResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        Present(_planner.AtBatResult(playerName, playerId, description, streamUrl));

    public Task NotifyPitchingResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        Present(_planner.PitchingResult(playerName, playerId, description, streamUrl));

    public Task NotifyNotInLineupAsync(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl) =>
        Present(_planner.NotInLineup(playerName, playerId, gamePk, game, fantraxUrl));

    // Purges are deliberately ungated: a toast shown before the user turned its type off must
    // still be removable. Swift doesn't gate them either.
    public void PurgeBatting(int gamePk, int playerId) =>
        presenter.Remove(ToastIds.Batting(gamePk, playerId));

    public void PurgePitching(int gamePk, int playerId) =>
        presenter.Remove(ToastIds.Pitching(gamePk, playerId));

    public Task PurgeNotInLineupAsync(int gamePk)
    {
        presenter.RemoveGroup(ToastIds.NotInLineupGroup(gamePk));
        return Task.CompletedTask;
    }

    public Task PurgeAllAsync()
    {
        presenter.Clear();
        return Task.CompletedTask;
    }

    /// <summary>A null plan means the user has that notification type switched off.</summary>
    private Task Present(ToastPlan? plan)
    {
        if (plan is null) return Task.CompletedTask;

        presenter.Show(plan, headshots.FilePath(plan.PlayerId));
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 6: Delete the stand-in**

```bash
git rm windows/src/OnDeck.App/Notifications/LoggingNotificationSink.cs
```

This breaks `App.xaml.cs`, which still constructs it — Task 5 replaces that line. To keep this task
independently green, make the one-line substitution now:

```csharp
            new ToastService(settings, headshots, new WindowsToastPresenter()));
```

in place of `new LoggingNotificationSink());`.

- [ ] **Step 7: Run and confirm the tests pass**

```bash
dotnet test windows/OnDeck.slnx 2>&1 | grep -E "Passed!|Failed!|error MSB|error CS"
```
Expected: TWO `Passed!` lines; the 10 new `ToastServiceTests` among them.

- [ ] **Step 8: Commit**

```bash
git add windows/src/OnDeck.App/Notifications windows/src/OnDeck.App/App.xaml.cs windows/tests/OnDeck.App.Tests/ToastServiceTests.cs windows/tests/OnDeck.App.Tests/RecordingToastPresenter.cs
git commit -m "phase 9: toast service over a testable presenter seam"
```

---

## Task 5: What a launch is for

**Files:**
- Create: `src/OnDeck.App/Platform/StartupPlan.cs`
- Create: `tests/OnDeck.App.Tests/StartupPlanTests.cs`
- Modify: `src/OnDeck.App/App.xaml.cs`

**Interfaces:**
- Consumes: `ToastActivation.UrlFrom` (Task 3); `ToastService` (Task 4); `SingleInstance`; `ExternalLink`.
- Produces: `enum LaunchAction { RunShell, HandleToastActivationAndExit, SendTestToastsAndExit, SignalExistingAndExit }` and `static class StartupPlan` with `TestToastSwitch`, `WantsTestToasts(IEnumerable<string>)`, `Decide(bool, bool, bool)`.

**Why this exists.** `App.OnStartup` currently has one rule: if the mutex is taken, signal and quit.
The spike's finding 5 says that is now wrong — a `-ToastActivated` launch must not be killed before
its activation is handled, or the click silently does nothing. Adding a second and third case to an
`if` inside `OnStartup` puts branching in the one file no test can reach, so the decision moves out
and the method becomes a switch.

**Why `--test-toast`.** Every other Phase 9 behaviour needs a live at-bat to observe, and the parts
a human must judge — how the toast looks, whether the headshot renders, whether the click opens the
stream, whether it clears from the Action Center — are exactly the parts no test covers. The switch
sends one of each type and exits without building a shell or touching the mutex, so it works whether
or not the app is running. It respects the toggles, because "does the checkbox actually silence it"
is one of the things worth checking.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/StartupPlanTests.cs`:

```csharp
using OnDeck.App.Platform;

namespace OnDeck.App.Tests;

public class StartupPlanTests
{
    [Fact]
    public void AnOrdinaryFirstLaunchRunsTheShell()
    {
        var action = StartupPlan.Decide(
            acquiredMutex: true, wasToastActivated: false, wantsTestToasts: false);

        Assert.Equal(LaunchAction.RunShell, action);
    }

    [Fact]
    public void AColdToastActivationRunsTheShell()
    {
        // The app was dead, Windows started it with -ToastActivated -Embedding. It should
        // become the app - the activation arrives a beat later and is handled in-process.
        var action = StartupPlan.Decide(
            acquiredMutex: true, wasToastActivated: true, wantsTestToasts: false);

        Assert.Equal(LaunchAction.RunShell, action);
    }

    [Fact]
    public void ASecondLaunchSignalsTheLiveInstance()
    {
        var action = StartupPlan.Decide(
            acquiredMutex: false, wasToastActivated: false, wantsTestToasts: false);

        Assert.Equal(LaunchAction.SignalExistingAndExit, action);
    }

    [Fact]
    public void AToastActivationThatRacesTheLiveInstanceIsHandledNotKilled()
    {
        // Spike finding 5. Shutting this down before the activation is delivered loses the
        // click, and the user just sees a toast that did nothing.
        var action = StartupPlan.Decide(
            acquiredMutex: false, wasToastActivated: true, wantsTestToasts: false);

        Assert.Equal(LaunchAction.HandleToastActivationAndExit, action);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TestToastsOutrankEverythingElse(bool acquiredMutex, bool wasToastActivated)
    {
        // It has to work while the app is running, which is the normal way to use it.
        var action = StartupPlan.Decide(acquiredMutex, wasToastActivated, wantsTestToasts: true);

        Assert.Equal(LaunchAction.SendTestToastsAndExit, action);
    }

    [Fact]
    public void TheTestToastSwitchIsRecognised()
    {
        Assert.True(StartupPlan.WantsTestToasts(["--test-toast"]));
        Assert.True(StartupPlan.WantsTestToasts(["--Test-Toast"]));
        Assert.True(StartupPlan.WantsTestToasts(["-ToastActivated", "--test-toast"]));
    }

    [Fact]
    public void OtherArgumentsAreNotTheTestSwitch()
    {
        Assert.False(StartupPlan.WantsTestToasts([]));
        Assert.False(StartupPlan.WantsTestToasts(["-ToastActivated", "-Embedding"]));
        Assert.False(StartupPlan.WantsTestToasts(["--test-toasts"]));
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~StartupPlanTests
```
Expected: build failure — `StartupPlan` does not exist.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Platform/StartupPlan.cs`:

```csharp
namespace OnDeck.App.Platform;

public enum LaunchAction
{
    /// <summary>Build the tray icon, windows and engine — the normal launch.</summary>
    RunShell,

    /// <summary>
    /// A toast activation that arrived while another instance holds the mutex. Handle the click
    /// and go away without building a second tray icon.
    /// </summary>
    HandleToastActivationAndExit,

    /// <summary>Send one of each notification type so a human can look at them, then exit.</summary>
    SendTestToastsAndExit,

    /// <summary>A duplicate launch: wake the live instance's flyout and exit.</summary>
    SignalExistingAndExit,
}

/// <summary>
/// What a given launch is for. This used to be a single <c>if</c> inside <c>App.OnStartup</c>;
/// toast activation added a case that must not be killed as a duplicate
/// (<c>spikes/ToastActivationSpike/FINDINGS.md</c>, finding 5), and branching inside
/// <c>OnStartup</c> is unreachable from a test.
/// </summary>
public static class StartupPlan
{
    public const string TestToastSwitch = "--test-toast";

    public static bool WantsTestToasts(IEnumerable<string> arguments) =>
        arguments.Any(argument =>
            string.Equals(argument, TestToastSwitch, StringComparison.OrdinalIgnoreCase));

    public static LaunchAction Decide(
        bool acquiredMutex, bool wasToastActivated, bool wantsTestToasts)
    {
        // Diagnostics first: it has to work whether or not the app is already running, which is
        // the normal way to use it.
        if (wantsTestToasts) return LaunchAction.SendTestToastsAndExit;

        if (acquiredMutex) return LaunchAction.RunShell;

        return wasToastActivated
            ? LaunchAction.HandleToastActivationAndExit
            : LaunchAction.SignalExistingAndExit;
    }
}
```

- [ ] **Step 4: Run and confirm the tests pass**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~StartupPlanTests
```
Expected: PASS — 11 cases.

- [ ] **Step 5: Wire it into the composition root**

In `src/OnDeck.App/App.xaml.cs`, add the usings:

```csharp
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
```

Add the fields beside the others:

```csharp
    private ToastService? _toasts;
    private bool _exitAfterActivation;
```

Replace the opening of `OnStartup` — everything from the `if (!SingleInstance.TryAcquire(...))`
block up to and including `_singleInstance!.SecondInstanceStarted += …` — with:

```csharp
        // Wired before anything else: a toast-activated cold start fires this within ~200 ms
        // of startup (spike FINDINGS.md). The event takes the library's own OnActivated
        // delegate, so this must be a lambda - an Action<T> variable will not convert. The
        // parameter cannot be named `e`: OnStartup's own StartupEventArgs already owns that
        // name in the enclosing scope, and C# rejects the shadow (CS0136).
        ToastNotificationManagerCompat.OnActivated += activation =>
            OnToastActivated(activation.Argument);

        var acquiredMutex = SingleInstance.TryAcquire(out var instance);
        var action = StartupPlan.Decide(
            acquiredMutex,
            ToastNotificationManagerCompat.WasCurrentProcessToastActivated(),
            StartupPlan.WantsTestToasts(e.Args));

        if (action != LaunchAction.RunShell) instance?.Dispose();

        switch (action)
        {
            case LaunchAction.SendTestToastsAndExit:
                SendTestToasts();
                Shutdown();
                return;

            case LaunchAction.HandleToastActivationAndExit:
                // The handler above opens the link and shuts us down. This is the safety net
                // for an activation that never arrives - without it the process would linger
                // with no window and no tray icon.
                _exitAfterActivation = true;
                ExitAfter(TimeSpan.FromSeconds(5));
                return;

            case LaunchAction.SignalExistingAndExit:
                // Hand the click to the live instance instead of adding a second tray icon.
                SingleInstance.SignalExistingInstance();
                Shutdown();
                return;
        }

        _singleInstance = instance;
        _singleInstance!.SecondInstanceStarted += () => Dispatcher.Invoke(() => OpenFlyout(null));
```

Replace the sink passed to the orchestrator, and keep the service for `SendTestToasts`:

```csharp
        _toasts = new ToastService(settings, headshots, new WindowsToastPresenter());

        _orchestrator = new AppOrchestrator(
            new RosterManager(fantrax, mlb, settings, headshots),
            new ScheduleManager(mlb),
            new GameMonitor(mlb),
            new StateManager(),
            fantrax,
            settings,
            _toasts);
```

Add the three methods beside `OpenSettings`:

```csharp
    /// <summary>
    /// A toast was clicked. Fires on a background thread, so everything here hops to the
    /// Dispatcher — the context <c>AppOrchestrator</c> was constructed on.
    /// </summary>
    private void OnToastActivated(string argument)
    {
        var url = ToastActivation.UrlFrom(argument);
        ShellLog.Append($"[Toast] activated argument=\"{argument}\" url={url?.AbsoluteUri ?? "(none)"}");

        Dispatcher.Invoke(() =>
        {
            if (url is not null) ExternalLink.Open(url);

            // This process exists only to service the click.
            if (_exitAfterActivation) Shutdown();
        });
    }

    /// <summary>
    /// <c>--test-toast</c>: one of each type, so the look, the headshot, the click-through and
    /// the Action Center behaviour can be checked without waiting for a live at-bat. Toggles are
    /// respected — whether a checkbox actually silences its type is one of the things to check.
    /// </summary>
    private static void SendTestToasts()
    {
        var settings = new SettingsStore();
        var http = new HttpClient();
        var headshots = new HeadshotCache(http, HeadshotCache.DefaultCacheDirectory());
        var service = new ToastService(settings, headshots, new WindowsToastPresenter());

        // Mookie Betts - a headshot is likely already cached from a roster sync.
        const int playerId = 605141;
        const int gamePk = 776543;
        var stream = new Uri("https://www.mlb.com/tv/g776543");

        service.NotifyBattingAsync(
            "Mookie Betts", playerId, gamePk, "SF 1 - LAD 2", "Bot 3", stream).Wait();
        service.NotifyPitchingAsync(
            "Logan Webb", 657277, gamePk, "SF 1 - LAD 2", "Top 4", stream).Wait();
        service.NotifyAtBatResultAsync(
            "Mookie Betts", playerId, "Home run to left field", stream).Wait();
        service.NotifyPitchingResultAsync(
            "Logan Webb", 657277, "Logan Webb has been pulled from the game", stream).Wait();
        service.NotifyNotInLineupAsync(
            "Freddie Freeman", 518692, gamePk, "SF @ LAD",
            new Uri("https://www.fantrax.com/fantasy/league/lg1/home")).Wait();

        ShellLog.Append("[Toast] sent the --test-toast set");
    }

    private void ExitAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Shutdown();
        };
        timer.Start();
    }
```

`SendTestToasts` needs `using OnDeck.Core.Utilities;` and `using System.Net.Http;`, both of which
`App.xaml.cs` already has.

- [ ] **Step 6: Run the full suite**

```bash
powershell -NoProfile -Command "Get-Process -Name 'onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx 2>&1 | grep -E "Passed!|Failed!|error MSB|error CS"
```
Expected: TWO `Passed!` lines.

- [ ] **Step 7: Commit**

```bash
git add windows/src/OnDeck.App/Platform/StartupPlan.cs windows/src/OnDeck.App/App.xaml.cs windows/tests/OnDeck.App.Tests/StartupPlanTests.cs
git commit -m "phase 9: launch routing for toast activation and test toasts"
```

---

## Task 6: Verification and close-out

**Files:**
- Modify: `windows/plans/2026-08-09-phase-9-notifications.md` (the Deviations section below)
- Modify: `windows/HANDOFF.md` (§5 tree, §8 deviations, §8b verification table, a §8d Phase 9 status, §9 → Phase 10, §10)

- [ ] **Step 1: Run every automated gate**

```bash
powershell -NoProfile -Command "Get-Process -Name 'onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"

dotnet test windows/OnDeck.slnx 2>&1 | grep -E "Passed!|Failed!|error MSB"
grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj

dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true

git status --short
```
Expected: TWO `Passed!` lines with `Failed: 0`; `0` package references in Core; publish succeeds;
working tree clean.

- [ ] **Step 2: Send the test set and look at it**

```bash
powershell -NoProfile -Command "Get-Process -Name 'onDeck' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet build windows/OnDeck.slnx
windows/src/OnDeck.App/bin/Debug/net10.0-windows10.0.17763.0/onDeck.exe --test-toast
```

Then check `%LOCALAPPDATA%\onDeck\shell.log` for the `[Toast] sent the --test-toast set` line, and
have the owner judge:

| Check | Where it comes from |
|---|---|
| The toast header reads **onDeck**, not OnDeck.App | Task 1's rename |
| Five toasts: batting, pitching, at-bat result, pitching result, not in lineup | `NotificationManager.swift:147-199` |
| Copy matches — "X is batting", "X is taking the mound", "X is not in the lineup"; results titled with just the name | same |
| A cached headshot renders, circle-cropped | `:90-94` |
| Clicking one opens the stream URL in the browser | `PORT_PLAN.md` toast row |
| The two result toasts leave the Action Center after ~30 s; the other three stay | `autoDismissAfter: 30` |
| Turning a toggle off in Settings and re-running suppresses exactly that one | `:148,159,170,181,192` |

- [ ] **Step 3: Verify click-through with the app dead**

Send the set, quit the app, then click a toast in the Action Center. Windows should cold-start
`onDeck.exe` and open the link. Confirm in `shell.log` that a `[Toast] activated` line appears and
that **one** tray icon is present afterwards, not two.

- [ ] **Step 4: The live-game checks**

These need a real game and cannot be forced; record them in HANDOFF §8b as pending if there isn't
one to hand, exactly as 7b did for the floating panel:

- batting/pitching toasts fire at the same moments the Mac's do
- a stale toast is purged when the player's state changes (`handleStateTransition`)
- not-in-lineup toasts for a game disappear when that game goes live (`PurgeNotInLineupAsync`)
- every toast clears on a schedule refresh / day rollover (`PurgeAllAsync`)

- [ ] **Step 5: Record the deviations and update the handoff**

Fill in the Deviations section below, then append the Phase 9 rows to `HANDOFF.md` §8, add the
manual results to §8b, add a §8d Phase 9 status, and rewrite §9 to brief **Phase 10** — system
integration and ship.

- [ ] **Step 6: Commit**

```bash
git add windows/plans/2026-08-09-phase-9-notifications.md windows/HANDOFF.md
git commit -m "phase 9: verification results and phase 10 handoff"
```

---

## Deviations from the Swift original

*(Filled in during execution. The entries below are the ones this plan commits to up front.)*

| Deviation | Why |
|---|---|
| `requestPermission()` not ported | Windows has no per-app notification authorisation prompt, so there is nothing to request and no `authorizationStatus` for `send` to gate on |
| `NotificationDelegate.willPresent` not ported | It exists to show notifications while the app is frontmost; Windows shows toasts regardless |
| `removeDeliveredNotifications` on click not ported | Already resolved in `PORT_PLAN.md`: Windows removes an activated toast automatically |
| `DismissalBag` not ported; `ExpirationTime` replaces it | It is a hand-rolled timer pool standing in for an expiry field macOS lacks. Windows has the field |
| Not-in-lineup toasts carry a `Group`; macOS sweeps ids by prefix | `History.Remove` is exact-match, so a game-scoped purge needs a real group and `RemoveGroup` |
| A toast's click URL is restricted to `http`/`https` | The argument arrives from outside the process and reaches `ShellExecute`, which launches any registered protocol handler. We only ever write those two schemes |
| The assembly is renamed to `onDeck` in this phase | An unpackaged toast takes its header from the exe. `PORT_PLAN.md` Decision 4 already settles the identity; this only chooses when. Owner-confirmed |
| `--test-toast` has no macOS counterpart | Every other behaviour here needs a live at-bat to observe, and the parts a human must judge are the parts no test covers |
| `IToastPresenter` sits between `ToastService` and the toast API | `ToastNotificationManagerCompat` is static and needs a live notification platform. The seam moves every routing and gating decision onto the tested side |
| `LoggingNotificationSink` deleted | It was the Phase 5 stand-in for exactly this service |

## Notes carried forward

- **The registered COM path is the published exe path** (spike finding 2). Renaming the assembly
  invalidates any registration written against `OnDeck.App.exe`; the Toolkit rewrites it on the next
  `Show()`. Phase 10 should call `ToastNotificationManagerCompat.Uninstall()` if an uninstaller is
  ever added.
- **`PurgeAllAsync` clears delivered history only.** `PORT_PLAN.md` also mentions scheduled toasts;
  nothing here schedules any (`ExpirationTime` is not scheduling), so there is nothing to cancel. If
  scheduled toasts are ever added, this method needs `GetScheduledToastNotifications` too.
