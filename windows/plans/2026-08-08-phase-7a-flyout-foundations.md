# Phase 7a: Flyout foundations — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The pieces the flyout's real content needs before any of it can be drawn — correct placement on the monitor the tray is actually on, team logos on disk, and the presentation rules `MenuBarView.swift` applies that Core deliberately left to the shell.

**Scope note:** Phase 7 in `PORT_PLAN.md` is one phase but two jobs. **7a (this plan)** is foundations, all unit-testable. **7b** is the visual layer — `PlayerRow`, the four sections, the footer, `FloatingPanelWindow` — and gets its own plan written against `MenuBarView.swift` when 7a lands. Splitting keeps both plans honest rather than padding one with vague XAML instructions.

**Architecture:** Anything that can be wrong without looking wrong goes in a plain class with tests. XAML gets converters over those classes, never logic of its own.

**Tech Stack:** WPF on `net10.0-windows`, xunit in `OnDeck.App.Tests`, `OnDeck.Core` for the cache.

## Global Constraints

- `OnDeck.Core` keeps **zero** package references. `TeamLogoCache` is portable (raw file cache) and belongs there beside `HeadshotCache`.
- `PlayerDisplay` already carries every field `MenuBarView.swift` reads. **Do not recompute** proximity, sort keys, stat lines, lineup badges or delay classification in the shell — converters map what Core resolved onto brushes and glyphs, nothing more.
- Single-file publish stays green.
- No `MainWindow`; the app is tray-only.

## Correction to the master plan

`PORT_PLAN.md`'s Phase 7 row says the row control shows a *headshot*. **`MenuBarView.swift` renders no headshots** — `LivePlayerRow` shows team logos via `TeamLogo`/`TeamLogoCache`, and `HeadshotCache` exists for notification images only. The Swift source is the authoritative spec, so rows get team logos. The parity checklist line "Headshots render in flyout, floating panel, and toasts" is wrong for the first two and is corrected here.

## File Structure

| File | Responsibility |
|---|---|
| `src/OnDeck.Core/Utilities/TeamLogoCache.cs` | On-demand team logo fetch + disk cache, mirroring `HeadshotCache` |
| `src/OnDeck.App/Platform/MonitorWorkArea.cs` | Work area of the monitor under a point |
| `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs` | Use that work area instead of the primary monitor's |
| `src/OnDeck.App/Views/DisplayFormatting.cs` | Start-time / PPD text, proximity dot style, lineup badge — the `MenuBarView` presentation rules |
| `src/OnDeck.App/Views/Converters.cs` | Thin `IValueConverter` wrappers so XAML can bind to the above |
| `tests/OnDeck.Core.Tests/Utilities/TeamLogoCacheTests.cs` | Cache behaviour |
| `tests/OnDeck.App.Tests/DisplayFormattingTests.cs` | Presentation rules |

---

## Task 1: Place the flyout on the right monitor

**Files:**
- Create: `src/OnDeck.App/Platform/MonitorWorkArea.cs`
- Modify: `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs`

**Interfaces:**
- Produces: `static class MonitorWorkArea` with `Rect? ForDevicePoint(Point devicePixels)` returning the work area **in device pixels**, and `Rect ToDips(Rect devicePixels, Visual visual)`.

**The debt this clears:** `FlyoutWindow.ShowAt` reads `SystemParameters.WorkArea`, which is always the *primary* monitor's. On a second monitor the flyout is placed against the wrong rectangle and lands on the wrong screen. This is thin interop — `MonitorFromPoint` plus `GetMonitorInfo` — so it is verified by the multi-monitor item in Task 4's checklist, not by a unit test. The placement maths it feeds is already covered by `FlyoutPositionerTests`.

- [ ] **Step 1: Write the interop**

Create `src/OnDeck.App/Platform/MonitorWorkArea.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace OnDeck.App.Platform;

/// <summary>
/// The work area of the monitor a point falls on. <c>SystemParameters.WorkArea</c> only ever
/// describes the primary monitor, which puts the flyout on the wrong screen for a tray on any
/// other one.
/// </summary>
public static class MonitorWorkArea
{
    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Work area in <em>device pixels</em>, or null if the shell won't say.</summary>
    public static Rect? ForDevicePoint(Point devicePixels)
    {
        var point = new NativePoint { X = (int)devicePixels.X, Y = (int)devicePixels.Y };
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return null;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        var work = info.WorkArea;
        return new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    /// <summary>
    /// Device pixels to DIPs, using the visual's own composition target so a per-monitor DPI
    /// setup converts against the right scale.
    /// </summary>
    public static Rect ToDips(Rect devicePixels, Visual visual)
    {
        if (PresentationSource.FromVisual(visual)?.CompositionTarget is not { } target)
        {
            return devicePixels;
        }

        var topLeft = target.TransformFromDevice.Transform(devicePixels.TopLeft);
        var bottomRight = target.TransformFromDevice.Transform(devicePixels.BottomRight);
        return new Rect(topLeft, bottomRight);
    }
}
```

- [ ] **Step 2: Use it from the flyout**

In `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs`, replace the body of `ShowAt` and `ToAnchorRect`:

```csharp
    /// <summary>
    /// Opens the flyout anchored at <paramref name="anchorDevicePixels"/> — the cursor, which is
    /// over the tray icon when the user clicks it.
    /// </summary>
    public void ShowAt(Point? anchorDevicePixels)
    {
        RenderSummary();

        // Measure before placing: SizeToContent means Height is only real after a layout pass.
        Show();
        UpdateLayout();

        var workArea = WorkAreaFor(anchorDevicePixels);
        var anchor = AnchorRect(anchorDevicePixels, workArea);

        var placement = FlyoutPositioner.Place(anchor, workArea, new Size(Width, ActualHeight));

        Left = placement.X;
        Top = placement.Y;

        Activate();
    }

    /// <summary>
    /// The work area of the monitor the tray is on, in DIPs. Falls back to the primary
    /// monitor's when there is no anchor or the shell declines to answer.
    /// </summary>
    private Rect WorkAreaFor(Point? anchorDevicePixels)
    {
        if (anchorDevicePixels is { } anchor
            && MonitorWorkArea.ForDevicePoint(anchor) is { } devicePixels)
        {
            return MonitorWorkArea.ToDips(devicePixels, this);
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>
    /// A small box around the cursor stands in for the tray icon's own rectangle. Device pixels
    /// become DIPs here because WPF's Left/Top are DIPs — skipping it misplaces the flyout on
    /// any display not at 100% scaling.
    /// </summary>
    private Rect AnchorRect(Point? devicePixels, Rect workArea)
    {
        if (devicePixels is not { } point)
        {
            // No cursor (e.g. a second launch signalling us): fall back to the tray corner.
            return new Rect(workArea.Right - 24, workArea.Bottom, 24, 24);
        }

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            point = target.TransformFromDevice.Transform(point);
        }

        return new Rect(point.X - 12, point.Y - 12, 24, 24);
    }
```

- [ ] **Step 3: Build and confirm the existing tests still pass**

```bash
dotnet build windows/OnDeck.slnx
dotnet test windows/OnDeck.slnx
```
Expected: `Failed: 0`. `FlyoutPositionerTests` already covers the placement maths this feeds.

- [ ] **Step 4: Commit**

```bash
git add windows/src/OnDeck.App
git commit -m "phase 7a: place the flyout on the monitor holding the tray"
```

---

## Task 2: TeamLogoCache

**Files:**
- Create: `src/OnDeck.Core/Utilities/TeamLogoCache.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/TeamLogoCacheTests.cs`

**Spec:** `Views/MenuBarView.swift:790-831` (`TeamLogoCache`).

**Interfaces:**
- Produces: `sealed class TeamLogoCache(HttpClient http, string cacheDirectory)` with
  `static string DefaultCacheDirectory()`, `string? FilePath(int teamId, int size)`,
  `Task<string?> GetAsync(int teamId, int size, CancellationToken ct = default)`.

**Behaviour:** files are `{cacheDirectory}/{teamId}_{size}.png`, fetched from
`https://midfield.mlbstatic.com/v1/team/{teamId}/spots/{size}`. A cached file is returned without a
request. Failures return null — a missing logo is a blank square, not an error. Unlike
`HeadshotCache` there is no prefetch: logos are fetched on demand for the handful of games on
screen, matching Swift's lazy `.task(id: teamID)`.

Swift's in-memory `NSImage` dictionary is **not** ported — WPF's `BitmapImage` does its own
decode caching from a file URI, so a second memory cache would duplicate it. That also makes
`evictMemoryCache` (called by the macOS-only `MemoryPressureRelief`) unnecessary.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Utilities/TeamLogoCacheTests.cs`:

```csharp
using System.Net;
using OnDeck.Core.Tests.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class TeamLogoCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ondeck-logo-tests", Guid.NewGuid().ToString("N"));

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task GetAsync_DownloadsAndCachesTheLogo()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        var path = await cache.GetAsync(119, 32);

        Assert.NotNull(path);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task GetAsync_RequestsTheMidfieldSpotsUrl()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        await cache.GetAsync(119, 32);

        Assert.Equal(
            "https://midfield.mlbstatic.com/v1/team/119/spots/32", handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetAsync_SkipsTheNetworkWhenAlreadyCached()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        await cache.GetAsync(119, 32);
        await cache.GetAsync(119, 32);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetAsync_KeepsSizesApart()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        var small = await cache.GetAsync(119, 16);
        var large = await cache.GetAsync(119, 32);

        Assert.NotEqual(small, large);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullOnAFailedRequest()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        Assert.Null(await cache.GetAsync(119, 32));
    }

    [Fact]
    public async Task GetAsync_RejectsABodyThatIsNotAPng()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"error":"nope"}""");
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        Assert.Null(await cache.GetAsync(119, 32));
        Assert.False(Directory.Exists(_directory) && Directory.GetFiles(_directory).Length > 0);
    }

    [Fact]
    public void FilePath_IsNullUntilCached()
    {
        var cache = new TeamLogoCache(new StubHttpMessageHandler().CreateClient(), _directory);

        Assert.Null(cache.FilePath(119, 32));
    }

    [Fact]
    public void DefaultCacheDirectory_SitsBesideTheHeadshotCache()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "onDeck",
                "TeamLogos"),
            TeamLogoCache.DefaultCacheDirectory());
    }
}
```

- [ ] **Step 2: Run and confirm failure**

`dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~TeamLogoCacheTests` → build failure, `TeamLogoCache` missing.

- [ ] **Step 3: Implement**

Create `src/OnDeck.Core/Utilities/TeamLogoCache.cs`:

```csharp
namespace OnDeck.Core.Utilities;

/// <summary>
/// Port of <c>TeamLogoCache</c> from <c>Views/MenuBarView.swift</c>. Logos are fetched on demand
/// for the games on screen and kept on disk; the shell loads them from the returned path.
/// <para>
/// Swift's in-memory <c>NSImage</c> dictionary is not ported: WPF's <c>BitmapImage</c> already
/// caches decoded frames per URI, so a second memory cache would only duplicate it — which also
/// makes Swift's <c>evictMemoryCache</c> unnecessary.
/// </para>
/// </summary>
public sealed class TeamLogoCache(HttpClient http, string cacheDirectory)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string DefaultCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "onDeck",
            "TeamLogos");

    /// <summary>The on-disk path for a logo, or null if it hasn't been fetched.</summary>
    public string? FilePath(int teamId, int size)
    {
        var file = PathFor(teamId, size);
        return File.Exists(file) ? file : null;
    }

    /// <summary>Cached path, fetching it first if needed. Null when the logo can't be had.</summary>
    public async Task<string?> GetAsync(int teamId, int size, CancellationToken ct = default)
    {
        if (FilePath(teamId, size) is { } cached) return cached;

        var url = $"https://midfield.mlbstatic.com/v1/team/{teamId}/spots/{size}";

        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (!IsPng(bytes)) return null;

            Directory.CreateDirectory(cacheDirectory);
            var file = PathFor(teamId, size);
            await File.WriteAllBytesAsync(file, bytes, ct);
            return file;
        }
        catch (Exception)
        {
            // A missing logo is a blank square, not a failure worth surfacing.
            return null;
        }
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length > PngSignature.Length
        && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    private string PathFor(int teamId, int size) =>
        Path.Combine(cacheDirectory, $"{teamId}_{size}.png");
}
```

- [ ] **Step 4: Run and confirm green**

`dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~TeamLogoCacheTests` → PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/TeamLogoCache.cs windows/tests/OnDeck.Core.Tests/Utilities/TeamLogoCacheTests.cs
git commit -m "phase 7a: team logo cache"
```

---

## Task 3: Presentation rules

**Files:**
- Create: `src/OnDeck.App/Views/DisplayFormatting.cs`
- Create: `tests/OnDeck.App.Tests/DisplayFormattingTests.cs`

**Spec:** `Views/MenuBarView.swift:378-399` (proximity dot), `:520-528` (`delayIcon`), `:579-618` (upcoming row badge and time/PPD).

**Interfaces:**
- Produces `static class DisplayFormatting` with:
  - `ProximityDot Dot(PlayerDisplay display)` where `enum ProximityDot { None, Filled, Outlined, Warning }`
  - `string? DelayGlyph(DelayIndicator delay)` — Segoe Fluent Icons glyph, or null
  - `string TrailingText(PlayerDisplay display)` — "PPD", a localised start time, or ""
  - `string? LineupBadgeText(PlayerDisplay display)` — the batting order number, or null
  - `LineupBadge Badge(PlayerDisplay display)` where `enum LineupBadge { None, Missing, Present, Order }`

**Why these are worth testing:** each is a small mapping that silently degrades if wrong — a
not-in-lineup hitter losing its red dot, or a postponed game showing a first-pitch time it will
never keep. The Swift original expresses them as `switch`es over enums; so does this.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/DisplayFormattingTests.cs`:

```csharp
using OnDeck.App.Views;
using OnDeck.Core.Models;

namespace OnDeck.App.Tests;

public class DisplayFormattingTests
{
    private static Player Hitter(int id = 101) =>
        new(id, $"Player {id}", "Los Angeles Dodgers",
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static PlayerDisplay Row(
        BattingProximity? proximity = null,
        bool isActive = false,
        LineupInfo lineup = default,
        DelayIndicator delay = DelayIndicator.None,
        DateTimeOffset? startTime = null) =>
        new()
        {
            Player = Hitter(),
            Proximity = proximity,
            IsActive = isActive,
            Lineup = lineup,
            Delay = delay,
            StartTime = startTime,
        };

    [Fact]
    public void Dot_IsFilledAtBatAndOutlinedOnDeck()
    {
        Assert.Equal(ProximityDot.Filled, DisplayFormatting.Dot(Row(BattingProximity.AtBat)));
        Assert.Equal(ProximityDot.Outlined, DisplayFormatting.Dot(Row(BattingProximity.OnDeck)));
        Assert.Equal(ProximityDot.Warning, DisplayFormatting.Dot(Row(BattingProximity.DueUp)));
    }

    [Fact]
    public void Dot_IsAbsentDeeperInTheOrder()
    {
        Assert.Equal(ProximityDot.None, DisplayFormatting.Dot(Row(BattingProximity.Order(5))));
        Assert.Equal(ProximityDot.None, DisplayFormatting.Dot(Row(BattingProximity.NotBatting(2))));
    }

    [Fact]
    public void Dot_FallsBackToTheActiveFlagForPitchers()
    {
        // A pitcher has no proximity; the green dot is driven by being active instead.
        Assert.Equal(ProximityDot.Filled, DisplayFormatting.Dot(Row(isActive: true)));
        Assert.Equal(ProximityDot.None, DisplayFormatting.Dot(Row()));
    }

    [Theory]
    [InlineData(DelayIndicator.None, null)]
    [InlineData(DelayIndicator.Rain, "")]
    [InlineData(DelayIndicator.Delayed, "")]
    [InlineData(DelayIndicator.Postponed, "")]
    public void DelayGlyph_MapsEachIndicator(DelayIndicator delay, string? expected)
    {
        Assert.Equal(expected, DisplayFormatting.DelayGlyph(delay));
    }

    [Fact]
    public void TrailingText_IsPpdForAPostponedGame()
    {
        var row = Row(delay: DelayIndicator.Postponed, startTime: DateTimeOffset.Now.AddHours(3));

        Assert.Equal("PPD", DisplayFormatting.TrailingText(row));
    }

    [Fact]
    public void TrailingText_IsTheLocalStartTime()
    {
        var start = new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero);

        var text = DisplayFormatting.TrailingText(Row(startTime: start));

        Assert.Equal(start.ToLocalTime().ToString("t"), text);
    }

    [Fact]
    public void TrailingText_IsEmptyWithoutAStartTime()
    {
        Assert.Equal("", DisplayFormatting.TrailingText(Row()));
    }

    [Fact]
    public void Badge_ReflectsTheLineupInfo()
    {
        Assert.Equal(LineupBadge.None, DisplayFormatting.Badge(Row(lineup: LineupInfo.Unknown)));
        Assert.Equal(LineupBadge.Missing, DisplayFormatting.Badge(Row(lineup: LineupInfo.NotInLineup)));
        Assert.Equal(LineupBadge.Present, DisplayFormatting.Badge(Row(lineup: LineupInfo.InLineup)));
        Assert.Equal(LineupBadge.Order, DisplayFormatting.Badge(Row(lineup: LineupInfo.BattingOrder(3))));
    }

    [Fact]
    public void LineupBadgeText_IsTheOrderNumberOnly()
    {
        Assert.Equal("3", DisplayFormatting.LineupBadgeText(Row(lineup: LineupInfo.BattingOrder(3))));
        Assert.Null(DisplayFormatting.LineupBadgeText(Row(lineup: LineupInfo.InLineup)));
        Assert.Null(DisplayFormatting.LineupBadgeText(Row(lineup: LineupInfo.Unknown)));
    }
}
```

- [ ] **Step 2: Run and confirm failure**

`dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~DisplayFormattingTests` → build failure.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Views/DisplayFormatting.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.App.Views;

/// <summary>Which dot precedes the player's name on a live row.</summary>
public enum ProximityDot
{
    None,
    Filled,      // at bat, or a pitcher currently on the mound
    Outlined,    // on deck
    Warning,     // in the hole
}

/// <summary>The badge in an upcoming row's leading column.</summary>
public enum LineupBadge
{
    None,
    Missing,     // red dot: this side's card was filed without them
    Present,     // green tick: on the card, spot unknown
    Order,       // their batting order number
}

/// <summary>
/// The presentation rules from <c>Views/MenuBarView.swift</c> that Core deliberately left to the
/// shell: which dot, which glyph, what trailing text. Everything they read was already resolved
/// onto <see cref="PlayerDisplay"/> — nothing here recomputes state.
/// </summary>
public static class DisplayFormatting
{
    // Segoe Fluent Icons. Rain shower, clock-alert, blocked.
    private const string RainGlyph = "";
    private const string DelayedGlyph = "";
    private const string PostponedGlyph = "";

    public static ProximityDot Dot(PlayerDisplay display) => display.Proximity?.Kind switch
    {
        BattingProximityKind.AtBat => ProximityDot.Filled,
        BattingProximityKind.OnDeck => ProximityDot.Outlined,
        BattingProximityKind.DueUp => ProximityDot.Warning,
        BattingProximityKind.Order or BattingProximityKind.NotBatting => ProximityDot.None,

        // No proximity at all - a pitcher. Swift shows the filled dot when they're active.
        _ => display.IsActive ? ProximityDot.Filled : ProximityDot.None,
    };

    public static string? DelayGlyph(DelayIndicator delay) => delay switch
    {
        DelayIndicator.Rain => RainGlyph,
        DelayIndicator.Delayed => DelayedGlyph,
        DelayIndicator.Postponed => PostponedGlyph,
        _ => null,
    };

    /// <summary>Right-hand text on an upcoming row: "PPD" or the local first-pitch time.</summary>
    public static string TrailingText(PlayerDisplay display)
    {
        if (display.Delay == DelayIndicator.Postponed) return "PPD";
        return display.StartTime is { } start ? start.ToLocalTime().ToString("t") : "";
    }

    public static LineupBadge Badge(PlayerDisplay display) => display.Lineup.Kind switch
    {
        LineupInfoKind.NotInLineup => LineupBadge.Missing,
        LineupInfoKind.InLineup => LineupBadge.Present,
        LineupInfoKind.BattingOrder => LineupBadge.Order,
        _ => LineupBadge.None,
    };

    public static string? LineupBadgeText(PlayerDisplay display) =>
        display.Lineup.Kind == LineupInfoKind.BattingOrder
            ? display.Lineup.Spot.ToString()
            : null;
}
```

- [ ] **Step 4: Run and confirm green**

`dotnet test windows/OnDeck.slnx` → `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Views windows/tests/OnDeck.App.Tests/DisplayFormattingTests.cs
git commit -m "phase 7a: flyout presentation rules"
```

---

## Task 4: Verify and hand off to 7b

- [ ] `dotnet test windows/OnDeck.slnx` → `Failed: 0`
- [ ] `grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj` → `0`
- [ ] Single-file publish green
- [ ] Run the app and re-check the Phase 6 items that were never exercised: theme swap with the app running, acrylic vs fallback, second monitor, display scaling, double launch, Quit
- [ ] `git status --short` → clean
- [ ] Write the 7b plan against `MenuBarView.swift`: `PlayerRow`, the four sections plus empty/error states, footer buttons with the four-state Refresh, and `FloatingPanelWindow` with persisted frame

## Deviations

| Deviation | Why |
|---|---|
| Phase 7 split into 7a (foundations) and 7b (visual layer) | One plan covering both would either run to thousands of lines or pad the XAML half with vague instructions |
| Rows show team logos, not headshots | `MenuBarView.swift` renders no headshots; `PORT_PLAN.md`'s Phase 7 row and parity checklist are wrong on this point |
| `TeamLogoCache` has no in-memory image cache | WPF's `BitmapImage` caches decoded frames per URI already; Swift's dictionary (and its `evictMemoryCache`) would be redundant |
| Monitor lookup is verified manually, not by unit test | `MonitorFromPoint`/`GetMonitorInfo` is thin interop; the placement maths it feeds is already covered by `FlyoutPositionerTests` |
