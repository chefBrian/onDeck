# Phase 7b: The flyout's real content — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Subagents are not used on this project** (session rule) — inline execution only.

**Goal:** Replace the flyout's one-line placeholder with the real UI from `Views/MenuBarView.swift`: live/upcoming/done player rows, the four sections plus empty and error states, a footer with the four-state Refresh button, and a `FloatingPanelWindow` that remembers where it was.

**Architecture:** Same rule 7a established — *anything that can be wrong without looking wrong goes in a plain class with tests; XAML binds to plain properties and holds no logic.* Core already resolved every field a row needs onto `PlayerDisplay`, and 7a mapped those onto dots/glyphs/badges. 7b adds one more pure layer between them and XAML: **row view-models** (`LiveRowViewModel`, `UpcomingRowViewModel`, `DoneRowViewModel`) built by a static factory, and a **sections model** that decides which sections show, which dividers appear, and what the empty/error text says. XAML binds to those records directly — no converters, no computation in templates.

**Tech Stack:** WPF on `net10.0-windows` with .NET 10's native Fluent theme, xunit in `OnDeck.App.Tests`, `Microsoft.Extensions.TimeProvider.Testing` for the Refresh button's hold timer.

## Global Constraints

- `OnDeck.Core` keeps **zero** package references. Verify with `grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj` → `0`.
- **Do not recompute Core's work in the shell.** Proximity, sort keys, stat lines, lineup badges, delay classification and list ordering are already resolved onto `PlayerDisplay` by `AppOrchestrator`/`DisplayRules`. Row view-models *project*; they never re-derive.
- Single-file publish stays green:
  `dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`
- **Do not touch the DWM backdrop path.** `windows/ACRYLIC-OPEN-ISSUE.md` documents an open cosmetic bug and the dead ends already tried. In particular **do not remove `ThemeMode="System"` from `App.xaml`** — that was tried, wrongly reported as a fix, and reverted.
- Commits go **directly to `main`**, one per task. **Never append `Co-Authored-By` or any AI-attribution trailer.**
- Don't install the app. Building to `./build`-equivalent output and launching locally to look at it is allowed; `dotnet build` / `dotnet test` are expected.
- Commands run from the repo root (`c:\Users\brian\Code\onDeck`). A bare `dotnet test` there fails — always pass `windows/OnDeck.slnx`.

## Scope

**In:** `PlayerRow` (live, upcoming, done variants), ACTIVE NOW / IN GAME / UPCOMING / DONE sections, empty-state text, error row, section dividers, stream-link click, footer (Fantrax, Refresh, Float, Quit), `FloatingPanelWindow` with persisted frame and drag-by-background, tray **Float** menu item, auto-open at launch when `AlwaysOpenPopout`.

**Out (deliberate):**
- **Settings footer button and tray Settings item → Phase 8.** `TrayIconService.cs:9-13` already states the convention: a button arrives with the window it opens. A dead button is worse than a missing one.
- The `#if DEBUG` memory overlay (`MenuBarView.swift:115-124`) — `MemoryStats` is macOS-only and explicitly not ported (`PORT_PLAN.md` porting map).
- `matchedGeometryEffect` row-reorder animation (`MenuBarView.swift:181`). WPF has no equivalent primitive; rows are replaced wholesale on each rebuild. Recorded as a deviation.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/OnDeck.App/Views/ThemePalette.cs` | Light/dark colour set + application into a `ResourceDictionary` |
| `src/OnDeck.App/Tray/ThemeWatcher.cs` | *(modify)* also expose `AppsUseLightTheme` — the app-surface theme, distinct from the taskbar's |
| `src/OnDeck.App/Views/RowViewModels.cs` | `LiveRowViewModel` / `UpcomingRowViewModel` / `DoneRowViewModel` + `RowViewModel.From(...)` factories |
| `src/OnDeck.App/Views/FlyoutSections.cs` | `FlyoutInput` → `FlyoutSections`: visibility, dividers, empty text, error text, which header owns the floating controls |
| `src/OnDeck.App/Views/RefreshButtonModel.cs` | idle→spinning→done/failed→idle state machine over `ResyncRosterAsync` |
| `src/OnDeck.App/Views/TeamLogoStore.cs` | Synchronous path lookup + de-duplicated background fetch, over Core's `TeamLogoCache` |
| `src/OnDeck.App/Views/FlyoutContent.xaml(.cs)` | The section list. Shared verbatim by the flyout and the floating panel |
| `src/OnDeck.App/Views/FooterBar.xaml(.cs)` | Fantrax / Refresh / Float / Quit |
| `src/OnDeck.App/Windows/FlyoutWindow.xaml(.cs)` | *(modify)* host `FlyoutContent` + `FooterBar` instead of the placeholder |
| `src/OnDeck.App/Windows/FloatingPanelWindow.xaml(.cs)` | Always-on-top no-activate panel hosting `FlyoutContent` |
| `src/OnDeck.App/Windows/FloatingPanelPlacement.cs` | Is a remembered frame still on a connected monitor? |
| `src/OnDeck.App/Platform/ExternalLink.cs` | `Process.Start` a URL without taking the app down |
| `src/OnDeck.App/SettingsStore.cs` | *(modify)* shell-only floating-panel frame, outside `ISettingsStore` |
| `src/OnDeck.App/App.xaml.cs` | *(modify)* palette application, Float wiring, auto-open |
| `tests/OnDeck.App.Tests/ThemePaletteTests.cs` | Palette completeness and light/dark difference |
| `tests/OnDeck.App.Tests/RowViewModelTests.cs` | Row projection rules |
| `tests/OnDeck.App.Tests/FlyoutSectionsTests.cs` | Section visibility, dividers, empty/error text |
| `tests/OnDeck.App.Tests/RefreshButtonModelTests.cs` | The four states and the re-entrancy guard |
| `tests/OnDeck.App.Tests/TeamLogoStoreTests.cs` | Path lookup, fetch de-duplication, change notification |
| `tests/OnDeck.App.Tests/FloatingPanelPlacementTests.cs` | Off-screen frame rejection |
| `tests/OnDeck.App.Tests/SettingsStoreTests.cs` | *(modify)* frame round-trip |

---

## Task 1: Theme palette

**Files:**
- Create: `src/OnDeck.App/Views/ThemePalette.cs`
- Create: `tests/OnDeck.App.Tests/ThemePaletteTests.cs`
- Modify: `src/OnDeck.App/Tray/ThemeWatcher.cs`

**Interfaces:**
- Produces: `sealed record ThemePalette` with `static ThemePalette For(bool appsUseLightTheme)`, `IReadOnlyDictionary<string, Color> Colors`, `static IReadOnlyList<string> Keys`, `void ApplyTo(ResourceDictionary resources)`.
- Produces: `ThemeWatcher.AppsUseLightTheme` (bool), alongside the existing `SystemUsesLightTheme`.
- Consumed by: every XAML file in Tasks 6–9 via `{DynamicResource OnDeck.*}`.

**Why this exists.** `MenuBarView.swift` leans on SwiftUI semantic colours (`.secondary`, `.quaternary`, `.green`, `.orange`, `.red`, `.blue`, `.white`, `.gray.opacity(0.3)`). WPF's Fluent theme has its own resource keys, but a `DynamicResource` naming a key that doesn't exist **fails silently** — the brush is simply null and the element renders invisible. That is the same class of silent-visual-failure that cost the last session on the acrylic bug. Owning the keys removes the guess entirely, and `ThemeWatcher` is already proven to fire on live theme changes (HANDOFF §8b).

**Why a second registry value.** `SystemUsesLightTheme` describes the **taskbar** — it is what picks the tray icon. App surfaces follow `AppsUseLightTheme`, which users can and do set independently (light apps, dark taskbar is the Windows 11 default pairing). Driving flyout text off the taskbar value gives dark text on a dark flyout for anyone using that default.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/ThemePaletteTests.cs`:

```csharp
using System.Windows;
using System.Windows.Media;
using OnDeck.App.Views;

namespace OnDeck.App.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void Keys_AreCoveredByBothThemes()
    {
        var light = ThemePalette.For(appsUseLightTheme: true);
        var dark = ThemePalette.For(appsUseLightTheme: false);

        foreach (var key in ThemePalette.Keys)
        {
            Assert.True(light.Colors.ContainsKey(key), $"light palette is missing {key}");
            Assert.True(dark.Colors.ContainsKey(key), $"dark palette is missing {key}");
        }

        Assert.Equal(ThemePalette.Keys.Count, light.Colors.Count);
        Assert.Equal(ThemePalette.Keys.Count, dark.Colors.Count);
    }

    [Fact]
    public void TextInvertsBetweenThemes()
    {
        var light = ThemePalette.For(appsUseLightTheme: true);
        var dark = ThemePalette.For(appsUseLightTheme: false);

        // Dark theme wants light text and vice versa - a palette that got this backwards
        // renders the whole flyout unreadable.
        Assert.True(Brightness(light.Colors["OnDeck.Text.Primary"]) < 0.5);
        Assert.True(Brightness(dark.Colors["OnDeck.Text.Primary"]) > 0.5);
    }

    [Fact]
    public void SecondaryTextIsDimmerThanPrimary()
    {
        var dark = ThemePalette.For(appsUseLightTheme: false);

        Assert.True(dark.Colors["OnDeck.Text.Secondary"].A < dark.Colors["OnDeck.Text.Primary"].A);
    }

    [Fact]
    public void ApplyTo_PublishesEveryKeyAsABrush()
    {
        var resources = new ResourceDictionary();

        ThemePalette.For(appsUseLightTheme: false).ApplyTo(resources);

        foreach (var key in ThemePalette.Keys)
        {
            var brush = Assert.IsType<SolidColorBrush>(resources[key]);
            Assert.True(brush.IsFrozen);
        }
    }

    [Fact]
    public void ApplyTo_ReplacesAnEarlierPalette()
    {
        var resources = new ResourceDictionary();

        ThemePalette.For(appsUseLightTheme: false).ApplyTo(resources);
        ThemePalette.For(appsUseLightTheme: true).ApplyTo(resources);

        var brush = (SolidColorBrush)resources["OnDeck.Text.Primary"];
        Assert.Equal(ThemePalette.For(appsUseLightTheme: true).Colors["OnDeck.Text.Primary"], brush.Color);
    }

    private static double Brightness(Color color) =>
        ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ThemePaletteTests
```
Expected: build failure — `ThemePalette` does not exist.

- [ ] **Step 3: Implement the palette**

Create `src/OnDeck.App/Views/ThemePalette.cs`:

```csharp
using System.Windows;
using System.Windows.Media;

namespace OnDeck.App.Views;

/// <summary>
/// The semantic colours <c>Views/MenuBarView.swift</c> uses, resolved for the current Windows
/// app theme and published as frozen brushes under <c>OnDeck.*</c> resource keys.
/// <para>
/// WPF's own Fluent keys are deliberately not used: a <c>DynamicResource</c> naming a key that
/// isn't there resolves to null and renders nothing, with no build or runtime error. Owning the
/// keys makes a missing colour impossible rather than invisible.
/// </para>
/// </summary>
public sealed record ThemePalette
{
    public const string TextPrimary = "OnDeck.Text.Primary";
    public const string TextSecondary = "OnDeck.Text.Secondary";
    public const string Divider = "OnDeck.Divider";
    public const string RowHover = "OnDeck.Row.Hover";
    public const string Green = "OnDeck.Accent.Green";
    public const string Orange = "OnDeck.Accent.Orange";
    public const string Red = "OnDeck.Accent.Red";
    public const string Blue = "OnDeck.Accent.Blue";
    public const string BaseOccupied = "OnDeck.Base.Occupied";
    public const string BaseEmpty = "OnDeck.Base.Empty";

    public static IReadOnlyList<string> Keys { get; } =
    [
        TextPrimary, TextSecondary, Divider, RowHover,
        Green, Orange, Red, Blue, BaseOccupied, BaseEmpty,
    ];

    private ThemePalette(IReadOnlyDictionary<string, Color> colors) => Colors = colors;

    public IReadOnlyDictionary<string, Color> Colors { get; }

    public static ThemePalette For(bool appsUseLightTheme) =>
        appsUseLightTheme ? Light() : Dark();

    /// <summary>Publishes every colour as a frozen <see cref="SolidColorBrush"/>, replacing any
    /// palette already there so a live theme change repaints without rebuilding the tree.</summary>
    public void ApplyTo(ResourceDictionary resources)
    {
        foreach (var (key, color) in Colors)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }

    private static ThemePalette Dark() => new(new Dictionary<string, Color>
    {
        [TextPrimary] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        [TextSecondary] = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF),   // SwiftUI .secondary
        [Divider] = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),         // SwiftUI .quaternary
        [RowHover] = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),        // .white.opacity(0.1)
        [Green] = Color.FromArgb(0xFF, 0x32, 0xD7, 0x4B),
        [Orange] = Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A),
        [Red] = Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A),
        [Blue] = Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF),
        [BaseOccupied] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        [BaseEmpty] = Color.FromArgb(0x4D, 0x80, 0x80, 0x80),        // .gray.opacity(0.3)
    });

    private static ThemePalette Light() => new(new Dictionary<string, Color>
    {
        [TextPrimary] = Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        [TextSecondary] = Color.FromArgb(0x8C, 0x00, 0x00, 0x00),
        [Divider] = Color.FromArgb(0x33, 0x00, 0x00, 0x00),
        [RowHover] = Color.FromArgb(0x14, 0x00, 0x00, 0x00),
        [Green] = Color.FromArgb(0xFF, 0x1D, 0x8A, 0x3D),
        [Orange] = Color.FromArgb(0xFF, 0xB2, 0x50, 0x00),
        [Red] = Color.FromArgb(0xFF, 0xD7, 0x00, 0x15),
        [Blue] = Color.FromArgb(0xFF, 0x00, 0x40, 0xDD),
        [BaseOccupied] = Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E),
        [BaseEmpty] = Color.FromArgb(0x4D, 0x80, 0x80, 0x80),
    });
}
```

- [ ] **Step 4: Run and confirm green**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~ThemePaletteTests
```
Expected: PASS.

- [ ] **Step 5: Add `AppsUseLightTheme` to the watcher**

In `src/OnDeck.App/Tray/ThemeWatcher.cs`, replace the class body's theme-reading parts so both values are tracked. The existing `SystemUsesLightTheme` semantics do not change — the tray icon still keys off the taskbar.

```csharp
    public ThemeWatcher()
    {
        SystemUsesLightTheme = ReadTheme("SystemUsesLightTheme");
        AppsUseLightTheme = ReadTheme("AppsUseLightTheme");
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the taskbar is light, which needs the dark icon.</summary>
    public bool SystemUsesLightTheme { get; private set; }

    /// <summary>
    /// True when app surfaces are light. Distinct from <see cref="SystemUsesLightTheme"/> —
    /// "light apps, dark taskbar" is a standard Windows 11 pairing, so driving the flyout's
    /// palette off the taskbar value would give dark text on a dark flyout.
    /// </summary>
    public bool AppsUseLightTheme { get; private set; }

    public event Action? Changed;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;

        var system = ReadTheme("SystemUsesLightTheme");
        var apps = ReadTheme("AppsUseLightTheme");
        if (system == SystemUsesLightTheme && apps == AppsUseLightTheme) return;

        SystemUsesLightTheme = system;
        AppsUseLightTheme = apps;
        Changed?.Invoke();
    }

    private static bool ReadTheme(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(valueName) is int value && value != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;       // assume the dark default
        }
    }
```

Delete the now-unused `ReadSystemUsesLightTheme`.

- [ ] **Step 6: Apply the palette at startup and on theme change**

In `src/OnDeck.App/App.xaml.cs`, after `_tray = new TrayIconService(_orchestrator);` add:

```csharp
        _theme = new ThemeWatcher();
        _theme.Changed += ApplyPalette;
        ApplyPalette();
```

Add the field and method:

```csharp
    private ThemeWatcher? _theme;

    private void ApplyPalette() =>
        ThemePalette.For(_theme!.AppsUseLightTheme).ApplyTo(Resources);
```

Add `using OnDeck.App.Views;` to the usings, and dispose in `OnExit`:

```csharp
        _theme?.Dispose();
```

(`TrayIconService` keeps its own `ThemeWatcher` for the icon; two registry watchers cost nothing and keep the tray independent of the window layer.)

- [ ] **Step 7: Build, run the suite, commit**

```bash
dotnet build windows/OnDeck.slnx
dotnet test windows/OnDeck.slnx
git add windows/src/OnDeck.App windows/tests/OnDeck.App.Tests/ThemePaletteTests.cs
git commit -m "phase 7b: theme palette for the flyout"
```
Expected: `Failed: 0`.

---

## Task 2: Row view-models

**Files:**
- Create: `src/OnDeck.App/Views/RowViewModels.cs`
- Create: `tests/OnDeck.App.Tests/RowViewModelTests.cs`

**Spec:** `MenuBarView.swift:332-518` (`LivePlayerRow`), `:539-623` (`UpcomingPlayerRow`), `:625-662` (`DonePlayerRow`), `:664-686` (`ScoreBlock`), `:720-761` (`BasesDiagram`, `OutsIndicator`).

**Interfaces:**
- Consumes: `DisplayFormatting.Dot/DelayGlyph/TrailingText/Badge/LineupBadgeText` (Task 7a), `PlayerDisplay` (Core).
- Produces:
  - `enum BaseState { Empty, Occupied, Highlighted }`
  - `sealed record LiveRowViewModel` — `PlayerId, Name, IsActive, Dot, StatLine, DelayGlyph, HasFeed, StreamUrl, AwayLogoPath, HomeLogoPath, AwayScore, HomeScore, First, Second, Third, IsTopHalf, InningText, CountText, Outs`
  - `sealed record UpcomingRowViewModel` — `PlayerId, Name, Badge, BadgeText, DelayGlyph, TrailingText`
  - `sealed record DoneRowViewModel` — `PlayerId, Name, StatLine`
  - `static class RowViewModel` with `LiveRowViewModel Live(PlayerDisplay display, Func<int, string?> logoPath)`, `UpcomingRowViewModel Upcoming(PlayerDisplay display)`, `DoneRowViewModel Done(PlayerDisplay display)`

**The rules being captured.** Each of these is a line of Swift that degrades quietly if mistranslated:

| Swift | Rule |
|---|---|
| `:444` | Count text is blank (`" "`) when the play is complete **or** the count is 0-0 — otherwise `"balls-strikes"` |
| `:434` | Inning arrow points up on `"Top"`, down otherwise |
| `:740-743` | A base diamond is green when its runner **is this player**, filled when occupied by anyone else, hollow when empty |
| `:401` | Name is semibold when active, medium otherwise |
| `:417` | The stat line indents 10pt when active, so it clears the dot |
| `:453-461` | No feed yet → the row collapses to name + a green "In Game" label |

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/RowViewModelTests.cs`:

```csharp
using OnDeck.App.Views;
using OnDeck.Core.Models;

namespace OnDeck.App.Tests;

public class RowViewModelTests
{
    private const int PlayerId = 605141;
    private const int TeammateId = 660271;

    private static Player Hitter(int id = PlayerId) =>
        new(id, "Mookie Betts", "Los Angeles Dodgers",
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static LiveFeedData Feed() => new()
    {
        GameState = "Live",
        DetailedState = "In Progress",
        Inning = 7,
        InningHalf = "Bottom",
        HomeTeamId = 119,
        AwayTeamId = 137,
        HomeScore = 4,
        AwayScore = 2,
        Balls = 2,
        Strikes = 1,
        Outs = 1,
    };

    private static PlayerDisplay LiveDisplay(
        LiveFeedData? feed = null,
        bool isActive = false,
        BattingProximity? proximity = null,
        string? statLine = null,
        DelayIndicator delay = DelayIndicator.None) =>
        new()
        {
            Player = Hitter(),
            GamePk = 745804,
            Feed = feed,
            IsActive = isActive,
            Proximity = proximity,
            StatLine = statLine,
            Delay = delay,
            StreamUrl = new Uri("https://www.mlb.com/tv"),
        };

    private static string? NoLogos(int teamId) => null;

    [Fact]
    public void Live_CarriesTheIdentityFields()
    {
        var row = RowViewModel.Live(
            LiveDisplay(Feed(), isActive: true, statLine: "1-3 · RBI"), NoLogos);

        Assert.Equal(PlayerId, row.PlayerId);
        Assert.Equal("Mookie Betts", row.Name);
        Assert.True(row.IsActive);
        Assert.Equal("1-3 · RBI", row.StatLine);
        Assert.Equal(new Uri("https://www.mlb.com/tv"), row.StreamUrl);
    }

    [Fact]
    public void Live_HasNoFeedBeforeTheFirstPoll()
    {
        var row = RowViewModel.Live(LiveDisplay(feed: null), NoLogos);

        Assert.False(row.HasFeed);
    }

    [Fact]
    public void Live_TakesItsDotFromTheProximity()
    {
        Assert.Equal(
            ProximityDot.Outlined,
            RowViewModel.Live(LiveDisplay(Feed(), proximity: BattingProximity.OnDeck), NoLogos).Dot);
    }

    [Fact]
    public void Live_ShowsTheCountDuringAnAtBat()
    {
        var row = RowViewModel.Live(LiveDisplay(Feed()), NoLogos);

        Assert.Equal("2-1", row.CountText);
        Assert.Equal(1, row.Outs);
    }

    [Fact]
    public void Live_BlanksTheCountWhenThePlayIsComplete()
    {
        var feed = Feed();
        feed.IsPlayComplete = true;

        Assert.Equal(" ", RowViewModel.Live(LiveDisplay(feed), NoLogos).CountText);
    }

    [Fact]
    public void Live_BlanksTheCountAtZeroAndZero()
    {
        var feed = Feed();
        feed.Balls = 0;
        feed.Strikes = 0;

        Assert.Equal(" ", RowViewModel.Live(LiveDisplay(feed), NoLogos).CountText);
    }

    [Fact]
    public void Live_PointsTheInningArrowByHalf()
    {
        var top = Feed();
        top.InningHalf = "Top";

        Assert.True(RowViewModel.Live(LiveDisplay(top), NoLogos).IsTopHalf);
        Assert.False(RowViewModel.Live(LiveDisplay(Feed()), NoLogos).IsTopHalf);
        Assert.Equal("7", RowViewModel.Live(LiveDisplay(Feed()), NoLogos).InningText);
    }

    [Fact]
    public void Live_ShowsZeroInningWhenTheFeedHasNone()
    {
        var feed = Feed();
        feed.Inning = null;

        Assert.Equal("0", RowViewModel.Live(LiveDisplay(feed), NoLogos).InningText);
    }

    [Fact]
    public void Live_HighlightsABaseThisPlayerIsStandingOn()
    {
        var feed = Feed();
        feed.RunnerOnFirst = PlayerId;
        feed.RunnerOnSecond = TeammateId;

        var row = RowViewModel.Live(LiveDisplay(feed), NoLogos);

        Assert.Equal(BaseState.Highlighted, row.First);
        Assert.Equal(BaseState.Occupied, row.Second);
        Assert.Equal(BaseState.Empty, row.Third);
    }

    [Fact]
    public void Live_CarriesTheScoreBlock()
    {
        var row = RowViewModel.Live(LiveDisplay(Feed()), teamId => $"C:\\logos\\{teamId}.png");

        Assert.Equal(2, row.AwayScore);
        Assert.Equal(4, row.HomeScore);
        Assert.Equal("C:\\logos\\137.png", row.AwayLogoPath);
        Assert.Equal("C:\\logos\\119.png", row.HomeLogoPath);
    }

    [Fact]
    public void Live_CarriesTheDelayGlyph()
    {
        var row = RowViewModel.Live(LiveDisplay(Feed(), delay: DelayIndicator.Rain), NoLogos);

        Assert.Equal(DisplayFormatting.DelayGlyph(DelayIndicator.Rain), row.DelayGlyph);
        Assert.Null(RowViewModel.Live(LiveDisplay(Feed()), NoLogos).DelayGlyph);
    }

    [Fact]
    public void Upcoming_CarriesBadgeAndTrailingText()
    {
        var start = new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero);
        var display = new PlayerDisplay
        {
            Player = Hitter(),
            Lineup = LineupInfo.BattingOrder(2),
            StartTime = start,
        };

        var row = RowViewModel.Upcoming(display);

        Assert.Equal(PlayerId, row.PlayerId);
        Assert.Equal(LineupBadge.Order, row.Badge);
        Assert.Equal("2", row.BadgeText);
        Assert.Equal(start.ToLocalTime().ToString("t"), row.TrailingText);
        Assert.Null(row.DelayGlyph);
    }

    [Fact]
    public void Upcoming_ShowsPpdInsteadOfAStartTime()
    {
        var display = new PlayerDisplay
        {
            Player = Hitter(),
            Lineup = LineupInfo.NotInLineup,
            Delay = DelayIndicator.Postponed,
            StartTime = DateTimeOffset.UtcNow.AddHours(3),
        };

        var row = RowViewModel.Upcoming(display);

        Assert.Equal("PPD", row.TrailingText);
        Assert.Equal(LineupBadge.Missing, row.Badge);
        Assert.Equal(DisplayFormatting.DelayGlyph(DelayIndicator.Postponed), row.DelayGlyph);
    }

    [Fact]
    public void Done_IsNameAndStatLine()
    {
        var display = new PlayerDisplay
        {
            Player = Hitter(),
            GamePk = 745804,
            StatLine = "2-4 · HR, 3 RBI",
        };

        var row = RowViewModel.Done(display);

        Assert.Equal(PlayerId, row.PlayerId);
        Assert.Equal("Mookie Betts", row.Name);
        Assert.Equal("2-4 · HR, 3 RBI", row.StatLine);
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~RowViewModelTests
```
Expected: build failure — `RowViewModel` does not exist.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Views/RowViewModels.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.App.Views;

/// <summary>One base on the diamond. Highlighted means the row's own player is standing on it.</summary>
public enum BaseState
{
    Empty,
    Occupied,
    Highlighted,
}

/// <summary>
/// A row in ACTIVE NOW or IN GAME — the port of <c>LivePlayerRow</c>. Every field is already
/// resolved; the template binds and nothing more.
/// </summary>
public sealed record LiveRowViewModel
{
    public required int PlayerId { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; }
    public ProximityDot Dot { get; init; }
    public string? StatLine { get; init; }
    public string? DelayGlyph { get; init; }

    /// <summary>False until the first live feed lands; the row collapses to a name + "In Game".</summary>
    public bool HasFeed { get; init; }

    public Uri? StreamUrl { get; init; }

    public string? AwayLogoPath { get; init; }
    public string? HomeLogoPath { get; init; }
    public int AwayScore { get; init; }
    public int HomeScore { get; init; }

    public BaseState First { get; init; }
    public BaseState Second { get; init; }
    public BaseState Third { get; init; }

    public bool IsTopHalf { get; init; }
    public string InningText { get; init; } = "0";

    /// <summary>"balls-strikes", or a single space between at-bats so the row doesn't reflow.</summary>
    public string CountText { get; init; } = " ";

    public int Outs { get; init; }
}

/// <summary>A row in UPCOMING — the port of <c>UpcomingPlayerRow</c>. Not clickable on macOS either.</summary>
public sealed record UpcomingRowViewModel
{
    public required int PlayerId { get; init; }
    public required string Name { get; init; }
    public LineupBadge Badge { get; init; }
    public string? BadgeText { get; init; }
    public string? DelayGlyph { get; init; }
    public string TrailingText { get; init; } = "";
}

/// <summary>A row in DONE — the port of <c>DonePlayerRow</c>.</summary>
public sealed record DoneRowViewModel
{
    public required int PlayerId { get; init; }
    public required string Name { get; init; }
    public string? StatLine { get; init; }
}

/// <summary>Projects <see cref="PlayerDisplay"/> onto the three row shapes the templates bind to.</summary>
public static class RowViewModel
{
    /// <param name="logoPath">Team id to an on-disk logo, or null when it hasn't been fetched.</param>
    public static LiveRowViewModel Live(PlayerDisplay display, Func<int, string?> logoPath)
    {
        var feed = display.Feed;

        return new LiveRowViewModel
        {
            PlayerId = display.Id,
            Name = display.Name,
            IsActive = display.IsActive,
            Dot = DisplayFormatting.Dot(display),
            StatLine = display.StatLine,
            DelayGlyph = DisplayFormatting.DelayGlyph(display.Delay),
            HasFeed = feed is not null,
            StreamUrl = display.StreamUrl,

            AwayLogoPath = feed is null ? null : logoPath(feed.AwayTeamId),
            HomeLogoPath = feed is null ? null : logoPath(feed.HomeTeamId),
            AwayScore = feed?.AwayScore ?? 0,
            HomeScore = feed?.HomeScore ?? 0,

            First = BaseFor(feed?.RunnerOnFirst, display.Id),
            Second = BaseFor(feed?.RunnerOnSecond, display.Id),
            Third = BaseFor(feed?.RunnerOnThird, display.Id),

            IsTopHalf = feed?.InningHalf == "Top",
            InningText = (feed?.Inning ?? 0).ToString(),
            CountText = CountFor(feed),
            Outs = feed?.Outs ?? 0,
        };
    }

    public static UpcomingRowViewModel Upcoming(PlayerDisplay display) => new()
    {
        PlayerId = display.Id,
        Name = display.Name,
        Badge = DisplayFormatting.Badge(display),
        BadgeText = DisplayFormatting.LineupBadgeText(display),
        DelayGlyph = DisplayFormatting.DelayGlyph(display.Delay),
        TrailingText = DisplayFormatting.TrailingText(display),
    };

    public static DoneRowViewModel Done(PlayerDisplay display) => new()
    {
        PlayerId = display.Id,
        Name = display.Name,
        StatLine = display.StatLine,
    };

    private static BaseState BaseFor(int? runnerId, int playerId)
    {
        if (runnerId is not { } runner) return BaseState.Empty;
        return runner == playerId ? BaseState.Highlighted : BaseState.Occupied;
    }

    /// <summary>
    /// Blank between at-bats: MLB holds the previous count until the next pitch, so showing it
    /// would report a stale 3-2 while nobody is batting.
    /// </summary>
    private static string CountFor(LiveFeedData? feed)
    {
        if (feed is null) return " ";
        if (feed.IsPlayComplete) return " ";
        if (feed.Balls == 0 && feed.Strikes == 0) return " ";
        return $"{feed.Balls}-{feed.Strikes}";
    }
}
```

- [ ] **Step 4: Run and confirm green**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~RowViewModelTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Views/RowViewModels.cs windows/tests/OnDeck.App.Tests/RowViewModelTests.cs
git commit -m "phase 7b: row view-models"
```

---

## Task 3: Sections model

**Files:**
- Create: `src/OnDeck.App/Views/FlyoutSections.cs`
- Create: `tests/OnDeck.App.Tests/FlyoutSectionsTests.cs`

**Spec:** `MenuBarView.swift:102-113` (order), `:130-235` (the four sections and their dividers), `:237-267` (`EmptySection`), `:269-285` (`ErrorSection`), `:289-318` (`SectionHeader`'s `showClose`).

**Interfaces:**
- Consumes: `RowViewModel.Live/Upcoming/Done` (Task 2).
- Produces:
  - `enum FlyoutSectionKind { Active, InGame, Upcoming, Done, Empty }`
  - `sealed record FlyoutInput { Active, InGame, Upcoming, Done (IReadOnlyList<PlayerDisplay>), IsSyncing, HasRosterUrl, LoadedPlayerCount, Error }`
  - `sealed record FlyoutSections` with the three row lists, `EmptyText`, `ErrorText`, per-section `Shows*`/`*Divider` flags, and `HeaderControlsSection`
  - `static FlyoutSections Build(FlyoutInput input, bool isFloating, Func<int, string?> logoPath)`

**The rules being captured.**

*Empty text* (`:244-252`), in order: syncing → `"Syncing roster..."`; no roster URL → `"Set roster URL in Settings"`; no players loaded → `"No players found"`; otherwise → `"No games today"`. Shown only when all four lists are empty.

*Dividers.* Every visible section is followed by a divider, **except** in floating mode where the last content section gets an 8pt spacer instead (`:204-210`, `:228-232`, `:253-257`). Concretely: Upcoming keeps its divider unless floating with an empty Done; Done and Empty drop theirs when floating.

*Header controls* (`showClose`, `:137`/`:168`/`:196`/`:222`). The close + refresh buttons live in the **first visible section's** header, and only when floating. Swift renders no header at all when every list is empty, which on macOS leaves the panel closable only via the Float button. On Windows a borderless, taskbar-less window with no close affordance is worse, so `HeaderControlsSection` falls back to `Empty` and the empty state carries the buttons. Recorded as a deviation.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/FlyoutSectionsTests.cs`:

```csharp
using OnDeck.App.Views;
using OnDeck.Core.Models;

namespace OnDeck.App.Tests;

public class FlyoutSectionsTests
{
    private static PlayerDisplay Row(int id) =>
        new()
        {
            Player = new Player(id, $"Player {id}", "Los Angeles Dodgers",
                new HashSet<PlayerPosition> { PlayerPosition.Hitter },
                new HashSet<string> { "OF" },
                RosterStatus.Active),
        };

    private static FlyoutInput Loaded(FlyoutInput input) =>
        input with { HasRosterUrl = true, LoadedPlayerCount = 12 };

    private static string? NoLogos(int teamId) => null;

    private static FlyoutSections Build(FlyoutInput input, bool isFloating = false) =>
        FlyoutSections.Build(input, isFloating, NoLogos);

    [Fact]
    public void ProjectsEachListIntoItsRowType()
    {
        var sections = Build(Loaded(new FlyoutInput
        {
            Active = [Row(1)],
            InGame = [Row(2), Row(3)],
            Upcoming = [Row(4)],
            Done = [Row(5)],
        }));

        Assert.Equal(new[] { 1 }, sections.Active.Select(row => row.PlayerId));
        Assert.Equal(new[] { 2, 3 }, sections.InGame.Select(row => row.PlayerId));
        Assert.Equal(new[] { 4 }, sections.Upcoming.Select(row => row.PlayerId));
        Assert.Equal(new[] { 5 }, sections.Done.Select(row => row.PlayerId));
        Assert.Null(sections.EmptyText);
    }

    [Fact]
    public void SectionsAreHiddenWhenEmpty()
    {
        var sections = Build(Loaded(new FlyoutInput { InGame = [Row(2)] }));

        Assert.False(sections.ShowsActive);
        Assert.True(sections.ShowsInGame);
        Assert.False(sections.ShowsUpcoming);
        Assert.False(sections.ShowsDone);
        Assert.False(sections.ShowsEmpty);
    }

    [Theory]
    [InlineData(true, true, 0, "Syncing roster...")]
    [InlineData(false, false, 0, "Set roster URL in Settings")]
    [InlineData(false, true, 0, "No players found")]
    [InlineData(false, true, 12, "No games today")]
    public void EmptyTextExplainsWhyThereIsNothing(
        bool isSyncing, bool hasRosterUrl, int loadedPlayerCount, string expected)
    {
        var sections = Build(new FlyoutInput
        {
            IsSyncing = isSyncing,
            HasRosterUrl = hasRosterUrl,
            LoadedPlayerCount = loadedPlayerCount,
        });

        Assert.True(sections.ShowsEmpty);
        Assert.Equal(expected, sections.EmptyText);
    }

    [Fact]
    public void EmptyTextIsAbsentWhenAnySectionHasRows()
    {
        var sections = Build(Loaded(new FlyoutInput { Done = [Row(5)] }));

        Assert.False(sections.ShowsEmpty);
        Assert.Null(sections.EmptyText);
    }

    [Fact]
    public void ErrorTextSurfacesTheSyncError()
    {
        var sections = Build(Loaded(new FlyoutInput { Error = "Couldn't reach Fantrax" }));

        Assert.True(sections.ShowsError);
        Assert.Equal("Couldn't reach Fantrax", sections.ErrorText);
    }

    [Fact]
    public void EverySectionIsFollowedByADividerInTheFlyout()
    {
        var sections = Build(Loaded(new FlyoutInput
        {
            Active = [Row(1)],
            InGame = [Row(2)],
            Upcoming = [Row(3)],
            Done = [Row(4)],
        }));

        Assert.True(sections.ActiveDivider);
        Assert.True(sections.InGameDivider);
        Assert.True(sections.UpcomingDivider);
        Assert.True(sections.DoneDivider);
    }

    [Fact]
    public void FloatingDropsTheTrailingDivider()
    {
        var sections = Build(
            Loaded(new FlyoutInput { Upcoming = [Row(3)], Done = [Row(4)] }), isFloating: true);

        Assert.True(sections.UpcomingDivider);      // Done still follows it
        Assert.False(sections.DoneDivider);         // nothing follows Done
    }

    [Fact]
    public void FloatingDropsUpcomingsDividerWhenItIsLast()
    {
        var sections = Build(Loaded(new FlyoutInput { Upcoming = [Row(3)] }), isFloating: true);

        Assert.False(sections.UpcomingDivider);
    }

    [Fact]
    public void FloatingDropsTheEmptyStatesDivider()
    {
        Assert.False(Build(Loaded(new FlyoutInput()), isFloating: true).EmptyDivider);
        Assert.True(Build(Loaded(new FlyoutInput())).EmptyDivider);
    }

    [Fact]
    public void HeaderControlsLandOnTheFirstVisibleSection()
    {
        Assert.Equal(
            FlyoutSectionKind.Active,
            Build(Loaded(new FlyoutInput { Active = [Row(1)], InGame = [Row(2)] })).HeaderControlsSection);

        Assert.Equal(
            FlyoutSectionKind.InGame,
            Build(Loaded(new FlyoutInput { InGame = [Row(2)], Done = [Row(4)] })).HeaderControlsSection);

        Assert.Equal(
            FlyoutSectionKind.Upcoming,
            Build(Loaded(new FlyoutInput { Upcoming = [Row(3)], Done = [Row(4)] })).HeaderControlsSection);

        Assert.Equal(
            FlyoutSectionKind.Done,
            Build(Loaded(new FlyoutInput { Done = [Row(4)] })).HeaderControlsSection);
    }

    [Fact]
    public void HeaderControlsFallBackToTheEmptyStateWithNothingToShow()
    {
        // Swift renders no header at all here, leaving the panel closable only from the Float
        // button. A borderless window with no taskbar entry needs its own close affordance.
        Assert.Equal(FlyoutSectionKind.Empty, Build(Loaded(new FlyoutInput())).HeaderControlsSection);
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~FlyoutSectionsTests
```
Expected: build failure — `FlyoutSections` does not exist.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Views/FlyoutSections.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.App.Views;

/// <summary>Identifies a section so the floating panel knows which header owns its buttons.</summary>
public enum FlyoutSectionKind
{
    Active,
    InGame,
    Upcoming,
    Done,
    Empty,
}

/// <summary>
/// Everything <c>MenuBarView.swift</c> reads off <c>AppState</c> to lay the flyout out, as plain
/// values. Taking this rather than the orchestrator keeps the layout rules testable without
/// standing up the whole engine.
/// </summary>
public sealed record FlyoutInput
{
    public IReadOnlyList<PlayerDisplay> Active { get; init; } = [];
    public IReadOnlyList<PlayerDisplay> InGame { get; init; } = [];
    public IReadOnlyList<PlayerDisplay> Upcoming { get; init; } = [];
    public IReadOnlyList<PlayerDisplay> Done { get; init; } = [];
    public bool IsSyncing { get; init; }
    public bool HasRosterUrl { get; init; }
    public int LoadedPlayerCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The laid-out flyout: which sections appear, which dividers follow them, and the empty/error
/// copy. Port of the section structure in <c>Views/MenuBarView.swift</c>.
/// </summary>
public sealed record FlyoutSections
{
    public IReadOnlyList<LiveRowViewModel> Active { get; init; } = [];
    public IReadOnlyList<LiveRowViewModel> InGame { get; init; } = [];
    public IReadOnlyList<UpcomingRowViewModel> Upcoming { get; init; } = [];
    public IReadOnlyList<DoneRowViewModel> Done { get; init; } = [];

    public string? EmptyText { get; init; }
    public string? ErrorText { get; init; }

    public bool ShowsActive => Active.Count > 0;
    public bool ShowsInGame => InGame.Count > 0;
    public bool ShowsUpcoming => Upcoming.Count > 0;
    public bool ShowsDone => Done.Count > 0;
    public bool ShowsEmpty => EmptyText is not null;
    public bool ShowsError => ErrorText is not null;

    public bool ActiveDivider { get; init; }
    public bool InGameDivider { get; init; }
    public bool UpcomingDivider { get; init; }
    public bool DoneDivider { get; init; }
    public bool EmptyDivider { get; init; }
    public bool ErrorDivider { get; init; }

    /// <summary>Whose header carries the floating panel's refresh and close buttons.</summary>
    public FlyoutSectionKind HeaderControlsSection { get; init; }

    public static FlyoutSections Build(
        FlyoutInput input, bool isFloating, Func<int, string?> logoPath)
    {
        var active = input.Active.Select(row => RowViewModel.Live(row, logoPath)).ToList();
        var inGame = input.InGame.Select(row => RowViewModel.Live(row, logoPath)).ToList();
        var upcoming = input.Upcoming.Select(RowViewModel.Upcoming).ToList();
        var done = input.Done.Select(RowViewModel.Done).ToList();

        var isEmpty = active.Count == 0 && inGame.Count == 0
                      && upcoming.Count == 0 && done.Count == 0;

        return new FlyoutSections
        {
            Active = active,
            InGame = inGame,
            Upcoming = upcoming,
            Done = done,
            EmptyText = isEmpty ? EmptyTextFor(input) : null,
            ErrorText = string.IsNullOrEmpty(input.Error) ? null : input.Error,

            // Everything gets a divider; floating mode drops it on whatever ends up last so the
            // panel doesn't end in a hanging rule.
            ActiveDivider = true,
            InGameDivider = true,
            UpcomingDivider = !isFloating || done.Count > 0,
            DoneDivider = !isFloating,
            EmptyDivider = !isFloating,
            ErrorDivider = true,

            HeaderControlsSection =
                active.Count > 0 ? FlyoutSectionKind.Active
                : inGame.Count > 0 ? FlyoutSectionKind.InGame
                : upcoming.Count > 0 ? FlyoutSectionKind.Upcoming
                : done.Count > 0 ? FlyoutSectionKind.Done
                : FlyoutSectionKind.Empty,
        };
    }

    private static string EmptyTextFor(FlyoutInput input)
    {
        if (input.IsSyncing) return "Syncing roster...";
        if (!input.HasRosterUrl) return "Set roster URL in Settings";
        if (input.LoadedPlayerCount == 0) return "No players found";
        return "No games today";
    }
}
```

- [ ] **Step 4: Run and confirm green**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~FlyoutSectionsTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Views/FlyoutSections.cs windows/tests/OnDeck.App.Tests/FlyoutSectionsTests.cs
git commit -m "phase 7b: flyout sections model"
```

---

## Task 4: Refresh button state machine

**Files:**
- Create: `src/OnDeck.App/Views/RefreshButtonModel.cs`
- Create: `tests/OnDeck.App.Tests/RefreshButtonModelTests.cs`
- Modify: `tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj`

**Spec:** `MenuBarView.swift:902-945` (footer button), `:954-1000` (the floating panel's smaller variant — same machine, different glyphs).

**Interfaces:**
- Produces: `enum RefreshButtonState { Idle, Spinning, Done, Failed }`, `sealed class RefreshButtonModel(TimeProvider? time = null)` with `State`, `event Action? Changed`, `Task ClickAsync(Func<Task<bool>> resync)`.

**Why a class.** Swift's version has three behaviours that break silently: the `guard state == .idle else { return }` re-entrancy guard (without it, a double-click fires two roster syncs), the success/failure split off `resyncRoster()`'s bool, and the 1.2 s hold before returning to idle. `TimeProvider` makes the hold testable instead of a `Thread.Sleep` in a test.

- [ ] **Step 1: Add the fake clock to the test project**

In `tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj`, add to the existing `PackageReference` group:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.0.0" />
```

(Same package and version `OnDeck.Core.Tests` already uses — confirm with
`grep TimeProvider windows/tests/OnDeck.Core.Tests/OnDeck.Core.Tests.csproj` and match it.)

- [ ] **Step 2: Write the failing test**

Create `tests/OnDeck.App.Tests/RefreshButtonModelTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using OnDeck.App.Views;

namespace OnDeck.App.Tests;

public class RefreshButtonModelTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.2);

    [Fact]
    public void StartsIdle()
    {
        Assert.Equal(RefreshButtonState.Idle, new RefreshButtonModel().State);
    }

    [Fact]
    public async Task ShowsDoneThenReturnsToIdleOnSuccess()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);

        var click = model.ClickAsync(() => Task.FromResult(true));

        Assert.Equal(RefreshButtonState.Done, model.State);
        time.Advance(Hold);
        await click;
        Assert.Equal(RefreshButtonState.Idle, model.State);
    }

    [Fact]
    public async Task ShowsFailedThenReturnsToIdleOnFailure()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);

        var click = model.ClickAsync(() => Task.FromResult(false));

        Assert.Equal(RefreshButtonState.Failed, model.State);
        time.Advance(Hold);
        await click;
        Assert.Equal(RefreshButtonState.Idle, model.State);
    }

    [Fact]
    public async Task SpinsWhileTheSyncIsInFlight()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);
        var sync = new TaskCompletionSource<bool>();

        var click = model.ClickAsync(() => sync.Task);

        Assert.Equal(RefreshButtonState.Spinning, model.State);
        sync.SetResult(true);
        await Task.Yield();
        time.Advance(Hold);
        await click;
        Assert.Equal(RefreshButtonState.Idle, model.State);
    }

    [Fact]
    public async Task IgnoresAClickWhileAlreadyRunning()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);
        var sync = new TaskCompletionSource<bool>();
        var calls = 0;

        var first = model.ClickAsync(() => { calls++; return sync.Task; });
        await model.ClickAsync(() => { calls++; return Task.FromResult(true); });

        Assert.Equal(1, calls);

        sync.SetResult(true);
        await Task.Yield();
        time.Advance(Hold);
        await first;
    }

    [Fact]
    public async Task RaisesChangedOnEveryTransition()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);
        var seen = new List<RefreshButtonState>();
        model.Changed += () => seen.Add(model.State);

        var click = model.ClickAsync(() => Task.FromResult(true));
        time.Advance(Hold);
        await click;

        Assert.Equal(
            new[] { RefreshButtonState.Spinning, RefreshButtonState.Done, RefreshButtonState.Idle },
            seen);
    }

    [Fact]
    public async Task ReturnsToIdleWhenTheSyncThrows()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);

        var click = model.ClickAsync(() => throw new InvalidOperationException("network"));

        Assert.Equal(RefreshButtonState.Failed, model.State);
        time.Advance(Hold);
        await click;
        Assert.Equal(RefreshButtonState.Idle, model.State);
    }
}
```

- [ ] **Step 3: Run and confirm failure**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~RefreshButtonModelTests
```
Expected: build failure — `RefreshButtonModel` does not exist.

- [ ] **Step 4: Implement**

Create `src/OnDeck.App/Views/RefreshButtonModel.cs`:

```csharp
namespace OnDeck.App.Views;

public enum RefreshButtonState
{
    Idle,
    Spinning,
    Done,
    Failed,
}

/// <summary>
/// The footer Refresh button's four states, ported from <c>FooterButtons.refreshButton</c> in
/// <c>Views/MenuBarView.swift</c>: spin during the sync, show the outcome for 1.2 s, then go
/// back to idle. Clicks during any of that are dropped — Swift's <c>guard state == .idle</c> —
/// so an impatient double-click can't fire two roster syncs.
/// </summary>
public sealed class RefreshButtonModel(TimeProvider? time = null)
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(1.2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public RefreshButtonState State { get; private set; } = RefreshButtonState.Idle;

    public event Action? Changed;

    public async Task ClickAsync(Func<Task<bool>> resync)
    {
        if (State != RefreshButtonState.Idle) return;

        Transition(RefreshButtonState.Spinning);

        bool success;
        try
        {
            success = await resync();
        }
        catch (Exception)
        {
            // A throwing sync is a failed sync; the button must not stick on the spinner.
            success = false;
        }

        Transition(success ? RefreshButtonState.Done : RefreshButtonState.Failed);

        await Task.Delay(HoldDuration, _time);

        Transition(RefreshButtonState.Idle);
    }

    private void Transition(RefreshButtonState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
```

Note the `throw` inside a lambda in the test compiles because `Func<Task<bool>>` bodies may throw synchronously; `ClickAsync`'s `try` covers both that and a faulted task.

- [ ] **Step 5: Run and confirm green**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~RefreshButtonModelTests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add windows/src/OnDeck.App/Views/RefreshButtonModel.cs windows/tests/OnDeck.App.Tests
git commit -m "phase 7b: refresh button state machine"
```

---

## Task 5: Team logo store

**Files:**
- Create: `src/OnDeck.App/Views/TeamLogoStore.cs`
- Create: `tests/OnDeck.App.Tests/StubHttpMessageHandler.cs`
- Create: `tests/OnDeck.App.Tests/TeamLogoStoreTests.cs`

**Spec:** `MenuBarView.swift:765-788` (`TeamLogo`'s lazy `.task(id: teamID)`).

**Interfaces:**
- Consumes: `OnDeck.Core.Utilities.TeamLogoCache` (Task 7a) — `FilePath(int, int)`, `GetAsync(int, int, CancellationToken)`.
- Produces: `sealed class TeamLogoStore(TeamLogoCache cache, int size = 32)` with `string? PathFor(int teamId)`, `void Prefetch(IEnumerable<int> teamIds)`, `event Action? Changed`.

**Why this layer.** Rows are rebuilt from scratch on every `StateChanged`, which during a live game is every 10 s. `PathFor` has to be synchronous so the row records stay plain data; the fetch has to happen off to the side. Without in-flight de-duplication, a rebuild with four games on screen would re-issue the same eight requests every 10 s for as long as any logo is missing. The `Changed` event is what lets the flyout re-render once a logo lands.

WPF's `Image.Source` accepts a file path string through its built-in type converter, so the row view-model can carry a path and the template needs no converter of its own.

- [ ] **Step 1: Add a stub handler to the App test project**

`OnDeck.Core.Tests` has one, but the two test projects don't reference each other. Create `tests/OnDeck.App.Tests/StubHttpMessageHandler.cs`:

```csharp
using System.Net;
using System.Net.Http;

namespace OnDeck.App.Tests;

/// <summary>Replays queued responses and records the requests it saw.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<Uri> Requests { get; } = [];

    public HttpClient CreateClient() => new(this);

    public void EnqueueBytes(byte[] body) =>
        _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        });

    public void EnqueueStatus(HttpStatusCode status) =>
        _responses.Enqueue(new HttpResponseMessage(status));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);

        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        return Task.FromResult(response);
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/OnDeck.App.Tests/TeamLogoStoreTests.cs`:

```csharp
using OnDeck.App.Views;
using OnDeck.Core.Utilities;

namespace OnDeck.App.Tests;

public class TeamLogoStoreTests : IDisposable
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ondeck-logo-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private TeamLogoStore Store(StubHttpMessageHandler handler) =>
        new(new TeamLogoCache(handler.CreateClient(), _directory), size: 32);

    [Fact]
    public void PathFor_IsNullBeforeAnythingIsFetched()
    {
        Assert.Null(Store(new StubHttpMessageHandler()).PathFor(119));
    }

    [Fact]
    public async Task PathFor_ReturnsTheFileOnceFetched()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var store = Store(handler);

        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Equal(Path.Combine(_directory, "119_32.png"), store.PathFor(119));
    }

    [Fact]
    public async Task Prefetch_RaisesChangedWhenALogoLands()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var store = Store(handler);
        var changes = 0;
        store.Changed += () => changes++;

        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Prefetch_DoesNotRefetchACachedLogo()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var store = Store(handler);

        store.Prefetch([119]);
        await store.DrainAsync();
        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Prefetch_CollapsesRepeatRequestsForTheSameTeam()
    {
        // A rebuild every 10s with the logo still missing must not queue a request per rebuild.
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var store = Store(handler);

        store.Prefetch([119, 119]);
        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Prefetch_StaysQuietWhenTheFetchFails()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueStatus(System.Net.HttpStatusCode.NotFound);
        var store = Store(handler);
        var changes = 0;
        store.Changed += () => changes++;

        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Equal(0, changes);
        Assert.Null(store.PathFor(119));
    }
}
```

- [ ] **Step 3: Run and confirm failure**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~TeamLogoStoreTests
```
Expected: build failure — `TeamLogoStore` does not exist.

- [ ] **Step 4: Implement**

Create `src/OnDeck.App/Views/TeamLogoStore.cs`:

```csharp
using OnDeck.Core.Utilities;

namespace OnDeck.App.Views;

/// <summary>
/// The shell's view of <see cref="TeamLogoCache"/>: a synchronous path lookup for row building,
/// plus a background fetch for anything missing.
/// <para>
/// Rows are rebuilt wholesale on every <c>StateChanged</c> — every 10 s during a live game — so
/// the lookup has to be synchronous and the fetch has to de-duplicate. Without the in-flight set,
/// a missing logo would be re-requested on every rebuild for as long as it stayed missing.
/// </para>
/// </summary>
public sealed class TeamLogoStore(TeamLogoCache cache, int size = 32)
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _inFlight = [];
    private readonly List<Task> _pending = [];

    /// <summary>Raised once a new logo is on disk, on whichever thread the fetch completed on.</summary>
    public event Action? Changed;

    /// <summary>The cached file for a team, or null if it isn't there yet.</summary>
    public string? PathFor(int teamId) => cache.FilePath(teamId, size);

    /// <summary>Starts fetching any of these logos that aren't cached or already being fetched.</summary>
    public void Prefetch(IEnumerable<int> teamIds)
    {
        foreach (var teamId in teamIds)
        {
            if (teamId <= 0) continue;
            if (PathFor(teamId) is not null) continue;

            // In the app every call is on the Dispatcher, but the fetch continuations are not
            // guaranteed to be, so the two collections are guarded rather than assumed serial.
            lock (_gate)
            {
                if (!_inFlight.Add(teamId)) continue;
                _pending.Add(FetchAsync(teamId));
            }
        }
    }

    /// <summary>Awaits the in-flight fetches. Test seam — the app never needs to wait.</summary>
    internal async Task DrainAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = [.. _pending];
                _pending.Clear();
            }

            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    private async Task FetchAsync(int teamId)
    {
        var path = await cache.GetAsync(teamId, size);

        lock (_gate)
        {
            _inFlight.Remove(teamId);
        }

        // A missing logo is a blank square, not something to redraw for.
        if (path is not null) Changed?.Invoke();
    }
}
```

Add to `src/OnDeck.App/OnDeck.App.csproj` so the test project can reach `DrainAsync`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="OnDeck.App.Tests" />
  </ItemGroup>
```

- [ ] **Step 5: Run and confirm green**

```bash
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~TeamLogoStoreTests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add windows/src/OnDeck.App windows/tests/OnDeck.App.Tests
git commit -m "phase 7b: team logo store"
```

---

## Task 6: The section list control

**Files:**
- Create: `src/OnDeck.App/Views/FlyoutContent.xaml`
- Create: `src/OnDeck.App/Views/FlyoutContent.xaml.cs`
- Create: `src/OnDeck.App/Platform/ExternalLink.cs`

**Spec:** `MenuBarView.swift:102-113`, `:130-328` (sections, header, divider), `:332-518` (live row), `:539-662` (upcoming and done rows), `:664-761` (score block, bases, outs), `:690-701` (`MenuRowButtonStyle`).

**Interfaces:**
- Consumes: `FlyoutSections`, `LiveRowViewModel`, `UpcomingRowViewModel`, `DoneRowViewModel`, `RefreshButtonModel`, `ThemePalette` keys.
- Produces: `FlyoutContent : UserControl` with `bool IsFloating { get; set; }`, `Func<Task<bool>>? Resync { get; set; }`, `void Render(FlyoutSections sections)`, `event Action<Uri>? RowActivated`, `event Action? CloseRequested`.
- Produces: `static class ExternalLink` with `void Open(Uri url)`.

The header's refresh button is `FloatingRefreshButton` (`MenuBarView.swift:954-1000`) — the *same* four-state machine as the footer's, drawn smaller. It therefore uses `RefreshButtonModel` too, which is why `FlyoutContent` takes a `Resync` delegate rather than raising an event and losing the state feedback.

**Layout numbers** are Swift's, in DIPs (SwiftUI points and WPF DIPs are both 1/96 in at 100%): row padding 12/4, section header padding 12 horizontal, 10 top, 4 bottom; divider 1px with 4 vertical margin; dot 6×6; bases block 35×24; outs dots 7×7 spaced 4; score text 16pt semibold; stat line 12pt; name at the default size.

- [ ] **Step 1: Write the link opener**

Create `src/OnDeck.App/Platform/ExternalLink.cs`:

```csharp
using System.Diagnostics;

namespace OnDeck.App.Platform;

/// <summary>Hands a URL to the default browser. Never throws — a dead link must not kill the app.</summary>
public static class ExternalLink
{
    public static void Open(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShellLog.Append($"[Link] failed to open {url}: {exception.Message}");
        }
    }
}
```

- [ ] **Step 2: Write the XAML**

Create `src/OnDeck.App/Views/FlyoutContent.xaml`:

```xml
<UserControl x:Class="OnDeck.App.Views.FlyoutContent"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:views="clr-namespace:OnDeck.App.Views"
             TextElement.Foreground="{DynamicResource OnDeck.Text.Primary}">
    <UserControl.Resources>

        <!-- Segoe Fluent Icons is Win11's; MDL2 is the Win10 fallback and carries the same
             codepoints for the glyphs used here. -->
        <FontFamily x:Key="IconFont">Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>

        <!-- MenuRowButtonStyle: chrome-free until hovered. -->
        <Style x:Key="RowButton" TargetType="Button">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderThickness" Value="0" />
            <Setter Property="Padding" Value="0" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Chrome"
                                Background="{TemplateBinding Background}"
                                CornerRadius="4"
                                Margin="8,0"
                                Padding="4,4">
                            <ContentPresenter />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Chrome" Property="Background"
                                        Value="{DynamicResource OnDeck.Row.Hover}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- One base of the diamond. Empty is a hollow outline, occupied is filled, and the
             row's own player turns it green. -->
        <Style x:Key="BaseDiamond" TargetType="Polygon">
            <Setter Property="Points" Value="5,0 10,5 5,10 0,5" />
            <Setter Property="Fill" Value="Transparent" />
            <Setter Property="Stroke" Value="{DynamicResource OnDeck.Base.Empty}" />
            <Setter Property="StrokeThickness" Value="1" />
        </Style>

        <DataTemplate x:Key="LiveRowTemplate" DataType="{x:Type views:LiveRowViewModel}">
            <Button Style="{StaticResource RowButton}" Click="OnRowClick">
                <Grid>
                    <!-- No feed yet: name on the left, a green "In Game" on the right. -->
                    <Grid x:Name="Placeholder" Visibility="Collapsed">
                        <TextBlock Text="{Binding Name}" FontWeight="Medium"
                                   HorizontalAlignment="Left" VerticalAlignment="Center" />
                        <TextBlock Text="In Game" FontSize="11"
                                   Foreground="{DynamicResource OnDeck.Accent.Green}"
                                   HorizontalAlignment="Right" VerticalAlignment="Center" />
                    </Grid>

                    <Grid x:Name="Live">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>

                        <StackPanel Grid.Column="0" VerticalAlignment="Center" Margin="0,0,8,0">
                            <StackPanel Orientation="Horizontal">
                                <Grid Width="10" VerticalAlignment="Center">
                                    <Ellipse x:Name="Dot" Width="6" Height="6"
                                             HorizontalAlignment="Left" Visibility="Collapsed" />
                                </Grid>
                                <TextBlock x:Name="Name" Text="{Binding Name}"
                                           FontWeight="Medium" TextTrimming="CharacterEllipsis" />
                            </StackPanel>
                            <StackPanel x:Name="StatRow" Orientation="Horizontal" Margin="0,1,0,0">
                                <TextBlock Text="{Binding DelayGlyph}"
                                           FontFamily="{StaticResource IconFont}"
                                           FontSize="11" Margin="0,0,4,0"
                                           VerticalAlignment="Center"
                                           Foreground="{DynamicResource OnDeck.Accent.Blue}" />
                                <TextBlock Text="{Binding StatLine}" FontSize="12"
                                           TextTrimming="CharacterEllipsis"
                                           Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                            </StackPanel>
                        </StackPanel>

                        <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">

                            <!-- Score block: away over home, logo then runs. -->
                            <StackPanel Margin="0,0,16,0">
                                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                    <Border Width="16" Height="16" ClipToBounds="True" Margin="0,0,6,0">
                                        <Image Source="{Binding AwayLogoPath}" Stretch="UniformToFill"
                                               RenderTransformOrigin="0.5,0.5">
                                            <Image.RenderTransform>
                                                <ScaleTransform ScaleX="1.7" ScaleY="1.7" />
                                            </Image.RenderTransform>
                                        </Image>
                                    </Border>
                                    <TextBlock Text="{Binding AwayScore}" FontSize="16"
                                               FontWeight="SemiBold"
                                               Typography.NumeralAlignment="Tabular" />
                                </StackPanel>
                                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right"
                                            Margin="0,2,0,0">
                                    <Border Width="16" Height="16" ClipToBounds="True" Margin="0,0,6,0">
                                        <Image Source="{Binding HomeLogoPath}" Stretch="UniformToFill"
                                               RenderTransformOrigin="0.5,0.5">
                                            <Image.RenderTransform>
                                                <ScaleTransform ScaleX="1.7" ScaleY="1.7" />
                                            </Image.RenderTransform>
                                        </Image>
                                    </Border>
                                    <TextBlock Text="{Binding HomeScore}" FontSize="16"
                                               FontWeight="SemiBold"
                                               Typography.NumeralAlignment="Tabular" />
                                </StackPanel>
                            </StackPanel>

                            <!-- Bases over the inning. -->
                            <StackPanel Margin="0,0,16,0">
                                <Grid Width="35" Height="24">
                                    <Polygon x:Name="SecondBase" Style="{StaticResource BaseDiamond}"
                                             HorizontalAlignment="Center" VerticalAlignment="Center"
                                             Margin="0,-7,0,0" />
                                    <Polygon x:Name="ThirdBase" Style="{StaticResource BaseDiamond}"
                                             HorizontalAlignment="Center" VerticalAlignment="Center"
                                             Margin="-21,3.5,0,0" />
                                    <Polygon x:Name="FirstBase" Style="{StaticResource BaseDiamond}"
                                             HorizontalAlignment="Center" VerticalAlignment="Center"
                                             Margin="21,3.5,0,0" />
                                </Grid>
                                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center"
                                            Margin="0,-3,0,0">
                                    <Path x:Name="InningArrow" Margin="0,0,1,0"
                                          VerticalAlignment="Center"
                                          Data="M 0,0 L 7,0 L 3.5,5 Z"
                                          Fill="{DynamicResource OnDeck.Text.Secondary}" />
                                    <TextBlock Text="{Binding InningText}" FontSize="13"
                                               FontWeight="SemiBold"
                                               Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                                </StackPanel>
                            </StackPanel>

                            <!-- Count over outs. Tabular figures keep 2-1 and 3-2 the same
                                 width so the block doesn't jitter pitch to pitch. -->
                            <StackPanel>
                                <TextBlock Text="{Binding CountText}" FontSize="12"
                                           FontWeight="SemiBold" HorizontalAlignment="Center"
                                           Typography.NumeralAlignment="Tabular" />
                                <StackPanel Orientation="Horizontal" Margin="0,4,0,0"
                                            HorizontalAlignment="Center">
                                    <Ellipse x:Name="Out1" Width="7" Height="7" Margin="0,0,4,0"
                                             Fill="{DynamicResource OnDeck.Base.Empty}" />
                                    <Ellipse x:Name="Out2" Width="7" Height="7" Margin="0,0,4,0"
                                             Fill="{DynamicResource OnDeck.Base.Empty}" />
                                    <Ellipse x:Name="Out3" Width="7" Height="7"
                                             Fill="{DynamicResource OnDeck.Base.Empty}" />
                                </StackPanel>
                            </StackPanel>
                        </StackPanel>
                    </Grid>
                </Grid>

                <DataTemplate.Triggers>
                    <DataTrigger Binding="{Binding HasFeed}" Value="False">
                        <Setter TargetName="Live" Property="Visibility" Value="Collapsed" />
                        <Setter TargetName="Placeholder" Property="Visibility" Value="Visible" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding IsActive}" Value="True">
                        <Setter TargetName="Name" Property="FontWeight" Value="SemiBold" />
                        <Setter TargetName="StatRow" Property="Margin" Value="10,1,0,0" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding StatLine}" Value="{x:Null}">
                        <Setter TargetName="StatRow" Property="Visibility" Value="Collapsed" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Dot}" Value="Filled">
                        <Setter TargetName="Dot" Property="Visibility" Value="Visible" />
                        <Setter TargetName="Dot" Property="Fill"
                                Value="{DynamicResource OnDeck.Accent.Green}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Dot}" Value="Outlined">
                        <Setter TargetName="Dot" Property="Visibility" Value="Visible" />
                        <Setter TargetName="Dot" Property="Stroke"
                                Value="{DynamicResource OnDeck.Accent.Green}" />
                        <Setter TargetName="Dot" Property="StrokeThickness" Value="1.5" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Dot}" Value="Warning">
                        <Setter TargetName="Dot" Property="Visibility" Value="Visible" />
                        <Setter TargetName="Dot" Property="Stroke"
                                Value="{DynamicResource OnDeck.Accent.Orange}" />
                        <Setter TargetName="Dot" Property="StrokeThickness" Value="1.5" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding IsTopHalf}" Value="True">
                        <Setter TargetName="InningArrow" Property="Data" Value="M 0,5 L 7,5 L 3.5,0 Z" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding First}" Value="Occupied">
                        <Setter TargetName="FirstBase" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                        <Setter TargetName="FirstBase" Property="Stroke"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding First}" Value="Highlighted">
                        <Setter TargetName="FirstBase" Property="Fill"
                                Value="{DynamicResource OnDeck.Accent.Green}" />
                        <Setter TargetName="FirstBase" Property="Stroke"
                                Value="{DynamicResource OnDeck.Accent.Green}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Second}" Value="Occupied">
                        <Setter TargetName="SecondBase" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                        <Setter TargetName="SecondBase" Property="Stroke"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Second}" Value="Highlighted">
                        <Setter TargetName="SecondBase" Property="Fill"
                                Value="{DynamicResource OnDeck.Accent.Green}" />
                        <Setter TargetName="SecondBase" Property="Stroke"
                                Value="{DynamicResource OnDeck.Accent.Green}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Third}" Value="Occupied">
                        <Setter TargetName="ThirdBase" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                        <Setter TargetName="ThirdBase" Property="Stroke"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Third}" Value="Highlighted">
                        <Setter TargetName="ThirdBase" Property="Fill"
                                Value="{DynamicResource OnDeck.Accent.Green}" />
                        <Setter TargetName="ThirdBase" Property="Stroke"
                                Value="{DynamicResource OnDeck.Accent.Green}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Outs}" Value="1">
                        <Setter TargetName="Out1" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Outs}" Value="2">
                        <Setter TargetName="Out1" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                        <Setter TargetName="Out2" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding Outs}" Value="3">
                        <Setter TargetName="Out1" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                        <Setter TargetName="Out2" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                        <Setter TargetName="Out3" Property="Fill"
                                Value="{DynamicResource OnDeck.Base.Occupied}" />
                    </DataTrigger>
                </DataTemplate.Triggers>
            </Button>
        </DataTemplate>

        <DataTemplate x:Key="UpcomingRowTemplate" DataType="{x:Type views:UpcomingRowViewModel}">
            <Grid Margin="12,3">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="14" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>

                <Grid Grid.Column="0">
                    <Ellipse x:Name="Missing" Width="6" Height="6" Visibility="Collapsed"
                             HorizontalAlignment="Center" VerticalAlignment="Center"
                             Fill="{DynamicResource OnDeck.Accent.Red}" />
                    <TextBlock x:Name="Present" Text="&#xE73E;" Visibility="Collapsed"
                               FontFamily="{StaticResource IconFont}" FontSize="9"
                               HorizontalAlignment="Center" VerticalAlignment="Center"
                               Foreground="{DynamicResource OnDeck.Accent.Green}" />
                    <TextBlock x:Name="Order" Text="{Binding BadgeText}" Visibility="Collapsed"
                               FontSize="10" FontWeight="SemiBold"
                               HorizontalAlignment="Center" VerticalAlignment="Center"
                               Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                </Grid>

                <TextBlock Grid.Column="1" Text="{Binding Name}" Margin="4,0,0,0"
                           TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />

                <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                    <TextBlock Text="{Binding DelayGlyph}" FontFamily="{StaticResource IconFont}"
                               FontSize="11" Margin="0,0,4,0"
                               Foreground="{DynamicResource OnDeck.Accent.Blue}" />
                    <TextBlock Text="{Binding TrailingText}" FontSize="11"
                               Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                </StackPanel>
            </Grid>
            <DataTemplate.Triggers>
                <DataTrigger Binding="{Binding Badge}" Value="Missing">
                    <Setter TargetName="Missing" Property="Visibility" Value="Visible" />
                </DataTrigger>
                <DataTrigger Binding="{Binding Badge}" Value="Present">
                    <Setter TargetName="Present" Property="Visibility" Value="Visible" />
                </DataTrigger>
                <DataTrigger Binding="{Binding Badge}" Value="Order">
                    <Setter TargetName="Order" Property="Visibility" Value="Visible" />
                </DataTrigger>
            </DataTemplate.Triggers>
        </DataTemplate>

        <DataTemplate x:Key="DoneRowTemplate" DataType="{x:Type views:DoneRowViewModel}">
            <Grid Margin="12,3">
                <TextBlock Text="{Binding Name}" HorizontalAlignment="Left"
                           Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                <TextBlock Text="{Binding StatLine}" FontSize="11" HorizontalAlignment="Right"
                           Foreground="{DynamicResource OnDeck.Text.Secondary}" />
            </Grid>
        </DataTemplate>

    </UserControl.Resources>

    <StackPanel>

        <!-- ACTIVE NOW -->
        <StackPanel x:Name="ActiveSection">
            <Grid Margin="12,10,12,4">
                <TextBlock Text="ACTIVE NOW" FontSize="11" FontWeight="SemiBold"
                           Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                <ContentPresenter x:Name="ActiveHeaderControls" HorizontalAlignment="Right" />
            </Grid>
            <ItemsControl x:Name="ActiveRows" ItemTemplate="{StaticResource LiveRowTemplate}" />
            <Rectangle x:Name="ActiveDivider" Height="1" Margin="0,4"
                       Fill="{DynamicResource OnDeck.Divider}" />
        </StackPanel>

        <!-- IN GAME -->
        <StackPanel x:Name="InGameSection">
            <Grid Margin="12,10,12,4">
                <TextBlock Text="IN GAME" FontSize="11" FontWeight="SemiBold"
                           Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                <ContentPresenter x:Name="InGameHeaderControls" HorizontalAlignment="Right" />
            </Grid>
            <ItemsControl x:Name="InGameRows" ItemTemplate="{StaticResource LiveRowTemplate}" />
            <Rectangle x:Name="InGameDivider" Height="1" Margin="0,4"
                       Fill="{DynamicResource OnDeck.Divider}" />
        </StackPanel>

        <!-- UPCOMING -->
        <StackPanel x:Name="UpcomingSection">
            <Grid Margin="12,10,12,4">
                <TextBlock Text="UPCOMING" FontSize="11" FontWeight="SemiBold"
                           Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                <ContentPresenter x:Name="UpcomingHeaderControls" HorizontalAlignment="Right" />
            </Grid>
            <ItemsControl x:Name="UpcomingRows" ItemTemplate="{StaticResource UpcomingRowTemplate}" />
            <Rectangle x:Name="UpcomingDivider" Height="1" Margin="0,4"
                       Fill="{DynamicResource OnDeck.Divider}" />
        </StackPanel>

        <!-- DONE -->
        <StackPanel x:Name="DoneSection">
            <Grid Margin="12,10,12,4">
                <TextBlock Text="DONE" FontSize="11" FontWeight="SemiBold"
                           Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                <ContentPresenter x:Name="DoneHeaderControls" HorizontalAlignment="Right" />
            </Grid>
            <ItemsControl x:Name="DoneRows" ItemTemplate="{StaticResource DoneRowTemplate}" />
            <Rectangle x:Name="DoneDivider" Height="1" Margin="0,4"
                       Fill="{DynamicResource OnDeck.Divider}" />
        </StackPanel>

        <!-- Empty state -->
        <StackPanel x:Name="EmptySection">
            <Grid Margin="12,8">
                <TextBlock x:Name="EmptyText" TextWrapping="Wrap"
                           Foreground="{DynamicResource OnDeck.Text.Secondary}" />
                <ContentPresenter x:Name="EmptyHeaderControls" HorizontalAlignment="Right"
                                  VerticalAlignment="Top" />
            </Grid>
            <Rectangle x:Name="EmptyDivider" Height="1" Margin="0,4"
                       Fill="{DynamicResource OnDeck.Divider}" />
        </StackPanel>

        <!-- Error -->
        <StackPanel x:Name="ErrorSection">
            <StackPanel Orientation="Horizontal" Margin="12,6">
                <TextBlock Text="&#xE7BA;" FontFamily="{StaticResource IconFont}" FontSize="11"
                           Margin="0,0,4,0" VerticalAlignment="Center"
                           Foreground="{DynamicResource OnDeck.Accent.Red}" />
                <TextBlock x:Name="ErrorText" FontSize="11" TextWrapping="Wrap"
                           Foreground="{DynamicResource OnDeck.Accent.Red}" />
            </StackPanel>
            <Rectangle x:Name="ErrorDivider" Height="1" Margin="0,4"
                       Fill="{DynamicResource OnDeck.Divider}" />
        </StackPanel>

    </StackPanel>
</UserControl>
```

- [ ] **Step 3: Write the code-behind**

Create `src/OnDeck.App/Views/FlyoutContent.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using OnDeck.App.Platform;

namespace OnDeck.App.Views;

/// <summary>
/// The section list from <c>Views/MenuBarView.swift</c>, shared verbatim by the flyout and the
/// floating panel. It renders a <see cref="FlyoutSections"/> and keeps no state beyond the
/// floating header's refresh button, which is what lets both windows point at the same control
/// with different <see cref="IsFloating"/>.
/// </summary>
public partial class FlyoutContent : UserControl
{
    // Segoe Fluent Icons: Refresh, CheckMark, and Cancel — which serves as both the failed-sync
    // mark and the close button, as it does on the Mac.
    private const string RefreshGlyphText = "";
    private const string DoneGlyphText = "";
    private const string FailedGlyphText = "";
    private const string CloseGlyphText = "";

    private readonly RefreshButtonModel _refresh = new();
    private readonly StackPanel _headerControls;
    private readonly TextBlock _refreshGlyph;

    public FlyoutContent()
    {
        InitializeComponent();

        // Built once and re-parented on each render: the button owns refresh state, so
        // rebuilding it every 10 s would reset a spinner mid-sync.
        (_headerControls, _refreshGlyph) = BuildHeaderControls();
        _refresh.Changed += ShowRefreshState;

        Render(FlyoutSections.Build(new FlyoutInput(), isFloating: false, _ => null));
    }

    /// <summary>Floating mode drops the trailing divider and shows the header controls.</summary>
    public bool IsFloating { get; set; }

    /// <summary>What the header's refresh button runs. Only the floating panel sets it.</summary>
    public Func<Task<bool>>? Resync { get; set; }

    /// <summary>A live row was clicked; the argument is the stream URL to open.</summary>
    public event Action<Uri>? RowActivated;

    /// <summary>The floating panel's close button.</summary>
    public event Action? CloseRequested;

    public void Render(FlyoutSections sections)
    {
        ActiveRows.ItemsSource = sections.Active;
        InGameRows.ItemsSource = sections.InGame;
        UpcomingRows.ItemsSource = sections.Upcoming;
        DoneRows.ItemsSource = sections.Done;

        EmptyText.Text = sections.EmptyText ?? "";
        ErrorText.Text = sections.ErrorText ?? "";

        Show(ActiveSection, sections.ShowsActive);
        Show(InGameSection, sections.ShowsInGame);
        Show(UpcomingSection, sections.ShowsUpcoming);
        Show(DoneSection, sections.ShowsDone);
        Show(EmptySection, sections.ShowsEmpty);
        Show(ErrorSection, sections.ShowsError);

        Show(ActiveDivider, sections.ActiveDivider);
        Show(InGameDivider, sections.InGameDivider);
        Show(UpcomingDivider, sections.UpcomingDivider);
        Show(DoneDivider, sections.DoneDivider);
        Show(EmptyDivider, sections.EmptyDivider);
        Show(ErrorDivider, sections.ErrorDivider);

        PlaceHeaderControls(sections.HeaderControlsSection);
    }

    /// <summary>
    /// Moves the refresh + close pair into whichever header is first on screen — Swift's
    /// <c>showClose</c>. Only one instance exists, so every host is cleared before re-parenting —
    /// WPF throws if an element ends up with two logical parents.
    /// </summary>
    private void PlaceHeaderControls(FlyoutSectionKind section)
    {
        ActiveHeaderControls.Content = null;
        InGameHeaderControls.Content = null;
        UpcomingHeaderControls.Content = null;
        DoneHeaderControls.Content = null;
        EmptyHeaderControls.Content = null;

        if (!IsFloating) return;

        var host = section switch
        {
            FlyoutSectionKind.Active => ActiveHeaderControls,
            FlyoutSectionKind.InGame => InGameHeaderControls,
            FlyoutSectionKind.Upcoming => UpcomingHeaderControls,
            FlyoutSectionKind.Done => DoneHeaderControls,
            _ => EmptyHeaderControls,
        };

        host.Content = _headerControls;
    }

    /// <summary>Port of <c>FloatingRefreshButton</c> plus the close button beside it.</summary>
    private (StackPanel Panel, TextBlock RefreshGlyph) BuildHeaderControls()
    {
        var icons = (System.Windows.Media.FontFamily)Resources["IconFont"];

        var refreshGlyph = NewGlyph(RefreshGlyphText, icons);
        var refresh = new Button
        {
            Content = refreshGlyph,
            Padding = new Thickness(4, 0, 4, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Refresh",
        };
        refresh.Click += (_, _) =>
        {
            if (Resync is { } resync) _ = _refresh.ClickAsync(resync);
        };

        var close = new Button
        {
            Content = NewGlyph(CloseGlyphText, icons),
            Padding = new Thickness(4, 0, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Close",
        };
        close.Click += (_, _) => CloseRequested?.Invoke();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(refresh);
        panel.Children.Add(close);
        return (panel, refreshGlyph);
    }

    /// <summary>A glyph run in the icon font, tinted secondary like every other chrome label.</summary>
    private static TextBlock NewGlyph(string text, System.Windows.Media.FontFamily icons)
    {
        var glyph = new TextBlock { Text = text, FontFamily = icons, FontSize = 12 };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, ThemePalette.TextSecondary);
        return glyph;
    }

    /// <summary>
    /// The click handler runs on the Dispatcher, so <c>ClickAsync</c>'s continuations return to
    /// it and this can touch the glyph directly.
    /// </summary>
    private void ShowRefreshState() => _refreshGlyph.Text = _refresh.State switch
    {
        RefreshButtonState.Done => DoneGlyphText,
        RefreshButtonState.Failed => FailedGlyphText,
        _ => RefreshGlyphText,
    };

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LiveRowViewModel row) return;
        if (row.StreamUrl is not { } url) return;

        RowActivated?.Invoke(url);
    }

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
```

A null `DelayGlyph` needs no trigger — `TextBlock` with null `Text` renders nothing and takes zero
width, which is exactly what Swift's `if let icon` produces.

- [ ] **Step 4: Build**

```bash
dotnet build windows/OnDeck.slnx
```
Expected: no errors. XAML errors surface here, not at runtime.

Two failure modes to expect and fix rather than work around:
- *"The name X does not exist in the namespace"* — the `views:` xmlns must be `clr-namespace:OnDeck.App.Views` with no assembly suffix (same assembly).
- *`DataTrigger` on an enum `Value="Filled"`* — WPF parses the string against the bound property's type; this works because `Dot` is typed `ProximityDot`. If it silently never fires, the binding path is wrong, not the trigger.

- [ ] **Step 5: Run the full suite and commit**

```bash
dotnet test windows/OnDeck.slnx
git add windows/src/OnDeck.App
git commit -m "phase 7b: player rows and section list"
```
Expected: `Failed: 0`.

---

## Task 7: Footer and flyout wiring

**Files:**
- Create: `src/OnDeck.App/Views/FooterBar.xaml`
- Create: `src/OnDeck.App/Views/FooterBar.xaml.cs`
- Modify: `src/OnDeck.App/Windows/FlyoutWindow.xaml`
- Modify: `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs`
- Modify: `src/OnDeck.App/Tray/TrayIconService.cs`
- Modify: `src/OnDeck.App/App.xaml.cs`

**Spec:** `MenuBarView.swift:835-950` (`FooterButtons`).

**Interfaces:**
- Consumes: `RefreshButtonModel`, `FlyoutContent`, `ExternalLink`, `AppOrchestrator.ParsedLeagueId/ResyncRosterAsync`.
- Produces: `FooterBar : UserControl` with `bool ShowsFantrax { get; set; }`, `event Action? FantraxRequested`, `event Action? FloatRequested`, `event Action? QuitRequested`, `Func<Task<bool>>? Resync { get; set; }`, `void SetFloating(bool isPanelOpen)`.
- Produces: `TrayIconService.FloatRequested` event and a **Float** menu item.

**Scope note.** The Settings button (`MenuBarView.swift:846`) is deliberately **not** built here — see the plan's Scope section. `FooterBar` has room for it and Phase 8 adds it with the window it opens.

- [ ] **Step 1: Write the footer XAML**

Create `src/OnDeck.App/Views/FooterBar.xaml`:

```xml
<UserControl x:Class="OnDeck.App.Views.FooterBar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <FontFamily x:Key="FooterIconFont">Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>

        <!-- FooterButtonStyle: 52x42 with a 6pt hover chrome. -->
        <Style x:Key="FooterButton" TargetType="Button">
            <Setter Property="Width" Value="52" />
            <Setter Property="Height" Value="42" />
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderThickness" Value="0" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Foreground" Value="{DynamicResource OnDeck.Text.Secondary}" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Chrome" CornerRadius="6"
                                Background="{TemplateBinding Background}">
                            <ContentPresenter HorizontalAlignment="Center"
                                              VerticalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Chrome" Property="Background"
                                        Value="{DynamicResource OnDeck.Row.Hover}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </UserControl.Resources>

    <Grid Margin="12,2,12,6">
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Left">

            <Button x:Name="FantraxButton" Style="{StaticResource FooterButton}" Click="OnFantrax">
                <StackPanel>
                    <TextBlock Text="&#xE774;" FontFamily="{StaticResource FooterIconFont}"
                               FontSize="16" Height="20" HorizontalAlignment="Center" />
                    <TextBlock Text="Fantrax" FontSize="10" Margin="0,3,0,0"
                               HorizontalAlignment="Center" />
                </StackPanel>
            </Button>

            <Button x:Name="RefreshButton" Style="{StaticResource FooterButton}" Click="OnRefresh">
                <StackPanel>
                    <TextBlock x:Name="RefreshGlyph" Text="&#xE72C;"
                               FontFamily="{StaticResource FooterIconFont}"
                               FontSize="16" Height="20" HorizontalAlignment="Center"
                               RenderTransformOrigin="0.5,0.5">
                        <TextBlock.RenderTransform>
                            <RotateTransform x:Name="RefreshSpin" />
                        </TextBlock.RenderTransform>
                    </TextBlock>
                    <TextBlock Text="Refresh" FontSize="10" Margin="0,3,0,0"
                               HorizontalAlignment="Center" />
                </StackPanel>
            </Button>

            <Button x:Name="FloatButton" Style="{StaticResource FooterButton}" Click="OnFloat">
                <StackPanel>
                    <TextBlock x:Name="FloatGlyph" Text="&#xE8A7;"
                               FontFamily="{StaticResource FooterIconFont}"
                               FontSize="16" Height="20" HorizontalAlignment="Center" />
                    <TextBlock Text="Float" FontSize="10" Margin="0,3,0,0"
                               HorizontalAlignment="Center" />
                </StackPanel>
            </Button>
        </StackPanel>

        <Button x:Name="QuitButton" Style="{StaticResource FooterButton}"
                HorizontalAlignment="Right" Click="OnQuit">
            <StackPanel>
                <TextBlock Text="&#xE7E8;" FontFamily="{StaticResource FooterIconFont}"
                           FontSize="16" Height="20" HorizontalAlignment="Center" />
                <TextBlock Text="Quit" FontSize="10" Margin="0,3,0,0"
                           HorizontalAlignment="Center" />
            </StackPanel>
        </Button>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Write the footer code-behind**

Create `src/OnDeck.App/Views/FooterBar.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace OnDeck.App.Views;

/// <summary>
/// Port of <c>FooterButtons</c> in <c>Views/MenuBarView.swift</c>. Settings is absent by design
/// until Phase 8 brings the window it would open.
/// </summary>
public partial class FooterBar : UserControl
{
    private readonly RefreshButtonModel _refresh = new();
    private readonly Storyboard _spinner;

    public FooterBar()
    {
        InitializeComponent();

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(spin, RefreshGlyph);
        Storyboard.SetTargetProperty(
            spin, new PropertyPath("RenderTransform.(RotateTransform.Angle)"));
        _spinner = new Storyboard();
        _spinner.Children.Add(spin);

        _refresh.Changed += ShowRefreshState;
    }

    /// <summary>Hidden when the roster URL has no parseable leagueID — Swift's `if let leagueID`.</summary>
    public bool ShowsFantrax
    {
        get => FantraxButton.Visibility == Visibility.Visible;
        set => FantraxButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What Refresh runs. Set by the window that owns this bar.</summary>
    public Func<Task<bool>>? Resync { get; set; }

    public event Action? FantraxRequested;

    public event Action? FloatRequested;

    public event Action? QuitRequested;

    /// <summary>Swaps the Float glyph between "open a panel" and "put it back".</summary>
    public void SetFloating(bool isPanelOpen) =>
        FloatGlyph.Text = isPanelOpen ? "\uE73F" : "\uE8A7";

    private void OnFantrax(object sender, RoutedEventArgs e) => FantraxRequested?.Invoke();

    private void OnFloat(object sender, RoutedEventArgs e) => FloatRequested?.Invoke();

    private void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (Resync is not { } resync) return;
        _ = _refresh.ClickAsync(resync);
    }

    private void ShowRefreshState()
    {
        switch (_refresh.State)
        {
            case RefreshButtonState.Spinning:
                RefreshGlyph.Text = "\uE72C";
                _spinner.Begin(this, isControllable: true);
                break;

            case RefreshButtonState.Done:
                _spinner.Stop(this);
                RefreshGlyph.Text = "\uE73E";
                break;

            case RefreshButtonState.Failed:
                _spinner.Stop(this);
                RefreshGlyph.Text = "\uE711";
                break;

            default:
                _spinner.Stop(this);
                RefreshGlyph.Text = "\uE72C";
                break;
        }
    }
}
```

The storyboard needs a name scope to target the glyph; `Storyboard.Begin(this, ...)` supplies it.

- [ ] **Step 3: Replace the flyout's placeholder**

Replace the whole of `src/OnDeck.App/Windows/FlyoutWindow.xaml` body (keep the `Window` attributes exactly as they are — `Background="Transparent"` and the rest are load-bearing for the backdrop path):

```xml
<Window x:Class="OnDeck.App.Windows.FlyoutWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:OnDeck.App.Views"
        Width="300"
        SizeToContent="Height"
        MaxHeight="640"
        WindowStyle="None"
        ResizeMode="NoResize"
        ShowInTaskbar="False"
        Topmost="True"
        Background="Transparent">
    <Border x:Name="Root" CornerRadius="8">
        <DockPanel LastChildFill="True">
            <views:FooterBar x:Name="Footer" DockPanel.Dock="Bottom" />
            <ScrollViewer VerticalScrollBarVisibility="Auto"
                          HorizontalScrollBarVisibility="Disabled">
                <views:FlyoutContent x:Name="Sections" />
            </ScrollViewer>
        </DockPanel>
    </Border>
</Window>
```

**The element is named `Sections`, not `Content`.** `x:Name="Content"` would generate a field that
collides with `Window`'s own inherited `Content` property — a compile error, and a confusing one.

Note the `Padding="12"` is gone: rows and headers carry their own 12pt horizontal padding (as they do in Swift), and the footer's own margin handles the bottom. Keeping both would double the inset.

- [ ] **Step 4: Wire the flyout**

In `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs`, replace `RenderSummary` and the constructor's subscription. Keep `OnSourceInitialized`, `ShowAt`, `WorkAreaFor` and `ToAnchorRect` **unchanged** — that is the backdrop and placement code from Phases 6/7a.

```csharp
    private readonly AppOrchestrator _orchestrator;
    private readonly TeamLogoStore _logos;

    public FlyoutWindow(AppOrchestrator orchestrator, TeamLogoStore logos)
    {
        _orchestrator = orchestrator;
        _logos = logos;
        InitializeComponent();

        Deactivated += (_, _) => Hide();        // light dismiss

        Sections.RowActivated += OpenStream;
        Footer.Resync = _orchestrator.ResyncRosterAsync;
        Footer.FantraxRequested += OpenFantrax;
        Footer.QuitRequested += () => Application.Current.Shutdown();
        Footer.FloatRequested += () => FloatRequested?.Invoke();

        _orchestrator.StateChanged += Render;
        _logos.Changed += Render;
        Closed += (_, _) =>
        {
            _orchestrator.StateChanged -= Render;
            _logos.Changed -= Render;
        };
    }

    /// <summary>The footer's Float button; the app owns the panel itself.</summary>
    public event Action? FloatRequested;

    /// <summary>Keeps the Float glyph in step with whether the panel is open.</summary>
    public void SetFloating(bool isPanelOpen) => Footer.SetFloating(isPanelOpen);

    private void Render()
    {
        var input = FlyoutInputFactory.From(_orchestrator);

        _logos.Prefetch(FlyoutInputFactory.TeamIds(input));
        Sections.Render(FlyoutSections.Build(input, isFloating: false, _logos.PathFor));

        Footer.ShowsFantrax = _orchestrator.ParsedLeagueId is not null;
    }

    private void OpenStream(Uri url)
    {
        Hide();     // Swift dismisses the menu bar window before opening the link
        ExternalLink.Open(url);
    }

    private void OpenFantrax()
    {
        if (_orchestrator.ParsedLeagueId is not { } leagueId) return;

        Hide();
        ExternalLink.Open(new Uri($"https://www.fantrax.com/fantasy/league/{leagueId}/home"));
    }
```

Change `ShowAt`'s first line from `RenderSummary();` to `Render();`, and add `using OnDeck.App.Views;` plus `using OnDeck.App.Platform;`.

- [ ] **Step 5: Add the shared input factory**

Both windows build the same `FlyoutInput`. Append to `src/OnDeck.App/Views/FlyoutSections.cs`:

```csharp
/// <summary>Reads a <see cref="FlyoutInput"/> off the orchestrator. The one place the shell
/// touches Core's published state, so the layout rules stay testable on plain values.</summary>
public static class FlyoutInputFactory
{
    public static FlyoutInput From(OnDeck.Core.AppOrchestrator orchestrator) => new()
    {
        Active = orchestrator.ActivePlayers,
        InGame = orchestrator.InGamePlayers,
        Upcoming = orchestrator.UpcomingPlayers,
        Done = orchestrator.DonePlayers,
        IsSyncing = orchestrator.IsSyncing,
        HasRosterUrl = orchestrator.ParsedLeagueId is not null,
        LoadedPlayerCount = orchestrator.LoadedPlayerCount,
        Error = orchestrator.SyncError,
    };

    /// <summary>The team ids whose logos are on screen right now.</summary>
    public static IEnumerable<int> TeamIds(FlyoutInput input) =>
        input.Active.Concat(input.InGame)
            .Select(display => display.Feed)
            .OfType<LiveFeedData>()
            .SelectMany(feed => new[] { feed.AwayTeamId, feed.HomeTeamId })
            .Distinct();
}
```

`HasRosterUrl` uses `ParsedLeagueId` rather than the raw string: an unparseable URL and no URL are the same thing to the empty-state copy, and `AppOrchestrator` doesn't publish the raw value.

- [ ] **Step 6: Add Float to the tray menu**

In `src/OnDeck.App/Tray/TrayIconService.cs`, add the event and menu item:

```csharp
    public event Action? FloatRequested;
```

and inside `BuildMenu`, between `open` and `refresh`:

```csharp
        var float_ = new MenuItem { Header = "Float" };
        float_.Click += (_, _) => FloatRequested?.Invoke();
```

```csharp
        menu.Items.Add(open);
        menu.Items.Add(float_);
        menu.Items.Add(refresh);
```

Update the class doc comment: `Float and Settings arrive with the windows they open, in Phases 7 and 8.` → `Settings arrives with its window in Phase 8.`

- [ ] **Step 7: Update the composition root**

In `src/OnDeck.App/App.xaml.cs`, after the orchestrator is built:

```csharp
        _logos = new TeamLogoStore(new TeamLogoCache(http, TeamLogoCache.DefaultCacheDirectory()));
```

and change the flyout construction:

```csharp
        _flyout = new FlyoutWindow(_orchestrator, _logos);
```

Add the field `private TeamLogoStore? _logos;`. Leave `_tray.FloatRequested` and `_flyout.FloatRequested` unwired for now — Task 9 attaches them to the panel.

- [ ] **Step 8: Build, test, commit**

```bash
dotnet build windows/OnDeck.slnx
dotnet test windows/OnDeck.slnx
git add windows/src/OnDeck.App
git commit -m "phase 7b: footer bar and flyout wiring"
```
Expected: `Failed: 0`.

---

## Task 8: Remembering where the panel was

**Files:**
- Create: `src/OnDeck.App/Windows/FloatingPanelPlacement.cs`
- Create: `tests/OnDeck.App.Tests/FloatingPanelPlacementTests.cs`
- Modify: `src/OnDeck.App/SettingsStore.cs`
- Modify: `tests/OnDeck.App.Tests/SettingsStoreTests.cs`

**Spec:** `MenuBarView.swift:1051-1054` — `setFrameAutosaveName` / `setFrameUsingName`, falling back to `center()`.

**Interfaces:**
- Produces: `static class FloatingPanelPlacement` with `Rect? Restore(Rect? saved, IReadOnlyList<Rect> workAreas)`.
- Produces: `SettingsStore.FloatingPanelFrame` (`Rect?`), **shell-only** — deliberately not on `ISettingsStore`.

**Why the on-screen check.** macOS's `setFrameUsingName` returns false when the saved frame no longer fits any screen; Windows has no equivalent, so a panel last positioned on a monitor that's since been unplugged would restore off-screen and be unreachable — no taskbar button, no Alt-Tab entry (the window is `ShowInTaskbar="False"`). A frame counts as restorable when it overlaps any work area by at least a title-bar's worth of area.

- [ ] **Step 1: Write the failing tests**

Create `tests/OnDeck.App.Tests/FloatingPanelPlacementTests.cs`:

```csharp
using System.Windows;
using OnDeck.App.Windows;

namespace OnDeck.App.Tests;

public class FloatingPanelPlacementTests
{
    private static readonly Rect Primary = new(0, 0, 1920, 1040);
    private static readonly Rect Secondary = new(1920, 0, 2560, 1400);

    [Fact]
    public void RestoresAFrameFullyOnAMonitor()
    {
        var saved = new Rect(400, 300, 300, 500);

        Assert.Equal(saved, FloatingPanelPlacement.Restore(saved, [Primary]));
    }

    [Fact]
    public void RestoresAFrameOnASecondMonitor()
    {
        var saved = new Rect(2200, 100, 300, 500);

        Assert.Equal(saved, FloatingPanelPlacement.Restore(saved, [Primary, Secondary]));
    }

    [Fact]
    public void RejectsAFrameOnAMonitorThatIsGone()
    {
        var saved = new Rect(2200, 100, 300, 500);

        Assert.Null(FloatingPanelPlacement.Restore(saved, [Primary]));
    }

    [Fact]
    public void RejectsAFrameOnlyBarelyOverlapping()
    {
        // Two pixels of the corner on screen is not a reachable window.
        var saved = new Rect(1918, 1038, 300, 500);

        Assert.Null(FloatingPanelPlacement.Restore(saved, [Primary]));
    }

    [Fact]
    public void RejectsNothingSaved()
    {
        Assert.Null(FloatingPanelPlacement.Restore(null, [Primary]));
    }

    [Fact]
    public void RejectsAnEmptyFrame()
    {
        Assert.Null(FloatingPanelPlacement.Restore(new Rect(0, 0, 0, 0), [Primary]));
    }
}
```

Add to `tests/OnDeck.App.Tests/SettingsStoreTests.cs` (keep the file's existing temp-directory fixture and idioms — read it first and match them):

```csharp
    [Fact]
    public void FloatingPanelFrame_RoundTripsThroughTheFile()
    {
        var frame = new System.Windows.Rect(120, 80, 300, 520);

        new SettingsStore(_directory).FloatingPanelFrame = frame;

        Assert.Equal(frame, new SettingsStore(_directory).FloatingPanelFrame);
    }

    [Fact]
    public void FloatingPanelFrame_IsNullUntilThePanelHasBeenPlaced()
    {
        Assert.Null(new SettingsStore(_directory).FloatingPanelFrame);
    }
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test windows/OnDeck.slnx --filter "FullyQualifiedName~FloatingPanelPlacementTests|FullyQualifiedName~SettingsStoreTests"
```
Expected: build failure — `FloatingPanelPlacement` and `FloatingPanelFrame` don't exist.

- [ ] **Step 3: Implement the placement check**

Create `src/OnDeck.App/Windows/FloatingPanelPlacement.cs`:

```csharp
using System.Windows;

namespace OnDeck.App.Windows;

/// <summary>
/// Decides whether a remembered panel frame can still be used. macOS gets this from
/// <c>setFrameUsingName</c> returning false; Windows has no equivalent, and a panel restored
/// onto a monitor that has since been unplugged is unreachable — it has no taskbar button and
/// no Alt-Tab entry.
/// </summary>
public static class FloatingPanelPlacement
{
    /// <summary>Enough of the window on screen to grab and drag.</summary>
    private const double MinimumVisibleArea = 300 * 32;

    public static Rect? Restore(Rect? saved, IReadOnlyList<Rect> workAreas)
    {
        if (saved is not { } frame) return null;
        if (frame.Width <= 0 || frame.Height <= 0) return null;

        foreach (var workArea in workAreas)
        {
            var overlap = Rect.Intersect(frame, workArea);
            if (overlap.IsEmpty) continue;
            if (overlap.Width * overlap.Height >= MinimumVisibleArea) return frame;
        }

        return null;
    }
}
```

- [ ] **Step 4: Add the shell-only frame to the settings store**

In `src/OnDeck.App/SettingsStore.cs`, add `using System.Windows;` and, after `RosterCacheJson`:

```csharp
    /// <summary>
    /// The floating panel's last frame. Shell-only and deliberately absent from
    /// <see cref="ISettingsStore"/> — Core has no business knowing a window exists.
    /// </summary>
    public Rect? FloatingPanelFrame
    {
        get => _values.PanelFrame?.ToRect();
        set => Update(values => values with { PanelFrame = StoredRect.From(value) });
    }
```

Add to the `Snapshot` record:

```csharp
        public StoredRect? PanelFrame { get; init; }
```

and beside it:

```csharp
    /// <summary><see cref="Rect"/> has no parameterless constructor, so it is persisted flat.</summary>
    private sealed record StoredRect(double X, double Y, double Width, double Height)
    {
        public Rect ToRect() => new(X, Y, Width, Height);

        public static StoredRect? From(Rect? rect) =>
            rect is { } value ? new StoredRect(value.X, value.Y, value.Width, value.Height) : null;
    }
```

Declare `StoredRect` as a sibling of `Snapshot` — both nested directly inside `SettingsStore`, not `StoredRect` inside `Snapshot`. `System.Text.Json` serialises private nested types through their public properties, which is already how `Snapshot` itself round-trips.

- [ ] **Step 5: Run and confirm green**

```bash
dotnet test windows/OnDeck.slnx --filter "FullyQualifiedName~FloatingPanelPlacementTests|FullyQualifiedName~SettingsStoreTests"
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add windows/src/OnDeck.App windows/tests/OnDeck.App.Tests
git commit -m "phase 7b: floating panel frame persistence"
```

---

## Task 9: FloatingPanelWindow

**Files:**
- Create: `src/OnDeck.App/Windows/FloatingPanelWindow.xaml`
- Create: `src/OnDeck.App/Windows/FloatingPanelWindow.xaml.cs`
- Modify: `src/OnDeck.App/Platform/MonitorWorkArea.cs`
- Modify: `src/OnDeck.App/App.xaml.cs`

**Spec:** `MenuBarView.swift:1004-1073` (`FloatingPanel`), `:99` + `:110-112` (floating hides the footer), `:303-313` (header close + refresh).

**Interfaces:**
- Consumes: `FlyoutContent`, `FlyoutSections`, `FlyoutInputFactory`, `TeamLogoStore`, `FloatingPanelPlacement`, `SettingsStore.FloatingPanelFrame`.
- Produces: `FloatingPanelWindow : Window` with `void Toggle()`, `bool IsOpen`, `event Action? OpenChanged`.
- Produces: `MonitorWorkArea.AllWorkAreas()` → `IReadOnlyList<Rect>` in DIPs.

**Behaviour being ported:** `.borderless` + `.nonactivatingPanel` → `WindowStyle="None"` + `ShowActivated="False"` + `WS_EX_NOACTIVATE`; `level = .floating` → `Topmost="True"`; `isMovableByWindowBackground` → `DragMove()` on a left-press anywhere that isn't a button; `setFrameAutosaveName` → save on move/resize/close.

- [ ] **Step 1: Add the all-monitors query**

Append to `src/OnDeck.App/Platform/MonitorWorkArea.cs`:

```csharp
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    /// <summary>Every connected monitor's work area, in device pixels.</summary>
    public static IReadOnlyList<Rect> AllWorkAreas()
    {
        var areas = new List<Rect>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeRect _, IntPtr _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.WorkArea;
                areas.Add(new Rect(
                    work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top));
            }

            return true;
        }, IntPtr.Zero);

        return areas;
    }
```

- [ ] **Step 2: Write the panel XAML**

Create `src/OnDeck.App/Windows/FloatingPanelWindow.xaml`:

```xml
<Window x:Class="OnDeck.App.Windows.FloatingPanelWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:OnDeck.App.Views"
        Width="300"
        SizeToContent="Height"
        MaxHeight="800"
        WindowStyle="None"
        ResizeMode="NoResize"
        ShowInTaskbar="False"
        ShowActivated="False"
        Topmost="True"
        Background="Transparent">
    <Border x:Name="Root" CornerRadius="12" MouseLeftButtonDown="OnDragBackground">
        <ScrollViewer VerticalScrollBarVisibility="Auto"
                      HorizontalScrollBarVisibility="Disabled">
            <views:FlyoutContent x:Name="Sections" IsFloating="True" />
        </ScrollViewer>
    </Border>
</Window>
```

- [ ] **Step 3: Write the panel code-behind**

Create `src/OnDeck.App/Windows/FloatingPanelWindow.xaml.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OnDeck.App.Platform;
using OnDeck.App.Views;
using OnDeck.Core;

namespace OnDeck.App.Windows;

/// <summary>
/// Port of <c>FloatingPanel</c> in <c>Views/MenuBarView.swift</c>: an always-on-top, borderless
/// panel showing the same sections as the flyout, draggable by its background, that remembers
/// where it was. It never takes focus — <c>WS_EX_NOACTIVATE</c> is the Windows analogue of
/// <c>.nonactivatingPanel</c>, so clicking it doesn't pull the user out of whatever they were in.
/// </summary>
public partial class FloatingPanelWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    private readonly AppOrchestrator _orchestrator;
    private readonly TeamLogoStore _logos;
    private readonly SettingsStore _settings;

    public FloatingPanelWindow(
        AppOrchestrator orchestrator, TeamLogoStore logos, SettingsStore settings)
    {
        _orchestrator = orchestrator;
        _logos = logos;
        _settings = settings;
        InitializeComponent();

        Sections.RowActivated += ExternalLink.Open;
        Sections.CloseRequested += Hide;
        Sections.Resync = _orchestrator.ResyncRosterAsync;

        _orchestrator.StateChanged += Render;
        _logos.Changed += Render;

        LocationChanged += (_, _) => SaveFrame();
        SizeChanged += (_, _) => SaveFrame();

        IsVisibleChanged += (_, _) => OpenChanged?.Invoke();
    }

    /// <summary>Fires whenever the panel opens or closes, so the Float glyph can follow.</summary>
    public event Action? OpenChanged;

    public bool IsOpen => IsVisible;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = (HwndSource)PresentationSource.FromVisual(this)!;

        // Same backdrop treatment as the flyout, including the same open acrylic issue -
        // see windows/ACRYLIC-OPEN-ISSUE.md before changing any of this.
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        DwmBackdrop.RoundCorners(source.Handle);
        if (DwmBackdrop.ApplyAcrylic(source.Handle) != 0)
        {
            Root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        }

        // Clicking the panel must not steal focus from whatever the user is actually doing.
        var style = GetWindowLong(source.Handle, GwlExStyle);
        SetWindowLong(source.Handle, GwlExStyle, style | WsExNoActivate);
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Render();
        RestoreFrame();
        Show();
    }

    private void Render()
    {
        var input = FlyoutInputFactory.From(_orchestrator);

        _logos.Prefetch(FlyoutInputFactory.TeamIds(input));
        Sections.Render(FlyoutSections.Build(input, isFloating: true, _logos.PathFor));
    }

    /// <summary>
    /// <c>isMovableByWindowBackground</c>. Buttons handle their own clicks first, so a press
    /// that reaches here is on the background.
    /// </summary>
    private void OnDragBackground(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
    }

    private void RestoreFrame()
    {
        var workAreas = MonitorWorkArea.AllWorkAreas()
            .Select(area => MonitorWorkArea.ToDips(area, this))
            .ToList();

        if (FloatingPanelPlacement.Restore(_settings.FloatingPanelFrame, workAreas) is { } frame)
        {
            Left = frame.X;
            Top = frame.Y;
            return;
        }

        // Swift's center() fallback.
        var primary = workAreas.Count > 0 ? workAreas[0] : SystemParameters.WorkArea;
        Left = primary.X + ((primary.Width - Width) / 2);
        Top = primary.Y + ((primary.Height - ActualHeight) / 2);
    }

    private void SaveFrame()
    {
        if (!IsVisible || ActualHeight <= 0) return;

        _settings.FloatingPanelFrame = new Rect(Left, Top, Width, ActualHeight);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
```

`MonitorWorkArea.ToDips` must be reachable before the window has a composition target — on the first `Toggle` it does, because `Show()` has not run yet. Call `RestoreFrame` **after** `Render()` (which sizes the content) and **before** `Show()`; `ToDips` falls back to device pixels when there is no source, which at 100% scaling is identical and at other scalings is corrected on the first `LocationChanged` save. Accept that; a first-open position that is a few pixels off on a scaled display self-corrects the moment the user drags it.

- [ ] **Step 4: Wire the panel into the app**

In `src/OnDeck.App/App.xaml.cs`:

Replace the `_flyout = new FlyoutWindow(...)` line from Task 7 with:

```csharp
        _flyout = new FlyoutWindow(_orchestrator, _logos);
        _flyout.FloatRequested += ToggleFloat;
        _tray.FloatRequested += ToggleFloat;

        _panel = new FloatingPanelWindow(_orchestrator, _logos, settings);
        _panel.OpenChanged += () => _flyout.SetFloating(_panel.IsOpen);

        if (settings.AlwaysOpenPopout) _panel.Toggle();
```

with the field and method:

```csharp
    private FloatingPanelWindow? _panel;

    private void ToggleFloat()
    {
        _flyout?.Hide();
        _panel?.Toggle();
    }
```

The flyout is constructed **first** so the `OpenChanged` handler closes over a non-null `_flyout` —
the panel's `IsVisibleChanged` can fire during `Toggle()` on the very next line.

Also make `OnExit` close the panel so its frame is saved:

```csharp
        _panel?.Close();
```

- [ ] **Step 5: Build, test, commit**

```bash
dotnet build windows/OnDeck.slnx
dotnet test windows/OnDeck.slnx
git add windows/src/OnDeck.App
git commit -m "phase 7b: floating panel window"
```
Expected: `Failed: 0`.

---

## Task 10: Verify and hand off

- [ ] **Step 1: Full suite**

```bash
dotnet test windows/OnDeck.slnx
```
Expected: `Failed: 0`. Note the new total in the handoff (543 before this phase).

- [ ] **Step 2: Core stays dependency-free**

```bash
grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj
```
Expected: `0`.

- [ ] **Step 3: Single-file publish**

```bash
dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```
Expected: succeeds, one exe under `bin/Release/net10.0-windows/win-x64/publish/`.

- [ ] **Step 4: Run it and look**

Launch the built app (do **not** install it to any system location). With a roster configured, walk this matrix. Where a check needs eyes on pixel colour or acrylic, **ask the repo owner to look** — `ACRYLIC-OPEN-ISSUE.md` records that automated `CopyFromScreen` sampling produced confidently wrong answers here. If a screenshot is taken anyway, save the PNG, open it, locate the flyout rectangle in the image, and only then read pixels.

| Check | Expect |
|---|---|
| Flyout opens with real sections, not the counts placeholder | |
| Section order: ACTIVE NOW → IN GAME → UPCOMING → DONE | |
| Dividers between sections; none dangling after the last one in the panel | |
| A live row shows score, both logos, bases, inning arrow + number, count, outs | |
| Count blanks between at-bats rather than showing a stale 3-2 | |
| A player on base turns that diamond green | |
| At-bat/on-deck/in-hole dots are filled green / outlined green / outlined orange | |
| Clicking a live row opens the stream and dismisses the flyout | |
| UPCOMING shows batting-order number, green tick, or red dot per lineup state | |
| A postponed game shows PPD instead of a first-pitch time | |
| Empty roster shows "Set roster URL in Settings" | |
| Footer Refresh spins, then shows a tick (or a red cross), then returns to the arrow | |
| Double-clicking Refresh fires one sync, not two | |
| Fantrax button opens the league page; hidden when the URL has no leagueID | |
| Quit exits with no process left | |
| **Text is readable in both Windows light and dark app modes** | see note below |
| Float opens the panel; it stays on top; clicking it does not steal focus | |
| Panel drags by its background | |
| Panel position survives a restart | |
| Panel's first section header carries refresh + close; close hides it | |
| `AlwaysOpenPopout` in `%APPDATA%\onDeck\settings.json` auto-opens the panel at launch | |
| Tray right-click has Open / Float / Refresh / Quit | |
| Still open from Phase 6: 100/125/150/200% scaling, docked taskbar edges, second monitor | |

**On the readability check:** the backdrop bug means the flyout surface may be an opaque grey regardless of the app theme. If light mode gives dark text on a dark surface, the fix is one line — set `Root.Background` from `ThemePalette` — but that is a change to the backdrop path, so **raise it with the repo owner rather than applying it unilaterally**, and record the outcome in `ACRYLIC-OPEN-ISSUE.md`.

- [ ] **Step 5: Working tree clean**

```bash
git status --short
```
Expected: empty.

- [ ] **Step 6: Update the docs**

- Fill in this plan's Deviations table with anything that diverged during execution.
- Append the Phase 7b rows to `windows/HANDOFF.md` §8, update §3's test count, replace §9 with Phase 8's scope (Settings window — including the footer Settings button and the tray Settings item this phase deferred), and record the manual-verification results in a table like §8b's.
- If the acrylic situation changed at all, update `windows/ACRYLIC-OPEN-ISSUE.md`.

```bash
git add windows/HANDOFF.md windows/plans windows/ACRYLIC-OPEN-ISSUE.md
git commit -m "phase 7b: handoff notes"
```

---

## Deviations

Fill in during execution; these are the ones known at planning time.

| Deviation | Why |
|---|---|
| Settings footer button and tray Settings item deferred to Phase 8 | `TrayIconService`'s own doc comment sets the convention: a button ships with the window it opens. A button that does nothing is worse than one that isn't there |
| No `matchedGeometryEffect` row-reorder animation (`MenuBarView.swift:181`) | WPF has no equivalent primitive. Rows are replaced wholesale each rebuild |
| Colours come from an app-owned `ThemePalette`, not WPF Fluent's resource keys | A `DynamicResource` naming a key that isn't there resolves to null and renders invisible, with no error at build or run time — the same silent-failure class as the acrylic bug |
| Palette is driven by `AppsUseLightTheme`; the tray icon still uses `SystemUsesLightTheme` | They are separate settings, and "light apps, dark taskbar" is the Windows 11 default pairing |
| Floating panel's close/refresh controls fall back to the empty state's header | Swift renders no header when every list is empty, leaving the panel closable only from the Float button. A borderless window with no taskbar entry needs its own close affordance |
| `TeamLogoStore` sits between the rows and Core's `TeamLogoCache` | Rows rebuild every 10 s during a live game; the path lookup must be synchronous and the fetch must de-duplicate, or a missing logo is re-requested on every rebuild |
| Rows carry a logo **file path**, not an `ImageSource` | WPF's built-in converter turns a path into an image, which keeps the row records plain data and unit-testable |
| Floating panel frame is persisted outside `ISettingsStore` | `PORT_PLAN.md` already scopes it as shell-only; Core has no business knowing a window exists |
| `FloatingPanelPlacement` adds an on-screen check macOS gets for free | `setFrameUsingName` returns false for an unusable frame; Windows has no equivalent, and the panel has no taskbar button to recover it with |
| The floating header's refresh shows a static glyph while syncing, not a spinner | Swift uses a 14pt `ProgressView` there. The outcome tick/cross still shows, and a second rotation storyboard for a 12px glyph isn't worth it. The footer's Refresh does spin |
| `#if DEBUG` memory overlay not ported | `MemoryStats` is macOS-only and explicitly out of scope in `PORT_PLAN.md` |
