# Phase 8: The Settings window — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Subagents are not used on this project** (session rule) — inline execution only.

**Goal:** Build `SettingsWindow` — the Windows port of `Views/SettingsView.swift` — over the `SettingsStore` that landed in Phase 6, and give it the two entry points Phase 7b deliberately left out: the footer Settings button and the tray context-menu item.

**Architecture:** The same split 7a and 7b established — *anything that can be wrong without looking wrong lives in a plain class with tests; XAML binds to plain properties and holds no logic*. Two plain classes carry the whole phase. `SettingsFormState` is the **read** side: a pure projection of what the orchestrator publishes into which sub-controls are visible and what every label says. `SettingsEditor` is the **write** side: an `INotifyPropertyChanged` surface over `ISettingsStore` whose setters write through and then raise `Changed`, which the window wires straight to `AppOrchestrator.SettingsChanged()` — the C# analogue of each Swift stored property's `didSet`. The XAML two-way binds its seven checkboxes to the editor and renders the state record; it computes nothing.

**Tech Stack:** WPF on `net10.0-windows` with .NET 10's native Fluent theme, xunit in `OnDeck.App.Tests`. No new packages.

## Global Constraints

- **Do not add anything to `OnDeck.Core`.** `AppOrchestrator` already publishes every value this window needs: `AvailableTeams`, `IsLoadingTeams`, `TeamsError`, `IsSyncing`, `LastSyncDate`, `SyncError`, `LoadedPlayerCount`, `ParsedLeagueId`, `UrlHasTeamId`, `EffectiveTeamId`, `FetchTeamsAsync`, `ResyncRosterAsync`, `SettingsChanged`.
- `OnDeck.Core` keeps **zero** package references. Verify with `grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj` → `0`.
- **Call `SettingsChanged()` after any settings write.** It re-reads `ISettingsStore` and rebuilds the lists locally with no network.
- **Kill `OnDeck.App.exe` before every build and test run.** A running instance locks `OnDeck.Core.dll`, `OnDeck.App.Tests` then silently fails to build, and `dotnet test` still prints `Passed!` for `OnDeck.Core.Tests` alone. **Always confirm TWO `Passed!` lines.** (HANDOFF §3.)
- Single-file publish stays green:
  `dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`
- **Do not touch the DWM backdrop path**, and **do not remove `ThemeMode="System"` from `App.xaml`**. `windows/ACRYLIC-OPEN-ISSUE.md` records ten failed attempts across two sessions; the bug is parked by owner decision. The settings window is an ordinary opaque window and has no business near it.
- **XAML traps (HANDOFF §6) — all five are silent.** The two that bite this phase:
  1. An explicit `Style` on a control **replaces the Fluent theme's implicit style wholesale**, so `Foreground` falls back to `SystemColors.ControlTextBrush` (black) and renders black-on-dark. **If you style a control, set `Foreground` in that style.** Better still: on this window, do not style `CheckBox`, `Button`, `TextBox` or `ComboBox` at all — set properties on the instance so the Fluent implicit style survives.
  2. Segoe UI has **no Medium weight**; `FontWeight="Medium"` silently degrades to Regular. Use the `UiFont` resource (`Segoe UI Variable Text, Segoe UI`).
  3. `<DataTemplate.Triggers>` must be a direct child of `DataTemplate` (not used here, but the same rule governs `Style.Triggers`).
- **Type sizes come from the Swift file, not from taste.** SwiftUI `.body` = 13 pt, `.caption` = **10** pt on macOS. 7b matched these exactly and the owner signed them off; stay consistent.
- **WPF's implicit usings omit `System.IO`.** Any file in `OnDeck.App` or `OnDeck.App.Tests` touching `Path`/`File`/`Directory` needs an explicit `using System.IO;`. (Not needed by this phase's files, but it is the standing gotcha.)
- Commits go **directly to `main`**, one per task. **Never append `Co-Authored-By` or any AI-attribution trailer.**
- Commands run from the repo root (`c:\Users\brian\Code\onDeck`). A bare `dotnet test` there fails — always pass `windows/OnDeck.slnx`.
- Launching the built app from `windows/src/OnDeck.App/bin/Debug/net10.0-windows/` to look at it is allowed and expected. **Installing it is not.** **Don't trust automated screen capture** — it has produced confidently wrong conclusions twice on this project. Ask the owner to look.

## Scope

**In:** every control in `SettingsView.swift` — League URL field with fetch-on-submit, team picker with its loading / picker / Load Teams / error states, sync status line with relative last-synced time, Sync Now with its disabled rule, the two display toggles, the five notification toggles, the two GitHub links — plus the footer Settings button (`Views/FooterBar.xaml`) and the tray context-menu Settings item (`Tray/TrayIconService.cs`), wired to a single window instance in `App.xaml.cs`.

**Out (deliberate):**
- **`NSApplication.setActivationPolicy` (`SettingsView.swift:115-122`).** It exists to let macOS unload the Settings window infrastructure and reclaim ~230 MB. Windows has no analogue. The nearest equivalent behaviour — actually releasing the window when it closes rather than hiding it — is what Task 6 does.
- **Any change to `OnDeck.Core`.** If something looks missing, re-read `AppOrchestrator`; it is all there.
- App icon / window icon polish — that belongs with app identity in Phase 10.

## Decisions taken with the owner before writing this plan

1. **The League URL commits on Enter, on focus loss, and on window close** — not on every keystroke. SwiftUI's `$appState.rosterURL` binding writes UserDefaults per character; our `SettingsStore` rewrites `settings.json` through a temp-file-and-move on every write, so per-keystroke persistence means a file rewrite per character. Enter additionally runs `FetchTeamsAsync()`, which is Swift's `.onSubmit`. Recorded as a deviation.
2. **The window uses grouped cards**, echoing `.formStyle(.grouped)`: section header, then a rounded card on a recessed window background. This needs two new palette keys (`OnDeck.Surface`, `OnDeck.Surface.Card`), which also makes the window's background **explicit** rather than inherited — a window that inherits an unexpected white background under dark-theme white text is precisely the silent-visual-failure class this project keeps hitting.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/OnDeck.App/Views/ThemePalette.cs` | *(modify)* add `OnDeck.Surface` and `OnDeck.Surface.Card` — a window background and a card background, in both themes |
| `src/OnDeck.App/Views/RelativeTime.cs` | The `"5 minutes"` half of Swift's `Text(date, style: .relative)` |
| `src/OnDeck.App/Views/SettingsFormState.cs` | `SettingsInput` → `SettingsFormState`: which sub-controls show, every label's text, whether Sync Now is enabled. Plus `SettingsInputFactory`, the one place that touches Core |
| `src/OnDeck.App/Views/SettingsEditor.cs` | `INotifyPropertyChanged` write-through over `ISettingsStore`; raises `Changed` after every write |
| `src/OnDeck.App/Windows/SettingsWindow.xaml(.cs)` | The window: four grouped cards, bound to the editor, rendering the state |
| `src/OnDeck.App/Views/FooterBar.xaml(.cs)` | *(modify)* the Settings button, first in the row, raising `SettingsRequested` |
| `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs` | *(modify)* forward the footer's `SettingsRequested`, hiding the flyout first |
| `src/OnDeck.App/Tray/TrayIconService.cs` | *(modify)* Settings menu item between Float and Refresh |
| `src/OnDeck.App/App.xaml.cs` | *(modify)* own the single `SettingsWindow`; keep the `SettingsStore` in a field |
| `tests/OnDeck.App.Tests/RecordingSettingsStore.cs` | `ISettingsStore` double that records which properties were written, in order |
| `tests/OnDeck.App.Tests/ThemePaletteTests.cs` | *(modify)* surface keys are opaque and the card sits above the surface |
| `tests/OnDeck.App.Tests/RelativeTimeTests.cs` | Unit boundaries, singular/plural, clock skew |
| `tests/OnDeck.App.Tests/SettingsFormStateTests.cs` | The picker-state chain, sync status, Sync Now enablement, team options |
| `tests/OnDeck.App.Tests/SettingsEditorTests.cs` | Write-through, no redundant writes, `Changed`/`PropertyChanged`, URL normalisation |

**Why no test instantiates a `Window`.** WPF resolves `x:Name`, template structure and resource *keys* at build time, so a structural XAML mistake fails the build. What survives the build is colour, weight and size — which no headless test can judge and which cost 7b a visual-parity pass. Standing up an STA `Application` per test buys nothing against that and adds a fragile fixture (a process may hold only one `Application`). The guard is the plain-class tests below plus the human pass in Task 7.

---

## Task 1: Surface colours for a window that isn't the flyout

**Files:**
- Modify: `src/OnDeck.App/Views/ThemePalette.cs`
- Modify: `tests/OnDeck.App.Tests/ThemePaletteTests.cs`

**Interfaces:**
- Produces: `ThemePalette.Surface` (`"OnDeck.Surface"`) and `ThemePalette.SurfaceCard` (`"OnDeck.Surface.Card"`), both present in `Keys` and in both palettes.
- Consumed by: Task 5's XAML via `{DynamicResource OnDeck.Surface}` / `{DynamicResource OnDeck.Surface.Card}`.

**Why this exists.** The flyout paints its own backdrop and never needed a window colour. A normal window does: with no explicit `Background` it takes whatever the theme hands it, and if that disagrees with `AppsUseLightTheme` — which is what drives `OnDeck.Text.*` — the window renders white-on-white or black-on-black. Owning both colours removes the guess. `ThemePaletteTests` already asserts that both themes cover exactly `Keys`, so adding a key without adding both colours fails immediately.

- [ ] **Step 1: Write the failing test**

Append to `tests/OnDeck.App.Tests/ThemePaletteTests.cs`, inside the existing class, above the private `Brightness` helper:

```csharp
    [Fact]
    public void SurfacesAreFullyOpaque()
    {
        foreach (var palette in new[]
                 {
                     ThemePalette.For(appsUseLightTheme: true),
                     ThemePalette.For(appsUseLightTheme: false),
                 })
        {
            // A window background with any transparency shows whatever the compositor left
            // behind it. The flyout can afford alpha; a real window cannot.
            Assert.Equal(0xFF, palette.Colors[ThemePalette.Surface].A);
            Assert.Equal(0xFF, palette.Colors[ThemePalette.SurfaceCard].A);
        }
    }

    [Fact]
    public void CardsSitAboveTheSurfaceInBothThemes()
    {
        foreach (var palette in new[]
                 {
                     ThemePalette.For(appsUseLightTheme: true),
                     ThemePalette.For(appsUseLightTheme: false),
                 })
        {
            // Grouped-form cards read as raised. If this inverts, the sections vanish into
            // the background and the window looks like an undifferentiated list.
            Assert.True(
                Brightness(palette.Colors[ThemePalette.SurfaceCard])
                > Brightness(palette.Colors[ThemePalette.Surface]));
        }
    }

    [Fact]
    public void TextReadsAgainstTheCardItSitsOn()
    {
        var light = ThemePalette.For(appsUseLightTheme: true);
        var dark = ThemePalette.For(appsUseLightTheme: false);

        Assert.True(Brightness(light.Colors[ThemePalette.SurfaceCard]) > 0.5);
        Assert.True(Brightness(light.Colors[ThemePalette.TextPrimary]) < 0.5);

        Assert.True(Brightness(dark.Colors[ThemePalette.SurfaceCard]) < 0.5);
        Assert.True(Brightness(dark.Colors[ThemePalette.TextPrimary]) > 0.5);
    }
```

- [ ] **Step 2: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ThemePaletteTests
```
Expected: build failure — `ThemePalette` has no `Surface` or `SurfaceCard`.

- [ ] **Step 3: Add the two keys**

In `src/OnDeck.App/Views/ThemePalette.cs`, add the constants after `BaseEmpty`:

```csharp
    public const string BaseEmpty = "OnDeck.Base.Empty";
    public const string Surface = "OnDeck.Surface";
    public const string SurfaceCard = "OnDeck.Surface.Card";
```

Extend `Keys`:

```csharp
    public static IReadOnlyList<string> Keys { get; } =
    [
        TextPrimary, TextSecondary, Divider, RowHover,
        Green, Orange, Red, Blue, BaseOccupied, BaseEmpty,
        Surface, SurfaceCard,
    ];
```

Add to `Dark()`, after `BaseEmpty`:

```csharp
        [BaseEmpty] = Color.FromArgb(0x4D, 0x80, 0x80, 0x80),       // .gray.opacity(0.3)

        // Window and card for the settings form's grouped sections - SwiftUI's
        // .formStyle(.grouped) recesses the window and raises the cards.
        [Surface] = Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
        [SurfaceCard] = Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B),
```

Add to `Light()`, after its `BaseEmpty`:

```csharp
        [BaseEmpty] = Color.FromArgb(0x4D, 0x80, 0x80, 0x80),
        [Surface] = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF7),
        [SurfaceCard] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
```

- [ ] **Step 4: Run and confirm the tests pass**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ThemePaletteTests
```
Expected: PASS, including the pre-existing `Keys_AreCoveredByBothThemes` count assertion.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Views/ThemePalette.cs windows/tests/OnDeck.App.Tests/ThemePaletteTests.cs
git commit -m "phase 8: window and card surfaces in the palette"
```

---

## Task 2: Relative last-synced time

**Files:**
- Create: `src/OnDeck.App/Views/RelativeTime.cs`
- Create: `tests/OnDeck.App.Tests/RelativeTimeTests.cs`

**Interfaces:**
- Produces: `static string RelativeTime.Describe(DateTimeOffset date, DateTimeOffset now)` → `"45 seconds"`, `"1 minute"`, `"3 hours"`, `"2 days"`.
- Consumed by: Task 3's `SettingsFormState.Build`, which wraps it as `$"Last synced: {…} ago"`.

**Why this exists.** `SettingsView.swift:55` is `Text("Last synced: \(date, style: .relative) ago")`. SwiftUI's relative style is a formatter with real rules — largest whole unit, singular at one. .NET has no drop-in equivalent, so the rules get written here where they can be tested, rather than inline in a render method where "1 minutes" ships unnoticed. `now` is a parameter rather than a clock read so the tests need no `TimeProvider` fixture.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/RelativeTimeTests.cs`:

```csharp
using OnDeck.App.Views;

namespace OnDeck.App.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 19, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(45, "45 seconds")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "1 minute")]              // truncates to the largest whole unit
    [InlineData(300, "5 minutes")]
    [InlineData(3599, "59 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(5400, "1 hour")]
    [InlineData(86_399, "23 hours")]
    [InlineData(86_400, "1 day")]
    [InlineData(259_200, "3 days")]
    public void DescribesTheLargestWholeUnit(int secondsAgo, string expected)
    {
        var result = RelativeTime.Describe(Now.AddSeconds(-secondsAgo), Now);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void AFutureStampNeverReadsAsNegative()
    {
        // A clock change between the sync and the render should not produce
        // "Last synced: -30 seconds ago".
        var result = RelativeTime.Describe(Now.AddMinutes(5), Now);

        Assert.Equal("0 seconds", result);
    }

    [Fact]
    public void InstantsAreComparedNotWallClockDigits()
    {
        // RosterManager stamps LastSyncDate in whatever offset the machine is in; the render
        // clock is DateTimeOffset.Now. Subtracting two DateTimeOffsets compares instants, and
        // this test fails loudly if someone "simplifies" that to DateTime.
        var sameInstantElsewhere = Now.ToOffset(TimeSpan.FromHours(-5)).AddMinutes(-2);

        Assert.Equal("2 minutes", RelativeTime.Describe(sameInstantElsewhere, Now));
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~RelativeTimeTests
```
Expected: build failure — `RelativeTime` does not exist.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Views/RelativeTime.cs`:

```csharp
namespace OnDeck.App.Views;

/// <summary>
/// The <c>"5 minutes"</c> half of Swift's <c>Text(date, style: .relative)</c>
/// (<c>Views/SettingsView.swift:55</c>) — the caller supplies the surrounding
/// <c>"Last synced: … ago"</c>. Largest whole unit, singular at one.
/// </summary>
public static class RelativeTime
{
    public static string Describe(DateTimeOffset date, DateTimeOffset now)
    {
        var elapsed = now - date;

        // A clock adjustment between the sync and this render can put the stamp in the
        // future; "-30 seconds ago" is worse than "0 seconds ago".
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (elapsed.TotalSeconds < 60) return Quantity((int)elapsed.TotalSeconds, "second");
        if (elapsed.TotalMinutes < 60) return Quantity((int)elapsed.TotalMinutes, "minute");
        if (elapsed.TotalHours < 24) return Quantity((int)elapsed.TotalHours, "hour");
        return Quantity((int)elapsed.TotalDays, "day");
    }

    private static string Quantity(int count, string unit) =>
        count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
```

- [ ] **Step 4: Run and confirm the tests pass**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~RelativeTimeTests
```
Expected: PASS — 15 cases.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Views/RelativeTime.cs windows/tests/OnDeck.App.Tests/RelativeTimeTests.cs
git commit -m "phase 8: relative last-synced time"
```

---

## Task 3: The settings form's read side

**Files:**
- Create: `src/OnDeck.App/Views/SettingsFormState.cs`
- Create: `tests/OnDeck.App.Tests/SettingsFormStateTests.cs`

**Interfaces:**
- Consumes: `RelativeTime.Describe` (Task 2); `OnDeck.Core.Networking.FantraxTeam`; `OnDeck.Core.AppOrchestrator`; `OnDeck.Core.ISettingsStore`.
- Produces:
  - `sealed record TeamOption(string Id, string Name)`
  - `sealed record SettingsInput` with `RosterUrl`, `ParsedLeagueId`, `UrlHasTeamId`, `SelectedTeamId`, `HasEffectiveTeam`, `AvailableTeams`, `IsLoadingTeams`, `TeamsError`, `IsSyncing`, `LastSyncDate`, `SyncError`, `LoadedPlayerCount`
  - `sealed record SettingsFormState` with `ShowsLoadingTeams`, `ShowsTeamPicker`, `ShowsLoadTeamsButton`, `TeamsErrorText`, `ShowsTeamsError`, `TeamOptions`, `SelectedTeamOptionId`, `ShowsSyncSpinner`, `SyncStatusText`, `ShowsSyncStatus`, `IsSyncNowEnabled`, `SyncErrorText`, `ShowsSyncError`, `PlayerCountText`, `ShowsPlayerCount`, and `static SettingsFormState Build(SettingsInput input, DateTimeOffset now)`
  - `static class SettingsInputFactory` with `From(AppOrchestrator orchestrator, ISettingsStore settings)`
- Consumed by: Task 5's `SettingsWindow.Render`.

**Why this exists.** `SettingsView.swift:20-46` is a four-branch conditional nested inside an outer `if`, and getting a branch wrong shows up as a control that is simply absent — no error, nothing in a log. Extracting it makes every branch an assertion. `SettingsInputFactory` is the sole place the shell reads Core, exactly as `FlyoutInputFactory` is for the flyout, so the branch rules stay testable on plain values.

**Two details from the Swift that are easy to miss.** The team error at `:41-45` sits **inside** the `if !rosterURL.isEmpty && !urlHasTeamID` block, so a URL carrying its own teamId hides the error along with the picker. And `Load Teams` at `:35-39` requires `parsedLeagueID != nil` — an unparseable URL offers nothing to click.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/SettingsFormStateTests.cs`:

```csharp
using OnDeck.App.Views;
using OnDeck.Core.Networking;

namespace OnDeck.App.Tests;

public class SettingsFormStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 19, 30, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<FantraxTeam> TwoTeams =
    [
        new("t1", "Bronx Bombers"),
        new("t2", "Queens Crew"),
    ];

    /// <summary>A URL that parses to a league but carries no teamId - the picker's whole reason.</summary>
    private static SettingsInput LeagueOnly() => new()
    {
        RosterUrl = "https://www.fantrax.com/fantasy/league/lg1/home",
        ParsedLeagueId = "lg1",
        UrlHasTeamId = false,
    };

    [Fact]
    public void NothingAboutTeamsShowsUntilThereIsAUrl()
    {
        var state = SettingsFormState.Build(new SettingsInput(), Now);

        Assert.False(state.ShowsLoadingTeams);
        Assert.False(state.ShowsTeamPicker);
        Assert.False(state.ShowsLoadTeamsButton);
    }

    [Fact]
    public void AUrlCarryingItsOwnTeamIdHidesTheEntirePickerBlock()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with
            {
                UrlHasTeamId = true,
                AvailableTeams = TwoTeams,
                IsLoadingTeams = true,
                TeamsError = "Couldn't load teams: boom",
            },
            Now);

        Assert.False(state.ShowsTeamPicker);
        Assert.False(state.ShowsLoadingTeams);
        Assert.False(state.ShowsLoadTeamsButton);

        // SettingsView.swift:41 nests the error inside the same `if` - it is not a
        // free-floating error row.
        Assert.False(state.ShowsTeamsError);
        Assert.Null(state.TeamsErrorText);
    }

    [Fact]
    public void LoadingReplacesThePicker()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { IsLoadingTeams = true, AvailableTeams = TwoTeams }, Now);

        Assert.True(state.ShowsLoadingTeams);
        Assert.False(state.ShowsTeamPicker);
        Assert.False(state.ShowsLoadTeamsButton);
    }

    [Fact]
    public void TeamsArriveAndThePickerAppears()
    {
        var state = SettingsFormState.Build(LeagueOnly() with { AvailableTeams = TwoTeams }, Now);

        Assert.True(state.ShowsTeamPicker);
        Assert.False(state.ShowsLoadingTeams);
        Assert.False(state.ShowsLoadTeamsButton);
    }

    [Fact]
    public void NoTeamsYetOffersLoadTeams()
    {
        var state = SettingsFormState.Build(LeagueOnly(), Now);

        Assert.True(state.ShowsLoadTeamsButton);
        Assert.False(state.ShowsTeamPicker);
    }

    [Fact]
    public void AnUnparseableUrlOffersNothingToClick()
    {
        var state = SettingsFormState.Build(
            new SettingsInput { RosterUrl = "not a url", ParsedLeagueId = null }, Now);

        Assert.False(state.ShowsLoadTeamsButton);
        Assert.False(state.ShowsTeamPicker);
    }

    [Fact]
    public void ATeamsErrorSurfacesUnderThePickerBlock()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { TeamsError = "Couldn't load teams: timed out" }, Now);

        Assert.True(state.ShowsTeamsError);
        Assert.Equal("Couldn't load teams: timed out", state.TeamsErrorText);
    }

    [Fact]
    public void ThePlaceholderIsAlwaysTheFirstOption()
    {
        var state = SettingsFormState.Build(LeagueOnly() with { AvailableTeams = TwoTeams }, Now);

        Assert.Equal(
            new[]
            {
                new TeamOption("", "Select a team..."),
                new TeamOption("t1", "Bronx Bombers"),
                new TeamOption("t2", "Queens Crew"),
            },
            state.TeamOptions);
    }

    [Fact]
    public void TheStoredTeamIsTheSelectedOption()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { AvailableTeams = TwoTeams, SelectedTeamId = "t2" }, Now);

        Assert.Equal("t2", state.SelectedTeamOptionId);
    }

    [Fact]
    public void ATeamThatIsNoLongerInTheLeagueFallsBackToThePlaceholder()
    {
        // FetchTeamsAsync clears a stale selection, but the window can render between the
        // team list arriving and that write landing. Selecting an id that is not in the
        // list leaves a WPF ComboBox blank with no indication why.
        var state = SettingsFormState.Build(
            LeagueOnly() with { AvailableTeams = TwoTeams, SelectedTeamId = "gone" }, Now);

        Assert.Equal("", state.SelectedTeamOptionId);
    }

    [Fact]
    public void SyncingShowsTheSpinnerAndItsLabel()
    {
        var state = SettingsFormState.Build(LeagueOnly() with { IsSyncing = true }, Now);

        Assert.True(state.ShowsSyncSpinner);
        Assert.Equal("Syncing...", state.SyncStatusText);
    }

    [Fact]
    public void SyncingOutranksTheLastSyncedStamp()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { IsSyncing = true, LastSyncDate = Now.AddMinutes(-4) }, Now);

        Assert.Equal("Syncing...", state.SyncStatusText);
    }

    [Fact]
    public void TheLastSyncedStampReadsAsRelativeAge()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { LastSyncDate = Now.AddMinutes(-4) }, Now);

        Assert.False(state.ShowsSyncSpinner);
        Assert.Equal("Last synced: 4 minutes ago", state.SyncStatusText);
    }

    [Fact]
    public void ThereIsNoStatusLineBeforeTheFirstSync()
    {
        var state = SettingsFormState.Build(LeagueOnly(), Now);

        Assert.False(state.ShowsSyncStatus);
        Assert.Null(state.SyncStatusText);
    }

    [Fact]
    public void SyncNowNeedsATeamAndAnIdleSync()
    {
        Assert.False(SettingsFormState.Build(LeagueOnly(), Now).IsSyncNowEnabled);

        Assert.False(SettingsFormState.Build(
            LeagueOnly() with { HasEffectiveTeam = true, IsSyncing = true }, Now).IsSyncNowEnabled);

        Assert.True(SettingsFormState.Build(
            LeagueOnly() with { HasEffectiveTeam = true }, Now).IsSyncNowEnabled);
    }

    [Fact]
    public void ASyncErrorIsSurfacedVerbatim()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { SyncError = "Fantrax API returned HTTP 403" }, Now);

        Assert.True(state.ShowsSyncError);
        Assert.Equal("Fantrax API returned HTTP 403", state.SyncErrorText);
    }

    [Fact]
    public void ThePlayerCountAppearsOnlyOnceThereArePlayers()
    {
        Assert.False(SettingsFormState.Build(LeagueOnly(), Now).ShowsPlayerCount);

        var loaded = SettingsFormState.Build(LeagueOnly() with { LoadedPlayerCount = 26 }, Now);

        Assert.True(loaded.ShowsPlayerCount);
        Assert.Equal("26 players loaded", loaded.PlayerCountText);
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~SettingsFormStateTests
```
Expected: build failure — `SettingsInput`, `SettingsFormState` and `TeamOption` do not exist.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Views/SettingsFormState.cs`:

```csharp
using OnDeck.Core;
using OnDeck.Core.Networking;

namespace OnDeck.App.Views;

/// <summary>
/// One row of the team picker. The placeholder carries an empty id, mirroring Swift's
/// <c>Text("Select a team...").tag("")</c>.
/// </summary>
public sealed record TeamOption(string Id, string Name);

/// <summary>
/// Everything <c>Views/SettingsView.swift</c> reads off <c>AppState</c>, as plain values. Taking
/// this rather than the orchestrator keeps the form's branch rules testable without standing up
/// the engine.
/// </summary>
public sealed record SettingsInput
{
    public string RosterUrl { get; init; } = "";
    public string? ParsedLeagueId { get; init; }
    public bool UrlHasTeamId { get; init; }
    public string? SelectedTeamId { get; init; }

    /// <summary><c>effectiveTeamID != nil</c> — the Sync Now guard.</summary>
    public bool HasEffectiveTeam { get; init; }

    public IReadOnlyList<FantraxTeam> AvailableTeams { get; init; } = [];
    public bool IsLoadingTeams { get; init; }
    public string? TeamsError { get; init; }
    public bool IsSyncing { get; init; }
    public DateTimeOffset? LastSyncDate { get; init; }
    public string? SyncError { get; init; }
    public int LoadedPlayerCount { get; init; }
}

/// <summary>
/// The laid-out settings form: which sub-controls appear and what each label says. Port of the
/// conditional structure in <c>Views/SettingsView.swift:13-79</c>.
/// </summary>
public sealed record SettingsFormState
{
    /// <summary>The id of Swift's <c>Text("Select a team...").tag("")</c>.</summary>
    public const string PlaceholderTeamId = "";

    public bool ShowsLoadingTeams { get; init; }
    public bool ShowsTeamPicker { get; init; }
    public bool ShowsLoadTeamsButton { get; init; }
    public string? TeamsErrorText { get; init; }
    public bool ShowsTeamsError => TeamsErrorText is not null;

    public IReadOnlyList<TeamOption> TeamOptions { get; init; } = [];
    public string SelectedTeamOptionId { get; init; } = PlaceholderTeamId;

    public bool ShowsSyncSpinner { get; init; }
    public string? SyncStatusText { get; init; }
    public bool ShowsSyncStatus => SyncStatusText is not null;
    public bool IsSyncNowEnabled { get; init; }

    public string? SyncErrorText { get; init; }
    public bool ShowsSyncError => SyncErrorText is not null;

    public string? PlayerCountText { get; init; }
    public bool ShowsPlayerCount => PlayerCountText is not null;

    public static SettingsFormState Build(SettingsInput input, DateTimeOffset now)
    {
        // Swift wraps the picker, its loading row, the Load Teams button AND the teams error
        // in one `if !rosterURL.isEmpty && !urlHasTeamID` (SettingsView.swift:20-46).
        var needsPicker = input.RosterUrl.Length > 0 && !input.UrlHasTeamId;
        var hasTeams = input.AvailableTeams.Count > 0;

        List<TeamOption> options = [new(PlaceholderTeamId, "Select a team...")];
        options.AddRange(input.AvailableTeams.Select(team => new TeamOption(team.Id, team.Name)));

        // A selection that isn't among the options leaves a ComboBox blank with no clue why;
        // fall back to the placeholder, which is what the user is being asked to replace.
        var selected = input.SelectedTeamId ?? PlaceholderTeamId;
        if (options.All(option => option.Id != selected)) selected = PlaceholderTeamId;

        return new SettingsFormState
        {
            ShowsLoadingTeams = needsPicker && input.IsLoadingTeams,
            ShowsTeamPicker = needsPicker && !input.IsLoadingTeams && hasTeams,
            ShowsLoadTeamsButton = needsPicker && !input.IsLoadingTeams && !hasTeams
                                   && input.ParsedLeagueId is not null,
            TeamsErrorText = needsPicker ? NullIfBlank(input.TeamsError) : null,

            TeamOptions = options,
            SelectedTeamOptionId = selected,

            ShowsSyncSpinner = input.IsSyncing,
            SyncStatusText = input.IsSyncing
                ? "Syncing..."
                : input.LastSyncDate is { } date
                    ? $"Last synced: {RelativeTime.Describe(date, now)} ago"
                    : null,
            IsSyncNowEnabled = !input.IsSyncing && input.HasEffectiveTeam,

            SyncErrorText = NullIfBlank(input.SyncError),
            PlayerCountText = input.LoadedPlayerCount > 0
                ? $"{input.LoadedPlayerCount} players loaded"
                : null,
        };
    }

    private static string? NullIfBlank(string? text) =>
        string.IsNullOrEmpty(text) ? null : text;
}

/// <summary>
/// Reads a <see cref="SettingsInput"/> off the orchestrator and the store. The one place the
/// settings window touches Core, mirroring <see cref="FlyoutInputFactory"/>.
/// </summary>
public static class SettingsInputFactory
{
    public static SettingsInput From(AppOrchestrator orchestrator, ISettingsStore settings) => new()
    {
        RosterUrl = settings.RosterUrl ?? "",
        ParsedLeagueId = orchestrator.ParsedLeagueId,
        UrlHasTeamId = orchestrator.UrlHasTeamId,
        SelectedTeamId = settings.SelectedTeamId,
        HasEffectiveTeam = orchestrator.EffectiveTeamId is not null,
        AvailableTeams = orchestrator.AvailableTeams,
        IsLoadingTeams = orchestrator.IsLoadingTeams,
        TeamsError = orchestrator.TeamsError,
        IsSyncing = orchestrator.IsSyncing,
        LastSyncDate = orchestrator.LastSyncDate,
        SyncError = orchestrator.SyncError,
        LoadedPlayerCount = orchestrator.LoadedPlayerCount,
    };
}
```

- [ ] **Step 4: Run and confirm the tests pass**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~SettingsFormStateTests
```
Expected: PASS — 17 tests.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Views/SettingsFormState.cs windows/tests/OnDeck.App.Tests/SettingsFormStateTests.cs
git commit -m "phase 8: settings form state"
```

---

## Task 4: The settings form's write side

**Files:**
- Create: `src/OnDeck.App/Views/SettingsEditor.cs`
- Create: `tests/OnDeck.App.Tests/RecordingSettingsStore.cs`
- Create: `tests/OnDeck.App.Tests/SettingsEditorTests.cs`

**Interfaces:**
- Consumes: `OnDeck.Core.ISettingsStore`.
- Produces: `sealed class SettingsEditor(ISettingsStore settings) : INotifyPropertyChanged` with two-way properties `RosterUrl` (string), `SelectedTeamId` (string), `HideBenchPlayers`, `AlwaysOpenPopout`, `NotifyBatting`, `NotifyPitching`, `NotifyAtBatResult`, `NotifyPitchingResult`, `NotifyNotInLineup` (bool), and `event Action? Changed`.
- Produces: `sealed class RecordingSettingsStore : ISettingsStore` with `List<string> Writes`.
- Consumed by: Task 5 — the window sets it as `DataContext` and wires `Changed` to `AppOrchestrator.SettingsChanged`.

**Why this exists.** Swift gets instant-apply for free: each `@Bindable` property has a `didSet` that writes UserDefaults, and `hideBenchPlayers` additionally calls `updatePlayerLists()`. The WPF equivalent of that binding target is an `INotifyPropertyChanged` object, which lets all seven checkboxes bind with `IsChecked="{Binding …}"` and **no event handlers in code-behind at all**. Nine near-identical properties is exactly the shape where a copy-paste slip writes the wrong key — a mistake that shows up months later as "the pitching toggle also turns off batting alerts". One test asserts the five notification writes land on five distinct keys, in order.

The `if (value == current) return` guard matters: `Changed` runs `SettingsChanged()` → `UpdatePlayerLists()` → `StateChanged` → a re-render, and a re-render that writes back would loop.

- [ ] **Step 1: Write the test double**

Create `tests/OnDeck.App.Tests/RecordingSettingsStore.cs`:

```csharp
using System.Runtime.CompilerServices;
using OnDeck.Core;

namespace OnDeck.App.Tests;

/// <summary>
/// An <see cref="ISettingsStore"/> that holds values in memory and records the name of every
/// property written, in order. That lets a test tell "wrote the same value again" from "did not
/// write at all" — the difference between a form that rewrites settings.json on every render and
/// one that doesn't.
/// </summary>
public sealed class RecordingSettingsStore : ISettingsStore
{
    private string? _rosterUrl;
    private string? _selectedTeamId;
    private bool _hideBenchPlayers;
    private bool _alwaysOpenPopout;
    private bool _notifyBatting = true;
    private bool _notifyPitching = true;
    private bool _notifyAtBatResult = true;
    private bool _notifyPitchingResult = true;
    private bool _notifyNotInLineup = true;
    private string? _rosterCacheJson;

    public List<string> Writes { get; } = [];

    public string? RosterUrl { get => _rosterUrl; set => Record(ref _rosterUrl, value); }

    public string? SelectedTeamId { get => _selectedTeamId; set => Record(ref _selectedTeamId, value); }

    public bool HideBenchPlayers { get => _hideBenchPlayers; set => Record(ref _hideBenchPlayers, value); }

    public bool AlwaysOpenPopout { get => _alwaysOpenPopout; set => Record(ref _alwaysOpenPopout, value); }

    public bool NotifyBatting { get => _notifyBatting; set => Record(ref _notifyBatting, value); }

    public bool NotifyPitching { get => _notifyPitching; set => Record(ref _notifyPitching, value); }

    public bool NotifyAtBatResult { get => _notifyAtBatResult; set => Record(ref _notifyAtBatResult, value); }

    public bool NotifyPitchingResult
    {
        get => _notifyPitchingResult;
        set => Record(ref _notifyPitchingResult, value);
    }

    public bool NotifyNotInLineup { get => _notifyNotInLineup; set => Record(ref _notifyNotInLineup, value); }

    public string? RosterCacheJson { get => _rosterCacheJson; set => Record(ref _rosterCacheJson, value); }

    private void Record<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        field = value;
        Writes.Add(property!);
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/OnDeck.App.Tests/SettingsEditorTests.cs`:

```csharp
using System.ComponentModel;
using OnDeck.App.Views;

namespace OnDeck.App.Tests;

public class SettingsEditorTests
{
    [Fact]
    public void AToggleWritesThroughAndAnnouncesTheChange()
    {
        var store = new RecordingSettingsStore();
        var editor = new SettingsEditor(store);
        var changes = 0;
        editor.Changed += () => changes++;

        editor.HideBenchPlayers = true;

        Assert.True(store.HideBenchPlayers);
        Assert.Equal(new[] { "HideBenchPlayers" }, store.Writes);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void EveryNotificationToggleWritesItsOwnKey()
    {
        // Nine near-identical properties: this is where a copy-paste slip points two toggles
        // at one key and nothing complains until a user notices the wrong alerts vanished.
        var store = new RecordingSettingsStore();
        var editor = new SettingsEditor(store);

        editor.NotifyBatting = false;
        editor.NotifyPitching = false;
        editor.NotifyAtBatResult = false;
        editor.NotifyPitchingResult = false;
        editor.NotifyNotInLineup = false;

        Assert.False(store.NotifyBatting);
        Assert.False(store.NotifyPitching);
        Assert.False(store.NotifyAtBatResult);
        Assert.False(store.NotifyPitchingResult);
        Assert.False(store.NotifyNotInLineup);
        Assert.Equal(
            new[]
            {
                "NotifyBatting", "NotifyPitching", "NotifyAtBatResult",
                "NotifyPitchingResult", "NotifyNotInLineup",
            },
            store.Writes);
    }

    [Fact]
    public void EachToggleReadsBackFromTheStore()
    {
        var store = new RecordingSettingsStore
        {
            HideBenchPlayers = true,
            AlwaysOpenPopout = true,
            NotifyAtBatResult = false,
        };

        var editor = new SettingsEditor(store);

        Assert.True(editor.HideBenchPlayers);
        Assert.True(editor.AlwaysOpenPopout);
        Assert.False(editor.NotifyAtBatResult);
        Assert.True(editor.NotifyBatting);
    }

    [Fact]
    public void WritingTheValueItAlreadyHasDoesNothing()
    {
        // Changed runs SettingsChanged() -> UpdatePlayerLists() -> StateChanged -> a re-render.
        // A write on every render would loop.
        var store = new RecordingSettingsStore();
        var editor = new SettingsEditor(store);
        var changes = 0;
        editor.Changed += () => changes++;

        editor.NotifyBatting = true;
        editor.HideBenchPlayers = false;
        editor.RosterUrl = "";

        Assert.Empty(store.Writes);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void PropertyChangedNamesThePropertyThatMoved()
    {
        var editor = new SettingsEditor(new RecordingSettingsStore());
        var names = new List<string?>();
        editor.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        editor.AlwaysOpenPopout = true;

        Assert.Equal(new[] { "AlwaysOpenPopout" }, names);
    }

    [Fact]
    public void TheRosterUrlIsTrimmedBeforeItIsStored()
    {
        var store = new RecordingSettingsStore();

        new SettingsEditor(store).RosterUrl =
            "  https://www.fantrax.com/fantasy/league/lg1/home  ";

        Assert.Equal("https://www.fantrax.com/fantasy/league/lg1/home", store.RosterUrl);
    }

    [Fact]
    public void RetypingTheSameUrlWithStrayWhitespaceIsNotAWrite()
    {
        // The window commits on Enter, on focus loss and on close, so the same text arrives
        // more than once by design.
        var store = new RecordingSettingsStore { RosterUrl = "https://x" };
        store.Writes.Clear();
        var editor = new SettingsEditor(store);

        editor.RosterUrl = "  https://x  ";

        Assert.Empty(store.Writes);
    }

    [Fact]
    public void ClearingTheRosterUrlStoresNullRatherThanAnEmptyString()
    {
        var store = new RecordingSettingsStore { RosterUrl = "https://x" };
        var editor = new SettingsEditor(store);

        editor.RosterUrl = "";

        Assert.Null(store.RosterUrl);
        Assert.Equal("", editor.RosterUrl);
    }

    [Fact]
    public void AnUnsetRosterUrlReadsBackAsEmptyText()
    {
        Assert.Equal("", new SettingsEditor(new RecordingSettingsStore()).RosterUrl);
    }

    [Fact]
    public void SelectingThePlaceholderClearsTheStoredTeam()
    {
        // Swift assigns selectedTeamID = "" rather than nil; EffectiveTeamId treats an empty
        // string as no selection.
        var store = new RecordingSettingsStore { SelectedTeamId = "t2" };
        var editor = new SettingsEditor(store);

        editor.SelectedTeamId = "";

        Assert.Equal("", store.SelectedTeamId);
    }

    [Fact]
    public void PickingATeamWritesItThrough()
    {
        var store = new RecordingSettingsStore();
        var editor = new SettingsEditor(store);
        var changes = 0;
        editor.Changed += () => changes++;

        editor.SelectedTeamId = "t2";

        Assert.Equal("t2", store.SelectedTeamId);
        Assert.Equal(1, changes);
    }
}
```

- [ ] **Step 3: Run and confirm failure**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~SettingsEditorTests
```
Expected: build failure — `SettingsEditor` does not exist.

- [ ] **Step 4: Implement**

Create `src/OnDeck.App/Views/SettingsEditor.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OnDeck.Core;

namespace OnDeck.App.Views;

/// <summary>
/// The settings form's two-way surface over <see cref="ISettingsStore"/>. Every setter writes
/// through and then raises <see cref="Changed"/>, which the window wires to
/// <c>AppOrchestrator.SettingsChanged()</c> — the C# analogue of the <c>didSet</c> on each stored
/// property in <c>App/AppState.swift:33-50</c>.
/// <para>
/// This exists so the checkboxes can bind directly (<c>IsChecked="{Binding NotifyBatting}"</c>)
/// and the window's code-behind carries no per-toggle handlers.
/// </para>
/// </summary>
public sealed class SettingsEditor(ISettingsStore settings) : INotifyPropertyChanged
{
    private readonly ISettingsStore _settings = settings;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after any write so the orchestrator can re-read and rebuild locally.</summary>
    public event Action? Changed;

    /// <summary>
    /// Trimmed on the way in and stored as null when cleared. The window commits on Enter, on
    /// focus loss and on close, so the same text arrives repeatedly — the equality guard below
    /// keeps that to one file write.
    /// </summary>
    public string RosterUrl
    {
        get => _settings.RosterUrl ?? "";
        set
        {
            var text = value.Trim();
            if (text == RosterUrl) return;

            _settings.RosterUrl = text.Length == 0 ? null : text;
            Notify();
        }
    }

    /// <summary>The empty string is the placeholder option, and clears the selection.</summary>
    public string SelectedTeamId
    {
        get => _settings.SelectedTeamId ?? "";
        set
        {
            if (value == SelectedTeamId) return;

            _settings.SelectedTeamId = value;
            Notify();
        }
    }

    public bool HideBenchPlayers
    {
        get => _settings.HideBenchPlayers;
        set
        {
            if (value == _settings.HideBenchPlayers) return;

            _settings.HideBenchPlayers = value;
            Notify();
        }
    }

    public bool AlwaysOpenPopout
    {
        get => _settings.AlwaysOpenPopout;
        set
        {
            if (value == _settings.AlwaysOpenPopout) return;

            _settings.AlwaysOpenPopout = value;
            Notify();
        }
    }

    public bool NotifyBatting
    {
        get => _settings.NotifyBatting;
        set
        {
            if (value == _settings.NotifyBatting) return;

            _settings.NotifyBatting = value;
            Notify();
        }
    }

    public bool NotifyPitching
    {
        get => _settings.NotifyPitching;
        set
        {
            if (value == _settings.NotifyPitching) return;

            _settings.NotifyPitching = value;
            Notify();
        }
    }

    public bool NotifyAtBatResult
    {
        get => _settings.NotifyAtBatResult;
        set
        {
            if (value == _settings.NotifyAtBatResult) return;

            _settings.NotifyAtBatResult = value;
            Notify();
        }
    }

    public bool NotifyPitchingResult
    {
        get => _settings.NotifyPitchingResult;
        set
        {
            if (value == _settings.NotifyPitchingResult) return;

            _settings.NotifyPitchingResult = value;
            Notify();
        }
    }

    public bool NotifyNotInLineup
    {
        get => _settings.NotifyNotInLineup;
        set
        {
            if (value == _settings.NotifyNotInLineup) return;

            _settings.NotifyNotInLineup = value;
            Notify();
        }
    }

    private void Notify([CallerMemberName] string? property = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        Changed?.Invoke();
    }
}
```

- [ ] **Step 5: Run and confirm the tests pass**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~SettingsEditorTests
```
Expected: PASS — 11 tests.

- [ ] **Step 6: Commit**

```bash
git add windows/src/OnDeck.App/Views/SettingsEditor.cs windows/tests/OnDeck.App.Tests/SettingsEditorTests.cs windows/tests/OnDeck.App.Tests/RecordingSettingsStore.cs
git commit -m "phase 8: settings editor write-through"
```

---

## Task 5: The Settings window

**Files:**
- Create: `src/OnDeck.App/Windows/SettingsWindow.xaml`
- Create: `src/OnDeck.App/Windows/SettingsWindow.xaml.cs`

**Interfaces:**
- Consumes: `SettingsFormState.Build`, `SettingsInputFactory.From` (Task 3); `SettingsEditor` (Task 4); `ThemePalette.Surface` / `ThemePalette.SurfaceCard` (Task 1); `OnDeck.App.Platform.ExternalLink.Open`.
- Produces: `public partial class SettingsWindow : Window` with constructor `SettingsWindow(AppOrchestrator orchestrator, ISettingsStore settings)`.
- Consumed by: Task 6's `App.OpenSettings()`.

**Three rules this window follows, and why.**

1. **`Render()` never writes to `RosterUrlBox.Text` or to a checkbox.** `Changed` → `SettingsChanged()` → `StateChanged` → `Render()`, so anything `Render` writes back into an input control fights the user mid-keystroke. The URL box is seeded once in the constructor; the checkboxes are bound to the editor and update themselves.
2. **The team `ComboBox` is set under an `_isRendering` guard.** Assigning `ItemsSource` or `SelectedValue` raises `SelectionChanged`, which would write the placeholder back over a real selection every time the list is re-set.
3. **No `Style` is declared for `CheckBox`, `Button`, `TextBox` or `ComboBox`.** An explicit style replaces the Fluent implicit style wholesale and takes `Foreground` down with it (HANDOFF §6, trap 1). Instance properties are safe; styles are not. The keyed `TextBlock` and `Border` styles below each set `Foreground`/`Background` explicitly for the same reason.

- [ ] **Step 1: Create the XAML**

Create `src/OnDeck.App/Windows/SettingsWindow.xaml`:

```xml
<Window x:Class="OnDeck.App.Windows.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="onDeck Settings"
        Width="480" Height="760" MinWidth="450" MinHeight="720"
        WindowStartupLocation="CenterScreen"
        Background="{DynamicResource OnDeck.Surface}"
        TextElement.Foreground="{DynamicResource OnDeck.Text.Primary}"
        TextElement.FontSize="13"
        TextOptions.TextFormattingMode="Ideal">
    <Window.Resources>

        <!-- Segoe UI Variable carries real Medium and SemiBold weights; plain "Segoe UI" has no
             Medium at all, so FontWeight="Medium" silently falls back to Regular. -->
        <FontFamily x:Key="UiFont">Segoe UI Variable Text, Segoe UI</FontFamily>

        <!-- Every keyed style below sets its own Foreground or Background. A style that omits it
             takes the control back to SystemColors defaults (black), which is invisible on the
             dark surface and gives no build or runtime error. -->

        <Style x:Key="SectionHeader" TargetType="TextBlock">
            <Setter Property="FontFamily" Value="{StaticResource UiFont}" />
            <Setter Property="FontSize" Value="13" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Foreground" Value="{DynamicResource OnDeck.Text.Primary}" />
            <Setter Property="Margin" Value="4,18,0,6" />
        </Style>

        <!-- SwiftUI .formStyle(.grouped): raised cards on a recessed window. -->
        <Style x:Key="Card" TargetType="Border">
            <Setter Property="Background" Value="{DynamicResource OnDeck.Surface.Card}" />
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="Padding" Value="14,10" />
        </Style>

        <Style x:Key="FieldLabel" TargetType="TextBlock">
            <Setter Property="FontFamily" Value="{StaticResource UiFont}" />
            <Setter Property="FontSize" Value="13" />
            <Setter Property="Foreground" Value="{DynamicResource OnDeck.Text.Primary}" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>

        <!-- SwiftUI .caption is 10 pt on macOS, not 11. -->
        <Style x:Key="Caption" TargetType="TextBlock">
            <Setter Property="FontSize" Value="10" />
            <Setter Property="Foreground" Value="{DynamicResource OnDeck.Text.Secondary}" />
            <Setter Property="TextWrapping" Value="Wrap" />
            <Setter Property="Margin" Value="0,6,0,0" />
        </Style>

        <Style x:Key="ErrorCaption" TargetType="TextBlock" BasedOn="{StaticResource Caption}">
            <Setter Property="Foreground" Value="{DynamicResource OnDeck.Accent.Red}" />
        </Style>

    </Window.Resources>

    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
        <StackPanel Margin="20,4,20,24">

            <!-- Fantrax Roster -->
            <TextBlock Text="Fantrax Roster" Style="{StaticResource SectionHeader}" />
            <Border Style="{StaticResource Card}">
                <StackPanel>

                    <Grid Margin="0,2,0,0">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="86" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="League URL"
                                   Style="{StaticResource FieldLabel}" />
                        <TextBox x:Name="RosterUrlBox" Grid.Column="1" FontSize="13"
                                 KeyDown="OnRosterUrlKeyDown" LostFocus="OnRosterUrlLostFocus" />
                    </Grid>

                    <StackPanel x:Name="LoadingTeamsRow" Orientation="Horizontal"
                                Margin="0,10,0,0" Visibility="Collapsed">
                        <ProgressBar IsIndeterminate="True" Width="60" Height="3"
                                     Margin="0,0,8,0" VerticalAlignment="Center" />
                        <TextBlock Text="Loading teams..." FontSize="13"
                                   VerticalAlignment="Center"
                                   Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                    </StackPanel>

                    <Grid x:Name="TeamPickerRow" Margin="0,10,0,0" Visibility="Collapsed">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="86" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="Team" Style="{StaticResource FieldLabel}" />
                        <ComboBox x:Name="TeamPicker" Grid.Column="1" FontSize="13"
                                  DisplayMemberPath="Name" SelectedValuePath="Id"
                                  SelectionChanged="OnTeamChanged" />
                    </Grid>

                    <Button x:Name="LoadTeamsButton" Content="Load Teams"
                            HorizontalAlignment="Left" Margin="0,10,0,0" Padding="14,4"
                            FontSize="13" Visibility="Collapsed" Click="OnLoadTeams" />

                    <TextBlock x:Name="TeamsErrorText" Style="{StaticResource ErrorCaption}"
                               Visibility="Collapsed" />

                    <Grid Margin="0,12,0,0">
                        <StackPanel Orientation="Horizontal" HorizontalAlignment="Left"
                                    VerticalAlignment="Center">
                            <ProgressBar x:Name="SyncSpinner" IsIndeterminate="True"
                                         Width="60" Height="3" Margin="0,0,8,0"
                                         VerticalAlignment="Center" Visibility="Collapsed" />
                            <TextBlock x:Name="SyncStatusText" Style="{StaticResource Caption}"
                                       Margin="0" VerticalAlignment="Center"
                                       Visibility="Collapsed" />
                        </StackPanel>
                        <Button x:Name="SyncNowButton" Content="Sync Now" FontSize="13"
                                HorizontalAlignment="Right" Padding="14,4" Click="OnSyncNow" />
                    </Grid>

                    <TextBlock x:Name="SyncErrorText" Style="{StaticResource ErrorCaption}"
                               Visibility="Collapsed" />
                    <TextBlock x:Name="PlayerCountText" Style="{StaticResource Caption}"
                               Visibility="Collapsed" />

                </StackPanel>
            </Border>

            <!-- Display -->
            <TextBlock Text="Display" Style="{StaticResource SectionHeader}" />
            <Border Style="{StaticResource Card}">
                <StackPanel>
                    <CheckBox Content="Hide bench players" FontSize="13" Margin="0,4"
                              IsChecked="{Binding HideBenchPlayers}" />
                    <CheckBox Content="Always open popout on launch" FontSize="13" Margin="0,4"
                              IsChecked="{Binding AlwaysOpenPopout}" />
                </StackPanel>
            </Border>

            <!-- Notifications -->
            <TextBlock Text="Notifications" Style="{StaticResource SectionHeader}" />
            <Border Style="{StaticResource Card}">
                <StackPanel>
                    <CheckBox Content="Stepping up to bat" FontSize="13" Margin="0,4"
                              IsChecked="{Binding NotifyBatting}" />
                    <CheckBox Content="Taking the mound" FontSize="13" Margin="0,4"
                              IsChecked="{Binding NotifyPitching}" />
                    <CheckBox Content="At-bat results" FontSize="13" Margin="0,4"
                              IsChecked="{Binding NotifyAtBatResult}" />
                    <CheckBox Content="Pitching results" FontSize="13" Margin="0,4"
                              IsChecked="{Binding NotifyPitchingResult}" />
                    <CheckBox Content="Not in lineup" FontSize="13" Margin="0,4"
                              IsChecked="{Binding NotifyNotInLineup}" />
                </StackPanel>
            </Border>

            <!-- Links -->
            <TextBlock Text="Links" Style="{StaticResource SectionHeader}" />
            <Border Style="{StaticResource Card}">
                <StackPanel>
                    <TextBlock Margin="0,4">
                        <Hyperlink NavigateUri="https://github.com/chefBrian/onDeck"
                                   Foreground="{DynamicResource OnDeck.Accent.Blue}"
                                   RequestNavigate="OnLink">GitHub</Hyperlink>
                    </TextBlock>
                    <TextBlock Margin="0,4">
                        <Hyperlink NavigateUri="https://github.com/chefBrian/onDeck/issues"
                                   Foreground="{DynamicResource OnDeck.Accent.Blue}"
                                   RequestNavigate="OnLink">Report a Bug</Hyperlink>
                    </TextBlock>
                </StackPanel>
            </Border>

        </StackPanel>
    </ScrollViewer>
</Window>
```

- [ ] **Step 2: Create the code-behind**

Create `src/OnDeck.App/Windows/SettingsWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using OnDeck.App.Platform;
using OnDeck.App.Views;
using OnDeck.Core;

namespace OnDeck.App.Windows;

/// <summary>
/// Port of <c>Views/SettingsView.swift</c>: the Fantrax roster URL and team picker, sync status
/// and Sync Now, the display and notification toggles, and the GitHub links.
/// <para>
/// The toggles two-way bind to a <see cref="SettingsEditor"/>; everything else is rendered from a
/// <see cref="SettingsFormState"/>. Neither the URL box nor a checkbox is ever written by
/// <see cref="Render"/> — a re-render is triggered by the very writes those controls make, so
/// writing back would fight the user mid-edit.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppOrchestrator _orchestrator;
    private readonly ISettingsStore _settings;
    private readonly SettingsEditor _editor;

    /// <summary>Set while <see cref="Render"/> assigns the picker, whose own SelectionChanged
    /// would otherwise write the placeholder back over a real selection.</summary>
    private bool _isRendering;

    public SettingsWindow(AppOrchestrator orchestrator, ISettingsStore settings)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _editor = new SettingsEditor(settings);

        InitializeComponent();

        DataContext = _editor;

        // Swift's didSet: every write re-reads settings and rebuilds the lists locally.
        _editor.Changed += _orchestrator.SettingsChanged;
        _orchestrator.StateChanged += Render;

        // Seeded once. Render must never touch this again - see the class comment.
        RosterUrlBox.Text = _editor.RosterUrl;

        // A window closed with text typed but never submitted should still keep it.
        Closing += (_, _) => CommitRosterUrl();

        Closed += (_, _) =>
        {
            _orchestrator.StateChanged -= Render;
            _editor.Changed -= _orchestrator.SettingsChanged;
        };

        Render();
    }

    private void Render()
    {
        var state = SettingsFormState.Build(
            SettingsInputFactory.From(_orchestrator, _settings), DateTimeOffset.Now);

        _isRendering = true;
        try
        {
            if (!state.TeamOptions.SequenceEqual(
                    TeamPicker.ItemsSource as IEnumerable<TeamOption> ?? []))
            {
                TeamPicker.ItemsSource = state.TeamOptions;
            }

            TeamPicker.SelectedValue = state.SelectedTeamOptionId;
        }
        finally
        {
            _isRendering = false;
        }

        Show(LoadingTeamsRow, state.ShowsLoadingTeams);
        Show(TeamPickerRow, state.ShowsTeamPicker);
        Show(LoadTeamsButton, state.ShowsLoadTeamsButton);

        TeamsErrorText.Text = state.TeamsErrorText ?? "";
        Show(TeamsErrorText, state.ShowsTeamsError);

        Show(SyncSpinner, state.ShowsSyncSpinner);
        SyncStatusText.Text = state.SyncStatusText ?? "";
        Show(SyncStatusText, state.ShowsSyncStatus);
        SyncNowButton.IsEnabled = state.IsSyncNowEnabled;

        SyncErrorText.Text = state.SyncErrorText ?? "";
        Show(SyncErrorText, state.ShowsSyncError);

        PlayerCountText.Text = state.PlayerCountText ?? "";
        Show(PlayerCountText, state.ShowsPlayerCount);
    }

    /// <summary>
    /// Swift's <c>.onSubmit</c>. The URL is committed first: <c>ParsedLeagueId</c> reads it back
    /// off the store, so fetching before the write would use the previous URL.
    /// </summary>
    private void OnRosterUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        CommitRosterUrl();
        _ = _orchestrator.FetchTeamsAsync();
    }

    private void OnRosterUrlLostFocus(object sender, RoutedEventArgs e) => CommitRosterUrl();

    private void CommitRosterUrl() => _editor.RosterUrl = RosterUrlBox.Text;

    private void OnTeamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRendering) return;

        _editor.SelectedTeamId = TeamPicker.SelectedValue as string ?? "";
    }

    private void OnLoadTeams(object sender, RoutedEventArgs e) => _ = _orchestrator.FetchTeamsAsync();

    private void OnSyncNow(object sender, RoutedEventArgs e) => _ = _orchestrator.ResyncRosterAsync();

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        ExternalLink.Open(e.Uri);
        e.Handled = true;
    }

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
```

- [ ] **Step 3: Build and confirm the window compiles**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet build windows/OnDeck.slnx
```
Expected: build succeeds. A misspelled `x:Name`, a resource key that isn't declared in this file, or a handler whose signature doesn't match fails here — that is the guard XAML gets for free, and the reason a headless test of this file would add nothing.

- [ ] **Step 4: Run the full suite**

```bash
dotnet test windows/OnDeck.slnx 2>&1 | grep -E "Passed!|Failed!|error MSB"
```
Expected: **TWO** `Passed!` lines, `Failed: 0` on both. One `Passed!` line means `OnDeck.App.Tests` didn't build — kill the app and re-run.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Windows/SettingsWindow.xaml windows/src/OnDeck.App/Windows/SettingsWindow.xaml.cs
git commit -m "phase 8: settings window"
```

---

## Task 6: The two entry points Phase 7b deferred

**Files:**
- Modify: `src/OnDeck.App/Views/FooterBar.xaml` (the button, first in the `StackPanel` at line 36)
- Modify: `src/OnDeck.App/Views/FooterBar.xaml.cs` (class comment at :7-10, a new event, a handler)
- Modify: `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs` (forward the event)
- Modify: `src/OnDeck.App/Tray/TrayIconService.cs` (class comment at :9-13, a new event, the menu item)
- Modify: `src/OnDeck.App/App.xaml.cs` (own the window)

**Interfaces:**
- Consumes: `SettingsWindow(AppOrchestrator, ISettingsStore)` (Task 5).
- Produces: `FooterBar.SettingsRequested`, `FlyoutWindow.SettingsRequested`, `TrayIconService.SettingsRequested` — all `event Action?`, matching the existing `FantraxRequested` / `FloatRequested` shape.

**Why now and not in 7b.** `TrayIconService`'s own doc comment sets the convention: a button ships with the window it opens. Both entry points existed in the 7b plan and were cut for exactly that reason.

**Ordering matters.** `MenuBarView.swift:846` puts Settings **first** in the footer row, before Fantrax. `PORT_PLAN.md`'s tray-menu line is `Open, Float, Settings, Refresh, Quit`, so the menu item goes **between Float and Refresh**.

- [ ] **Step 1: Add the footer button**

In `src/OnDeck.App/Views/FooterBar.xaml`, insert as the **first** child of the left-hand `StackPanel`, immediately above `FantraxButton`:

```xml
            <Button x:Name="SettingsButton" Style="{StaticResource FooterButton}" Click="OnSettings">
                <StackPanel>
                    <TextBlock Text="&#xE713;" FontFamily="{StaticResource FooterIconFont}"
                               FontSize="16" Height="20" HorizontalAlignment="Center" />
                    <TextBlock Text="Settings" FontSize="10" Margin="0,3,0,0"
                               HorizontalAlignment="Center" />
                </StackPanel>
            </Button>

```

(`E713` is Segoe Fluent Icons' `Setting` gear, the counterpart of Swift's `systemIcon: "gear"`.)

- [ ] **Step 2: Raise the event from the footer**

In `src/OnDeck.App/Views/FooterBar.xaml.cs`, replace the class comment:

```csharp
/// <summary>
/// Port of <c>FooterButtons</c> in <c>Views/MenuBarView.swift</c>. Settings is absent by design
/// until Phase 8 brings the window it would open.
/// </summary>
```

with:

```csharp
/// <summary>
/// Port of <c>FooterButtons</c> in <c>Views/MenuBarView.swift</c>: Settings, Fantrax, Refresh,
/// Float, and Quit on the right.
/// </summary>
```

Add the event beside the others, above `FantraxRequested`:

```csharp
    public event Action? SettingsRequested;

    public event Action? FantraxRequested;
```

Add the handler beside `OnFantrax`:

```csharp
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnFantrax(object sender, RoutedEventArgs e) => FantraxRequested?.Invoke();
```

- [ ] **Step 3: Forward it from the flyout**

In `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs`, add to the constructor's wiring block, just above the `Footer.FantraxRequested` line:

```csharp
        Footer.SettingsRequested += () =>
        {
            Hide();     // Swift dismisses the menu bar window before opening Settings
            SettingsRequested?.Invoke();
        };
```

and declare the event beside `FloatRequested`:

```csharp
    /// <summary>The footer's Settings button; the app owns the window itself.</summary>
    public event Action? SettingsRequested;
```

- [ ] **Step 4: Add the tray menu item**

In `src/OnDeck.App/Tray/TrayIconService.cs`, replace the last sentence of the class comment:

```csharp
/// same text the Mac menu bar title would, and a right-click menu. Settings arrives with its
/// window in Phase 8.
```

with:

```csharp
/// same text the Mac menu bar title would, and a right-click menu: Open, Float, Settings,
/// Refresh, Quit.
```

Add the event after `FloatRequested`:

```csharp
    public event Action? FloatRequested;

    public event Action? SettingsRequested;

```

and the item in `BuildMenu`, between `floatPanel` and `refresh`:

```csharp
        var settings = new MenuItem { Header = "Settings" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        var refresh = new MenuItem { Header = "Refresh" };
```

```csharp
        menu.Items.Add(open);
        menu.Items.Add(floatPanel);
        menu.Items.Add(settings);
        menu.Items.Add(refresh);
```

- [ ] **Step 5: Own the window in the composition root**

In `src/OnDeck.App/App.xaml.cs`, add the two fields beside the others:

```csharp
    private SettingsStore? _settingsStore;
    private SettingsWindow? _settingsWindow;
```

Keep the store in that field where it is constructed:

```csharp
        var settings = new SettingsStore();
        _settingsStore = settings;
```

Wire both entry points — the tray line goes with the other `_tray` handlers, the flyout line beside `_flyout.FloatRequested`:

```csharp
        _tray.SettingsRequested += OpenSettings;
```

```csharp
        _flyout.FloatRequested += ToggleFloat;
        _flyout.SettingsRequested += OpenSettings;
```

Add the method beside `ToggleFloat`:

```csharp
    /// <summary>
    /// One window at a time, released when it closes. macOS flips the activation policy to
    /// <c>.accessory</c> on dismissal so the OS can unload the Settings infrastructure
    /// (<c>SettingsView.swift:118-122</c>); Windows has no equivalent, so the closest thing is to
    /// actually let the window go and rebuild it on the next request.
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_orchestrator!, _settingsStore!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }
```

and close it on exit, beside `_panel?.Close()`:

```csharp
        _panel?.Close();
        _settingsWindow?.Close();
```

- [ ] **Step 6: Build and run the full suite**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet test windows/OnDeck.slnx 2>&1 | grep -E "Passed!|Failed!|error MSB"
```
Expected: **TWO** `Passed!` lines, `Failed: 0` on both.

- [ ] **Step 7: Commit**

```bash
git add windows/src/OnDeck.App/Views/FooterBar.xaml windows/src/OnDeck.App/Views/FooterBar.xaml.cs windows/src/OnDeck.App/Windows/FlyoutWindow.xaml.cs windows/src/OnDeck.App/Tray/TrayIconService.cs windows/src/OnDeck.App/App.xaml.cs
git commit -m "phase 8: settings entry points in the footer and tray"
```

---

## Task 7: Verification and close-out

**Files:**
- Modify: `windows/plans/2026-08-08-phase-8-settings-window.md` (this file — the Deviations section below)
- Modify: `windows/HANDOFF.md` (§8 deviations table, §8b verification table, §9 → Phase 9, the QA carry-over list)

- [ ] **Step 1: Run every automated gate**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"

dotnet test windows/OnDeck.slnx 2>&1 | grep -E "Passed!|Failed!|error MSB"
grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj

dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true

git status --short
```
Expected: TWO `Passed!` lines with `Failed: 0`; `0` package references in Core; publish succeeds; working tree clean.

- [ ] **Step 2: Run the app and exercise the window**

```bash
powershell -NoProfile -Command "Get-Process -Name 'OnDeck.App' -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet build windows/OnDeck.slnx
start "" "windows/src/OnDeck.App/bin/Debug/net10.0-windows/OnDeck.App.exe"
```

Walk this list. A live Fantrax roster is already configured in `%APPDATA%\onDeck\settings.json`, so the loaded-state paths are reachable immediately; **back that file up first** if you intend to clear the URL to see the empty states.

| Check | Where it comes from |
|---|---|
| Footer Settings button is first in the row, gear glyph, and opens the window | `MenuBarView.swift:846` |
| Tray right-click → Settings sits between Float and Refresh, and opens the same window | `PORT_PLAN.md` tray menu line |
| Opening from the footer hides the flyout first | `dismissMenu()` at `MenuBarView.swift:847` |
| A second Settings request focuses the open window rather than opening another | Task 6 |
| Text is readable on the cards in **both** Windows themes — switch the app theme live | XAML trap 1 |
| League URL shows the configured URL; editing and pressing Enter loads teams | `SettingsView.swift:14-17` |
| Team picker lists the league's teams with "Select a team..." first | `SettingsView.swift:29-34` |
| Picking a team enables Sync Now | `SettingsView.swift:65` |
| Sync Now shows "Syncing...", then "Last synced: N seconds ago" | `SettingsView.swift:49-58` |
| "N players loaded" appears after a successful sync | `SettingsView.swift:74-78` |
| Hide bench players re-filters the flyout **immediately**, with no network sync | `AppState.swift:41-47` |
| Closing and reopening the window shows every toggle in the state it was left in | round-trip through `settings.json` |
| Both links open in the browser | `SettingsView.swift:108-111` |
| Closing Settings does **not** quit the app; the tray icon stays | `ShutdownMode="OnExplicitShutdown"` |

- [ ] **Step 3: Have the owner look at it**

Automated screen capture has produced confidently wrong conclusions twice on this project. Ask the owner to open the window and check type, colour and spacing against the Mac Settings pane before the phase is called done. Note anything they flag as a follow-up row in HANDOFF §8b rather than fixing it silently.

- [ ] **Step 4: Record the deviations and update the handoff**

Fill in the Deviations section below with anything that diverged in execution, then append the Phase 8 rows to `HANDOFF.md` §8, add the manual results to the §8b table, and rewrite §9 to brief Phase 9 (notifications) — starting with the `net10.0-windows10.0.17763.0` bump the toast compat APIs need.

- [ ] **Step 5: Commit**

```bash
git add windows/plans/2026-08-08-phase-8-settings-window.md windows/HANDOFF.md
git commit -m "phase 8: verification results and phase 9 handoff"
```

---

## Deviations from the Swift original

*Executed 2026-08-08. Nothing diverged from the plan during execution — the list below is as
written up front. Owner verified the window on Windows 11 build 26200: all seven manual checks
passed (HANDOFF §8b), including readability in both themes on a live theme change, which is the
one that caught 7b out.*

| Deviation | Why |
|---|---|
| The League URL commits on Enter, focus loss and window close — not per keystroke | SwiftUI's binding writes UserDefaults per character; `SettingsStore` rewrites `settings.json` through a temp-file-and-move on every write. Enter still runs `FetchTeamsAsync`, which is `.onSubmit` |
| `SettingsEditor` (an `INotifyPropertyChanged` write-through) replaces `@Bindable` | WPF's binding target must raise `PropertyChanged`. It also puts all nine `didSet` bodies in one tested class instead of nine XAML event handlers |
| Notification toggles also call `SettingsChanged()`; Swift writes UserDefaults only | One write path for all nine settings. The call is a local list rebuild with no network — the cost of the extra generality is a rebuild nobody sees |
| `.formStyle(.grouped)` is hand-built from a section header plus a card `Border`, on two new palette keys | WPF has no `Form`. Making the window and card colours explicit also removes the risk of a window inheriting a theme background that disagrees with `OnDeck.Text.*` |
| An indeterminate `ProgressBar` stands in for `ProgressView().controlSize(.small)` | WPF has no built-in circular progress indicator; a thin indeterminate bar is the Fluent idiom |
| "Syncing..." and "Last synced: …" share one caption-sized run | Swift renders the first at `.body` and the second at `.caption`; they occupy the same slot here and a 3 pt swap mid-sync reads as a jump |
| The relative last-synced age is computed per render, not live-ticking | SwiftUI's `Text(date, style: .relative)` re-renders itself on a timer. Here it refreshes on `StateChanged`, which fires every poll cycle during a live game and on every settings write |
| `NSApplication.setActivationPolicy` is not ported; the window is released on close instead | It exists to let macOS reclaim ~230 MB of Settings infrastructure. Windows has no equivalent; dropping the reference on `Closed` is the nearest thing |
| No test instantiates the window | WPF fails the build on a bad `x:Name`, template or resource key, and no headless test can judge colour, weight or size. The plain-class tests plus the human pass in Task 7 are the guard |

## Notes carried forward

- **Shared quirk, not a port bug:** configuring the roster *after* launch never schedules the 8 AM daily re-sync. `AppState.start()` (`AppState.swift:116`) returns early on an empty URL before `scheduleDailyRefresh()`, and `AppOrchestrator.StartAsync` mirrors it exactly. The next launch schedules it normally. Both platforms behave the same, so it is out of Phase 8's scope; worth raising with the owner as a shared-behaviour question.
- `PORT_PLAN.md`'s Phase 7 row claims the player row shows a headshot. It does not, and the parity-checklist line about headshots in the flyout and floating panel is wrong — `HeadshotCache` is for notification images only. Correct it when Phase 9 touches that checklist.
