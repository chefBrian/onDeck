# Phase 6: Shell skeleton — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A running Windows tray app — icon, tooltip, context menu, light-dismissing flyout shell — driven by the finished `OnDeck.Core`, with settings persisted to disk.

**Architecture:** `OnDeck.App` gains a composition root that builds the managers and `AppOrchestrator` **on the WPF Dispatcher thread** (Core captures that `SynchronizationContext` in its constructor). Anything with real logic — settings persistence, flyout placement, theme→icon selection — lives in a plain testable class; XAML and P/Invoke stay thin. Fluent styling and acrylic come from .NET 10's built-in theme plus `DwmSetWindowAttribute`, not a third-party UI library.

**Tech Stack:** WPF on `net10.0-windows`, `Hardcodet.NotifyIcon.Wpf` 2.0.1, xunit in a new `OnDeck.App.Tests`, DWM interop.

## Global Constraints

- `OnDeck.Core` stays untouched and keeps **zero** package references. All new packages go in `OnDeck.App`.
- **Construct `AppOrchestrator` on the Dispatcher thread.** It captures `SynchronizationContext.Current`; building it anywhere else silently breaks the coalesced rebuild and the race guards.
- Single-file publish stays green; no `PublishTrimmed`.
- `EnableWindowsTargeting=true` stays so the shell compile-checks off-Windows.
- The spike's findings bind (`spikes/ToastActivationSpike/FINDINGS.md`): activation routes through the registered CLSID, and the single-instance guard must not kill a `-ToastActivated -Embedding` launch before its activation is handled.
- Toast work itself is **Phase 9**. Phase 6 injects a logging `INotificationSink` so the app runs end to end.

## Decisions taken in this phase

| Decision | Rationale |
|---|---|
| Native .NET 10 Fluent (`ThemeMode=System`) + DWM interop instead of WPF-UI | The framework now covers what WPF-UI was chosen for. One less dependency, smaller exe, no theming conflicts. Confirmed with the user 2026-08-08 |
| `SettingsStore` lands here, not in Phase 8 | Nothing runs without an `ISettingsStore` — the composition root needs it to build `AppOrchestrator`. Phase 8 becomes purely the settings **UI** |
| Context menu ships Open / Refresh / Quit only | Float is Phase 7 and Settings is Phase 8; wiring dead menu items now would be a placeholder |
| Tray icons drop the 6 stitch marks at 16 px and 20 px | Tabler's `ball-baseball` has ten strokes in a 24-unit box; at 16 px the stitches turn to mud. Circle + two seams stay legible. MIT licence permits the edit |

## File Structure

| File | Responsibility |
|---|---|
| `tools/IconGen/` | One-shot generator: Tabler path data → multi-res `.ico` per colour. Not in the solution |
| `src/OnDeck.App/Assets/tray-{white,dark,green}.ico` | Generated tray icons, 16/20/24/32 |
| `src/OnDeck.App/SettingsStore.cs` | `ISettingsStore` over JSON at `%APPDATA%\onDeck\settings.json`, atomic write |
| `src/OnDeck.App/Tray/TrayIconVariant.cs` | The theme→icon rule as a pure function |
| `src/OnDeck.App/Tray/ThemeWatcher.cs` | Reads `SystemUsesLightTheme`, listens for `WM_SETTINGCHANGE` |
| `src/OnDeck.App/Tray/TrayIconService.cs` | `TaskbarIcon`, tooltip, context menu, icon swap |
| `src/OnDeck.App/Windows/FlyoutPositioner.cs` | Tray rect + work area + size → placement. Pure |
| `src/OnDeck.App/Windows/FlyoutWindow.xaml{,.cs}` | Borderless, light-dismiss, DWM backdrop |
| `src/OnDeck.App/System/SingleInstance.cs` | Named mutex + activate-existing |
| `src/OnDeck.App/System/DwmBackdrop.cs` | `DwmSetWindowAttribute` wrapper with fallback |
| `src/OnDeck.App/System/TrayGeometry.cs` | `Shell_NotifyIconGetRect` interop |
| `src/OnDeck.App/Notifications/LoggingNotificationSink.cs` | Stand-in until Phase 9 |
| `src/OnDeck.App/App.xaml{,.cs}` | Composition root |
| `tests/OnDeck.App.Tests/` | `SettingsStore`, `FlyoutPositioner`, `TrayIconVariant` |

---

## Task 1: Tray icon assets

**Files:**
- Create: `tools/IconGen/IconGen.csproj`, `tools/IconGen/Program.cs`
- Create (generated, committed): `src/OnDeck.App/Assets/tray-white.ico`, `tray-dark.ico`, `tray-green.ico`

**Interfaces:** produces three `.ico` files, each containing 16/20/24/32 px PNG-compressed frames.

**Why a generator rather than checked-in art:** the three colours and four sizes are twelve renders; regenerating after a tweak must not be a manual chore, and the Tabler path data belongs in source where its MIT provenance is visible.

- [ ] **Step 1: Write the generator**

Create `tools/IconGen/IconGen.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Create `tools/IconGen/Program.cs`:

```csharp
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Renders Tabler Icons' `ball-baseball` (MIT) to multi-resolution .ico files.
// Usage: dotnet run --project tools/IconGen -- <output-directory>

// Path data straight from tabler-icons/icons/outline/ball-baseball.svg, 24x24 viewBox,
// stroke-width 2, round caps and joins.
string[] outline =
[
    "M5.636 18.364a9 9 0 1 0 12.728 -12.728a9 9 0 0 0 -12.728 12.728",
    "M12.495 3.02a9 9 0 0 1 -9.475 9.475",
    "M20.98 11.505a9 9 0 0 0 -9.475 9.475",
];

// The stitches are dropped below 24 px - ten strokes in a 24-unit box turns to mud at 16.
string[] stitches =
[
    "M9 9l2 2",
    "M13 13l2 2",
    "M11 7l2 1",
    "M7 11l1 2",
    "M16 11l1 2",
    "M11 16l2 1",
];

(string Name, Color Colour)[] variants =
[
    ("tray-white", Color.FromRgb(0xFF, 0xFF, 0xFF)),
    ("tray-dark", Color.FromRgb(0x1A, 0x1A, 0x1A)),
    ("tray-green", Color.FromRgb(0x34, 0xC7, 0x59)),
];

int[] sizes = [16, 20, 24, 32];

var outputDirectory = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outputDirectory);

foreach (var (name, colour) in variants)
{
    var frames = sizes.Select(size => RenderPng(size, colour)).ToArray();
    var path = Path.Combine(outputDirectory, name + ".ico");
    File.WriteAllBytes(path, PackIco(sizes, frames));
    Console.WriteLine($"{path}  {new FileInfo(path).Length} bytes  ({string.Join(", ", sizes)})");
}

return 0;

byte[] RenderPng(int size, Color colour)
{
    var pen = new Pen(new SolidColorBrush(colour), 2)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
    pen.Freeze();

    var paths = size >= 24 ? outline.Concat(stitches) : outline;

    var visual = new DrawingVisual();
    using (var context = visual.RenderOpen())
    {
        // The 24-unit design box scales to the target; strokes scale with it.
        context.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
        foreach (var data in paths) context.DrawGeometry(null, pen, Geometry.Parse(data));
        context.Pop();
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));

    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

// ICONDIR + one ICONDIRENTRY per frame + the PNG payloads. PNG-compressed frames are
// supported by every Windows since Vista and keep the file small.
static byte[] PackIco(int[] sizes, byte[][] frames)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0);            // reserved
    writer.Write((ushort)1);            // type: icon
    writer.Write((ushort)frames.Length);

    var offset = 6 + (16 * frames.Length);
    for (var i = 0; i < frames.Length; i++)
    {
        writer.Write((byte)sizes[i]);   // width  (0 would mean 256)
        writer.Write((byte)sizes[i]);   // height
        writer.Write((byte)0);          // palette size
        writer.Write((byte)0);          // reserved
        writer.Write((ushort)1);        // colour planes
        writer.Write((ushort)32);       // bits per pixel
        writer.Write(frames[i].Length);
        writer.Write(offset);
        offset += frames[i].Length;
    }

    foreach (var frame in frames) writer.Write(frame);

    writer.Flush();
    return stream.ToArray();
}
```

- [ ] **Step 2: Generate the icons**

```bash
dotnet run --project windows/tools/IconGen -- windows/src/OnDeck.App/Assets
```
Expected: three lines naming the files, each a few KB, listing sizes `16, 20, 24, 32`.

- [ ] **Step 3: Verify them visually**

Render one variant to PNG in the scratchpad and look at it — a malformed arc or a wrong scale is
obvious in the image and invisible in the byte count. Confirm: a round ball, two curved seams, no
clipping at the edges, strokes not washed out at 16 px.

- [ ] **Step 4: Reference the assets from the app**

Add to `src/OnDeck.App/OnDeck.App.csproj`:

```xml
  <ItemGroup>
    <Resource Include="Assets\tray-white.ico" />
    <Resource Include="Assets\tray-dark.ico" />
    <Resource Include="Assets\tray-green.ico" />
  </ItemGroup>
```

- [ ] **Step 5: Commit**

```bash
git add windows/tools/IconGen windows/src/OnDeck.App/Assets windows/src/OnDeck.App/OnDeck.App.csproj
git commit -m "phase 6: tray icon assets and their generator"
```

---

## Task 2: SettingsStore

**Files:**
- Create: `src/OnDeck.App/SettingsStore.cs`
- Create: `tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj`, `tests/OnDeck.App.Tests/SettingsStoreTests.cs`
- Modify: `OnDeck.slnx`

**Interfaces:**
- Produces: `sealed class SettingsStore : ISettingsStore` with `SettingsStore(string? directory = null)` (defaults to `%APPDATA%\onDeck`) and `static string DefaultDirectory()`.

**Behaviour:** every setter writes the whole file immediately, via temp-file-then-move so a crash mid-write can't truncate settings. Unreadable or corrupt JSON falls back to defaults rather than throwing — a settings file is not worth losing the app over. The five `Notify*` flags default **true**, matching `UserDefaults.bool(forKey:default:)` on the Mac.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\OnDeck.App\OnDeck.App.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

(Match the package versions already used by `OnDeck.Core.Tests` — read that csproj and copy them rather than pinning blind.)

Create `tests/OnDeck.App.Tests/SettingsStoreTests.cs`:

```csharp
using System.IO;

namespace OnDeck.App.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ondeck-settings-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void NotificationTogglesDefaultToOn()
    {
        var settings = new SettingsStore(_directory);

        Assert.True(settings.NotifyBatting);
        Assert.True(settings.NotifyPitching);
        Assert.True(settings.NotifyAtBatResult);
        Assert.True(settings.NotifyPitchingResult);
        Assert.True(settings.NotifyNotInLineup);
    }

    [Fact]
    public void EverythingElseDefaultsToEmpty()
    {
        var settings = new SettingsStore(_directory);

        Assert.Null(settings.RosterUrl);
        Assert.Null(settings.SelectedTeamId);
        Assert.False(settings.HideBenchPlayers);
        Assert.False(settings.AlwaysOpenPopout);
        Assert.Null(settings.RosterCacheJson);
    }

    [Fact]
    public void ValuesRoundTripThroughANewInstance()
    {
        var first = new SettingsStore(_directory)
        {
            RosterUrl = "https://www.fantrax.com/fantasy/league/lg1/team/roster;teamId=t1",
            SelectedTeamId = "t2",
            HideBenchPlayers = true,
            AlwaysOpenPopout = true,
            NotifyBatting = false,
            RosterCacheJson = """[{"id":101}]""",
        };

        var second = new SettingsStore(_directory);

        Assert.Equal(first.RosterUrl, second.RosterUrl);
        Assert.Equal("t2", second.SelectedTeamId);
        Assert.True(second.HideBenchPlayers);
        Assert.True(second.AlwaysOpenPopout);
        Assert.False(second.NotifyBatting);
        Assert.True(second.NotifyPitching);          // untouched flag keeps its default
        Assert.Equal("""[{"id":101}]""", second.RosterCacheJson);
    }

    [Fact]
    public void WritingCreatesTheDirectoryAndLeavesNoTempFile()
    {
        _ = new SettingsStore(_directory) { HideBenchPlayers = true };

        Assert.True(File.Exists(SettingsPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void CorruptJsonFallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var settings = new SettingsStore(_directory);

        Assert.Null(settings.RosterUrl);
        Assert.True(settings.NotifyBatting);
    }

    [Fact]
    public void AnUnknownPropertyInTheFileIsIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"rosterUrl":"https://x","somethingNew":42}""");

        var settings = new SettingsStore(_directory);

        Assert.Equal("https://x", settings.RosterUrl);
    }

    [Fact]
    public void DefaultDirectoryLivesUnderAppData()
    {
        var directory = SettingsStore.DefaultDirectory();

        Assert.EndsWith(Path.Combine("onDeck"), directory);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), directory);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Add the test project to the solution, then run:
```bash
dotnet sln windows/OnDeck.slnx add windows/tests/OnDeck.App.Tests/OnDeck.App.Tests.csproj
dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~SettingsStoreTests
```
Expected: build failure — `SettingsStore` does not exist.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/SettingsStore.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnDeck.Core;

namespace OnDeck.App;

/// <summary>
/// <see cref="ISettingsStore"/> over a JSON file at <c>%APPDATA%\onDeck\settings.json</c> — the
/// Windows stand-in for the Mac's UserDefaults. Every setter rewrites the file through a temp
/// file and a move, so a crash mid-write cannot truncate it.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly Lock _gate = new();
    private Snapshot _values;
    private bool _loading;

    public SettingsStore(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory();
        _path = Path.Combine(_directory, "settings.json");
        _values = Load();
    }

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "onDeck");

    public string? RosterUrl
    {
        get => _values.RosterUrl;
        set => Update(values => values with { RosterUrl = value });
    }

    public string? SelectedTeamId
    {
        get => _values.SelectedTeamId;
        set => Update(values => values with { SelectedTeamId = value });
    }

    public bool HideBenchPlayers
    {
        get => _values.HideBenchPlayers;
        set => Update(values => values with { HideBenchPlayers = value });
    }

    public bool AlwaysOpenPopout
    {
        get => _values.AlwaysOpenPopout;
        set => Update(values => values with { AlwaysOpenPopout = value });
    }

    public bool NotifyBatting
    {
        get => _values.NotifyBatting;
        set => Update(values => values with { NotifyBatting = value });
    }

    public bool NotifyPitching
    {
        get => _values.NotifyPitching;
        set => Update(values => values with { NotifyPitching = value });
    }

    public bool NotifyAtBatResult
    {
        get => _values.NotifyAtBatResult;
        set => Update(values => values with { NotifyAtBatResult = value });
    }

    public bool NotifyPitchingResult
    {
        get => _values.NotifyPitchingResult;
        set => Update(values => values with { NotifyPitchingResult = value });
    }

    public bool NotifyNotInLineup
    {
        get => _values.NotifyNotInLineup;
        set => Update(values => values with { NotifyNotInLineup = value });
    }

    public string? RosterCacheJson
    {
        get => _values.RosterCacheJson;
        set => Update(values => values with { RosterCacheJson = value });
    }

    private void Update(Func<Snapshot, Snapshot> change)
    {
        lock (_gate)
        {
            _values = change(_values);
            if (!_loading) Save();
        }
    }

    private Snapshot Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Snapshot();
            return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), Options)
                   ?? new Snapshot();
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file is not worth failing startup over.
            return new Snapshot();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_values, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing one write is better than taking the app down mid-session.
        }
    }

    /// <summary>The persisted shape. Defaults here are the defaults the app starts with.</summary>
    private sealed record Snapshot
    {
        public string? RosterUrl { get; init; }
        public string? SelectedTeamId { get; init; }
        public bool HideBenchPlayers { get; init; }
        public bool AlwaysOpenPopout { get; init; }
        public bool NotifyBatting { get; init; } = true;
        public bool NotifyPitching { get; init; } = true;
        public bool NotifyAtBatResult { get; init; } = true;
        public bool NotifyPitchingResult { get; init; } = true;
        public bool NotifyNotInLineup { get; init; } = true;
        public string? RosterCacheJson { get; init; }
    }
}
```

- [ ] **Step 4: Run and confirm green**

`dotnet test windows/OnDeck.slnx --filter FullyQualifiedName~SettingsStoreTests` → PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/SettingsStore.cs windows/tests/OnDeck.App.Tests windows/OnDeck.slnx
git commit -m "phase 6: JSON settings store with atomic writes"
```

---

## Task 3: Icon variant rule and flyout placement

**Files:**
- Create: `src/OnDeck.App/Tray/TrayIconVariant.cs`, `src/OnDeck.App/Windows/FlyoutPositioner.cs`
- Create: `tests/OnDeck.App.Tests/TrayIconVariantTests.cs`, `tests/OnDeck.App.Tests/FlyoutPositionerTests.cs`

**Interfaces:**
- Produces: `enum TrayIcon { White, Dark, Green }` and `static class TrayIconVariant` with `TrayIcon Select(bool systemUsesLightTheme, bool hasActivePlayers)` and `string ResourcePath(TrayIcon icon)`.
- Produces: `static class FlyoutPositioner` with `Point Place(Rect trayIcon, Rect workArea, Size flyout, double gap = 8)`.

**Why these two are tested and the rest of the shell isn't:** they are the only parts of the window layer that can be wrong without being obviously wrong on screen. A flyout that lands 4 px off a 3840-wide monitor's edge, or on the wrong side of a left-docked taskbar, is exactly the bug that survives a casual look.

- [ ] **Step 1: Write the failing tests**

Create `tests/OnDeck.App.Tests/TrayIconVariantTests.cs`:

```csharp
using OnDeck.App.Tray;

namespace OnDeck.App.Tests;

public class TrayIconVariantTests
{
    [Theory]
    [InlineData(false, false, TrayIcon.White)]   // dark taskbar, idle
    [InlineData(true, false, TrayIcon.Dark)]     // light taskbar, idle
    [InlineData(false, true, TrayIcon.Green)]    // active wins over theme
    [InlineData(true, true, TrayIcon.Green)]
    public void Select_PrefersActiveThenContrastsWithTheTaskbar(
        bool systemUsesLightTheme, bool hasActivePlayers, TrayIcon expected)
    {
        Assert.Equal(expected, TrayIconVariant.Select(systemUsesLightTheme, hasActivePlayers));
    }

    [Theory]
    [InlineData(TrayIcon.White, "tray-white.ico")]
    [InlineData(TrayIcon.Dark, "tray-dark.ico")]
    [InlineData(TrayIcon.Green, "tray-green.ico")]
    public void ResourcePath_PointsAtTheGeneratedAsset(TrayIcon icon, string file)
    {
        Assert.EndsWith(file, TrayIconVariant.ResourcePath(icon));
        Assert.StartsWith("pack://application:,,,/", TrayIconVariant.ResourcePath(icon));
    }
}
```

Create `tests/OnDeck.App.Tests/FlyoutPositionerTests.cs`:

```csharp
using System.Windows;
using OnDeck.App.Windows;

namespace OnDeck.App.Tests;

public class FlyoutPositionerTests
{
    // 1920x1080 with a 48 px taskbar docked at the bottom.
    private static readonly Rect BottomWorkArea = new(0, 0, 1920, 1032);
    private static readonly Size Flyout = new(300, 500);

    [Fact]
    public void Place_PutsTheFlyoutAboveABottomDockedTray()
    {
        var tray = new Rect(1850, 1040, 24, 24);

        var point = FlyoutPositioner.Place(tray, BottomWorkArea, Flyout);

        Assert.Equal(1032 - 500 - 8, point.Y);              // above the work area edge
        Assert.True(point.X + 300 <= 1920 - 8);             // right-aligned, inside the screen
    }

    [Fact]
    public void Place_RightAlignsWithTheTrayIcon()
    {
        var tray = new Rect(1850, 1040, 24, 24);

        var point = FlyoutPositioner.Place(tray, BottomWorkArea, Flyout);

        Assert.Equal(1874 - 300, point.X);                  // tray right edge minus width
    }

    [Fact]
    public void Place_DropsBelowATopDockedTaskbar()
    {
        var workArea = new Rect(0, 48, 1920, 1032);
        var tray = new Rect(1850, 12, 24, 24);

        var point = FlyoutPositioner.Place(tray, workArea, Flyout);

        Assert.Equal(48 + 8, point.Y);
    }

    [Fact]
    public void Place_SitsBesideALeftDockedTaskbar()
    {
        var workArea = new Rect(62, 0, 1858, 1080);
        var tray = new Rect(10, 1000, 24, 24);

        var point = FlyoutPositioner.Place(tray, workArea, Flyout);

        Assert.Equal(62 + 8, point.X);
        Assert.True(point.Y + 500 <= 1080 - 8);
    }

    [Fact]
    public void Place_SitsBesideARightDockedTaskbar()
    {
        var workArea = new Rect(0, 0, 1858, 1080);
        var tray = new Rect(1880, 1000, 24, 24);

        var point = FlyoutPositioner.Place(tray, workArea, Flyout);

        Assert.Equal(1858 - 300 - 8, point.X);
    }

    [Fact]
    public void Place_ClampsToTheWorkAreaWhenTheTrayIsNearACorner()
    {
        var tray = new Rect(4, 1040, 24, 24);           // tray icon hard against the left edge

        var point = FlyoutPositioner.Place(tray, BottomWorkArea, Flyout);

        Assert.True(point.X >= 8);
    }

    [Fact]
    public void Place_HandlesAFlyoutTallerThanTheWorkArea()
    {
        var point = FlyoutPositioner.Place(
            new Rect(1850, 1040, 24, 24), BottomWorkArea, new Size(300, 2000));

        Assert.Equal(0, point.Y);                        // pinned to the top, never negative
    }

    [Fact]
    public void Place_UsesTheMonitorTheTrayIsOn()
    {
        // Second monitor to the right: work area origin is not (0,0).
        var workArea = new Rect(1920, 0, 1920, 1032);
        var tray = new Rect(3770, 1040, 24, 24);

        var point = FlyoutPositioner.Place(tray, workArea, Flyout);

        Assert.True(point.X >= 1920);
        Assert.Equal(1032 - 500 - 8, point.Y);
    }
}
```

- [ ] **Step 2: Run and confirm failure**

`dotnet test windows/OnDeck.slnx --filter "FullyQualifiedName~TrayIconVariantTests|FullyQualifiedName~FlyoutPositionerTests"` → build failure, types missing.

- [ ] **Step 3: Implement**

Create `src/OnDeck.App/Tray/TrayIconVariant.cs`:

```csharp
namespace OnDeck.App.Tray;

public enum TrayIcon
{
    White,
    Dark,
    Green,
}

/// <summary>
/// The macOS menu bar draws a template image the system recolours; Windows has no equivalent,
/// so the shell picks an asset. Green means at least one player is active — the whole point of
/// the app — and outranks taskbar contrast.
/// </summary>
public static class TrayIconVariant
{
    public static TrayIcon Select(bool systemUsesLightTheme, bool hasActivePlayers) =>
        hasActivePlayers ? TrayIcon.Green
        : systemUsesLightTheme ? TrayIcon.Dark
        : TrayIcon.White;

    public static string ResourcePath(TrayIcon icon) => icon switch
    {
        TrayIcon.Dark => "pack://application:,,,/Assets/tray-dark.ico",
        TrayIcon.Green => "pack://application:,,,/Assets/tray-green.ico",
        _ => "pack://application:,,,/Assets/tray-white.ico",
    };
}
```

Create `src/OnDeck.App/Windows/FlyoutPositioner.cs`:

```csharp
using System.Windows;

namespace OnDeck.App.Windows;

/// <summary>
/// Places the flyout against the tray icon. The taskbar edge is inferred from where the tray
/// icon sits relative to the work area rather than asked for directly, so a docked-left or
/// docked-top taskbar needs no special case at the call site.
/// </summary>
public static class FlyoutPositioner
{
    public static Point Place(Rect trayIcon, Rect workArea, Size flyout, double gap = 8)
    {
        var trayCentreX = trayIcon.X + (trayIcon.Width / 2);
        var trayCentreY = trayIcon.Y + (trayIcon.Height / 2);

        double x;
        double y;

        if (trayCentreY >= workArea.Bottom)
        {
            // Taskbar along the bottom: sit above it, right edge aligned with the icon.
            x = trayIcon.Right - flyout.Width;
            y = workArea.Bottom - flyout.Height - gap;
        }
        else if (trayCentreY <= workArea.Top)
        {
            x = trayIcon.Right - flyout.Width;
            y = workArea.Top + gap;
        }
        else if (trayCentreX <= workArea.Left)
        {
            x = workArea.Left + gap;
            y = trayIcon.Bottom - flyout.Height;
        }
        else
        {
            x = workArea.Right - flyout.Width - gap;
            y = trayIcon.Bottom - flyout.Height;
        }

        return new Point(Clamp(x, workArea.Left, workArea.Right, flyout.Width, gap),
                         Clamp(y, workArea.Top, workArea.Bottom, flyout.Height, gap));
    }

    /// <summary>
    /// Keeps the flyout inside the work area. When it simply doesn't fit, the near edge wins —
    /// a window pinned to the top with its bottom off-screen beats one positioned at a negative
    /// coordinate on the wrong monitor.
    /// </summary>
    private static double Clamp(double value, double min, double max, double extent, double gap)
    {
        var upper = max - extent - gap;
        var lower = min + gap;
        if (upper < lower) return Math.Max(min, 0);
        return Math.Clamp(value, lower, upper);
    }
}
```

- [ ] **Step 4: Run and confirm green**

`dotnet test windows/OnDeck.slnx` → `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App/Tray windows/src/OnDeck.App/Windows windows/tests/OnDeck.App.Tests
git commit -m "phase 6: tray icon variant rule and flyout placement"
```

---

## Task 4: Single-instance guard and system interop

**Files:**
- Create: `src/OnDeck.App/System/SingleInstance.cs`, `src/OnDeck.App/System/TrayGeometry.cs`, `src/OnDeck.App/System/DwmBackdrop.cs`
- Create: `src/OnDeck.App/Tray/ThemeWatcher.cs`

**Interfaces:**
- Produces: `sealed class SingleInstance : IDisposable` with `static bool TryAcquire(out SingleInstance? instance)`, `event Action? SecondInstanceStarted`, `static void SignalExistingInstance()`.
- Produces: `static class TrayGeometry` with `Rect? IconRectangle(IntPtr windowHandle, uint iconId)`.
- Produces: `static class DwmBackdrop` with `bool TryApplyAcrylic(IntPtr handle)` and `void RoundCorners(IntPtr handle)`.
- Produces: `sealed class ThemeWatcher : IDisposable` with `bool SystemUsesLightTheme { get; }` and `event Action? Changed`.

**The toast interaction:** a cold toast click launches the exe with `-ToastActivated -Embedding`. That process must be allowed to live long enough for the Toolkit to raise `OnActivated`; killing it as a "duplicate" would swallow the click. `TryAcquire` is therefore only consulted for ordinary launches — see the composition root in Task 6.

- [ ] **Step 1: Implement the guard**

Create `src/OnDeck.App/System/SingleInstance.cs`:

```csharp
using System.Threading;

namespace OnDeck.App.System;

/// <summary>
/// One tray icon per user session. The second launch signals the first and exits; the first
/// responds by opening its flyout, which is what a user double-clicking the exe expects.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\onDeck.singleInstance";
    private const string SignalName = @"Local\onDeck.showFlyout";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal, (_, _) => SecondInstanceStarted?.Invoke(), null, Timeout.Infinite, false);
    }

    /// <summary>Raised on a thread-pool thread when another launch signals us.</summary>
    public event Action? SecondInstanceStarted;

    public static bool TryAcquire(out SingleInstance? instance)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex);
        return true;
    }

    public static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SignalName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance exited between our mutex check and here. Nothing to signal.
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _signal.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
```

- [ ] **Step 2: Implement the interop helpers**

Create `src/OnDeck.App/System/TrayGeometry.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;

namespace OnDeck.App.System;

/// <summary>
/// Asks the shell where our tray icon actually is. Guessing the bottom-right corner breaks on
/// docked taskbars, overflow flyouts and multi-monitor setups.
/// </summary>
public static class TrayGeometry
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Item;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(
        ref NotifyIconIdentifier identifier, out NativeRect rectangle);

    /// <summary>Screen-pixel rectangle of the icon, or null when the shell won't say.</summary>
    public static Rect? IconRectangle(IntPtr windowHandle, uint iconId)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = windowHandle,
            Id = iconId,
        };

        if (Shell_NotifyIconGetRect(ref identifier, out var rectangle) != 0) return null;

        return new Rect(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right - rectangle.Left,
            rectangle.Bottom - rectangle.Top);
    }
}
```

Create `src/OnDeck.App/System/DwmBackdrop.cs`:

```csharp
using System.Runtime.InteropServices;

namespace OnDeck.App.System;

/// <summary>
/// Acrylic and rounded corners straight from DWM. Both attributes are Win11-version-sensitive,
/// so every call reports whether it took and the caller falls back to a solid brush.
/// </summary>
public static class DwmBackdrop
{
    private const int SystemBackdropType = 38;      // DWMWA_SYSTEMBACKDROP_TYPE
    private const int CornerPreference = 33;        // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int TransientWindow = 3;          // DWMSBT_TRANSIENTWINDOW (acrylic)
    private const int RoundedCorners = 2;           // DWMWCP_ROUND

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    public static bool TryApplyAcrylic(IntPtr handle)
    {
        var value = TransientWindow;
        return DwmSetWindowAttribute(handle, SystemBackdropType, ref value, sizeof(int)) == 0;
    }

    public static void RoundCorners(IntPtr handle)
    {
        var value = RoundedCorners;
        DwmSetWindowAttribute(handle, CornerPreference, ref value, sizeof(int));
    }
}
```

Create `src/OnDeck.App/Tray/ThemeWatcher.cs`:

```csharp
using Microsoft.Win32;

namespace OnDeck.App.Tray;

/// <summary>
/// Tracks the taskbar's light/dark setting so the tray icon keeps contrast. Windows raises
/// <c>UserPreferenceChanged</c> for this; the registry value is the source of truth.
/// </summary>
public sealed class ThemeWatcher : IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public ThemeWatcher()
    {
        SystemUsesLightTheme = ReadSystemUsesLightTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the taskbar is light, which needs the dark icon.</summary>
    public bool SystemUsesLightTheme { get; private set; }

    public event Action? Changed;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;

        var current = ReadSystemUsesLightTheme();
        if (current == SystemUsesLightTheme) return;

        SystemUsesLightTheme = current;
        Changed?.Invoke();
    }

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;       // assume the dark taskbar default
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
```

`SystemEvents` needs `Microsoft.Win32.SystemEvents`, which is already in the WPF framework
reference — no package required. Add `using System.IO;` for the `IOException` catch.

- [ ] **Step 3: Build**

`dotnet build windows/OnDeck.slnx` → succeeds.

- [ ] **Step 4: Commit**

```bash
git add windows/src/OnDeck.App/System windows/src/OnDeck.App/Tray/ThemeWatcher.cs
git commit -m "phase 6: single-instance guard, tray geometry, DWM and theme interop"
```

---

## Task 5: Tray icon and flyout window

**Files:**
- Create: `src/OnDeck.App/Tray/TrayIconService.cs`
- Create: `src/OnDeck.App/Windows/FlyoutWindow.xaml`, `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs`
- Modify: `src/OnDeck.App/OnDeck.App.csproj` (add `Hardcodet.NotifyIcon.Wpf`)
- Delete: `src/OnDeck.App/MainWindow.xaml`, `src/OnDeck.App/MainWindow.xaml.cs`

**Interfaces:**
- Produces: `sealed class TrayIconService : IDisposable` — `TrayIconService(AppOrchestrator orchestrator)`, `event Action? OpenRequested`, `void Refresh()`, `IntPtr WindowHandle { get; }`, `uint IconId { get; }`.
- Produces: `sealed class FlyoutWindow : Window` — `void ShowAt(Rect trayIcon)`, and light-dismiss on deactivate.

The flyout's **content** is Phase 7. Phase 6 puts a placeholder panel in it that shows the live
section counts, which is enough to prove the binding and the positioning are real.

- [ ] **Step 1: Add the package and drop the template window**

```bash
dotnet add windows/src/OnDeck.App package Hardcodet.NotifyIcon.Wpf --version 2.0.1
rm windows/src/OnDeck.App/MainWindow.xaml windows/src/OnDeck.App/MainWindow.xaml.cs
```

- [ ] **Step 2: Write the flyout window**

Create `src/OnDeck.App/Windows/FlyoutWindow.xaml`:

```xml
<Window x:Class="OnDeck.App.Windows.FlyoutWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="300"
        SizeToContent="Height"
        MaxHeight="640"
        WindowStyle="None"
        ResizeMode="NoResize"
        ShowInTaskbar="False"
        Topmost="True"
        Background="Transparent">
    <Border x:Name="Root" Padding="12" CornerRadius="8">
        <StackPanel>
            <TextBlock Text="onDeck" FontWeight="SemiBold" FontSize="14" Margin="0,0,0,8" />
            <TextBlock x:Name="SummaryText" TextWrapping="Wrap" />
        </StackPanel>
    </Border>
</Window>
```

Create `src/OnDeck.App/Windows/FlyoutWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OnDeck.Core;
using OnDeck.App.System;

namespace OnDeck.App.Windows;

/// <summary>
/// The tray flyout. Phase 7 replaces the placeholder content with the real sections; what
/// matters here is that it lands in the right place, dismisses on focus loss, and gets its
/// backdrop from DWM with a solid fallback.
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly AppOrchestrator _orchestrator;

    public FlyoutWindow(AppOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        InitializeComponent();

        Deactivated += (_, _) => Hide();        // light dismiss
        _orchestrator.StateChanged += RenderSummary;
        Closed += (_, _) => _orchestrator.StateChanged -= RenderSummary;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        DwmBackdrop.RoundCorners(handle);

        if (!DwmBackdrop.TryApplyAcrylic(handle))
        {
            // Older Win11 builds refuse the backdrop attribute - paint something opaque so the
            // flyout is never an unreadable transparent rectangle.
            Root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        }
    }

    public void ShowAt(Rect trayIcon)
    {
        RenderSummary();

        // Measure before placing: SizeToContent means Height is only real after a layout pass.
        Show();
        UpdateLayout();

        var workArea = SystemParameters.WorkArea;
        var placement = FlyoutPositioner.Place(
            trayIcon, workArea, new Size(Width, ActualHeight));

        Left = placement.X;
        Top = placement.Y;

        Activate();
    }

    private void RenderSummary()
    {
        SummaryText.Text =
            $"Active {_orchestrator.ActivePlayers.Count}   "
            + $"In game {_orchestrator.InGamePlayers.Count}   "
            + $"Upcoming {_orchestrator.UpcomingPlayers.Count}   "
            + $"Done {_orchestrator.DonePlayers.Count}"
            + (_orchestrator.SyncError is { } error ? $"\n{error}" : "");
    }
}
```

Note: `SystemParameters.WorkArea` is the primary monitor's. Multi-monitor correctness is a
manual-verification item in Task 7; if the flyout lands on the wrong screen there, swap it for
the work area of the monitor containing `trayIcon` via `Screen.FromRectangle`.

- [ ] **Step 3: Write the tray icon service**

Create `src/OnDeck.App/Tray/TrayIconService.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using OnDeck.Core;

namespace OnDeck.App.Tray;

/// <summary>
/// The tray presence: icon that greens up when a player is active, tooltip carrying the same
/// text the Mac menu bar title would, and a right-click menu. Float and Settings arrive with
/// the windows they open, in Phases 7 and 8.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly AppOrchestrator _orchestrator;
    private readonly ThemeWatcher _theme = new();
    private readonly TaskbarIcon _icon;
    private TrayIcon? _current;

    public TrayIconService(AppOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;

        _icon = new TaskbarIcon { Visibility = Visibility.Visible };
        _icon.TrayLeftMouseUp += (_, _) => OpenRequested?.Invoke();
        _icon.ContextMenu = BuildMenu();

        _theme.Changed += Refresh;
        _orchestrator.StateChanged += Refresh;

        Refresh();
    }

    public event Action? OpenRequested;

    public event Action? RefreshRequested;

    public event Action? QuitRequested;

    /// <summary>Handle and id the shell knows the icon by — needed by <c>Shell_NotifyIconGetRect</c>.</summary>
    public IntPtr WindowHandle => _icon.Handle;

    public uint IconId => _icon.Id;

    public void Refresh()
    {
        var wanted = TrayIconVariant.Select(_theme.SystemUsesLightTheme, _orchestrator.HasActivePlayers);
        if (_current != wanted)
        {
            _current = wanted;
            _icon.IconSource = new BitmapImage(new Uri(TrayIconVariant.ResourcePath(wanted)));
        }

        var title = _orchestrator.MenuBarTitleText;
        _icon.ToolTipText = title.Length == 0 ? "onDeck" : title;
    }

    private ContextMenu BuildMenu()
    {
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => OpenRequested?.Invoke();

        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => RefreshRequested?.Invoke();

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    public void Dispose()
    {
        _orchestrator.StateChanged -= Refresh;
        _theme.Changed -= Refresh;
        _theme.Dispose();
        _icon.Dispose();
    }
}
```

`TaskbarIcon.Handle` and `.Id` are exposed by Hardcodet 2.x. If the property names differ on the
installed version, read the package's public surface and adapt — do not guess.

- [ ] **Step 4: Build**

`dotnet build windows/OnDeck.slnx` → succeeds.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App
git commit -m "phase 6: tray icon service and flyout window shell"
```

---

## Task 6: Composition root

**Files:**
- Modify: `src/OnDeck.App/App.xaml`, `src/OnDeck.App/App.xaml.cs`
- Create: `src/OnDeck.App/Notifications/LoggingNotificationSink.cs`

**Interfaces:** consumes everything above; produces a running app.

- [ ] **Step 1: The stand-in sink**

Create `src/OnDeck.App/Notifications/LoggingNotificationSink.cs`:

```csharp
using System.Diagnostics;
using OnDeck.Core;

namespace OnDeck.App.Notifications;

/// <summary>
/// Stands in for Phase 9's <c>ToastService</c> so the engine can run end to end now. Every call
/// is logged; nothing is shown.
/// </summary>
public sealed class LoggingNotificationSink : INotificationSink
{
    public Task NotifyBattingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        Log($"BATTING {playerName} - {game}, {inning} ({streamUrl})");

    public Task NotifyPitchingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl) =>
        Log($"PITCHING {playerName} - {game}, {inning} ({streamUrl})");

    public Task NotifyAtBatResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        Log($"AT-BAT RESULT {playerName} - {description}");

    public Task NotifyPitchingResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl) =>
        Log($"PITCHING RESULT {playerName} - {description}");

    public Task NotifyNotInLineupAsync(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl) =>
        Log($"NOT IN LINEUP {playerName} - {game}");

    public void PurgeBatting(int gamePk, int playerId) =>
        Debug.WriteLine($"[Notifications] purge batting {playerId} in {gamePk}");

    public void PurgePitching(int gamePk, int playerId) =>
        Debug.WriteLine($"[Notifications] purge pitching {playerId} in {gamePk}");

    public Task PurgeNotInLineupAsync(int gamePk) => Log($"purge not-in-lineup for {gamePk}");

    public Task PurgeAllAsync() => Log("purge all");

    private static Task Log(string message)
    {
        Debug.WriteLine($"[Notifications] {message}");
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Rewrite the application entry point**

Replace `src/OnDeck.App/App.xaml`:

```xml
<Application x:Class="OnDeck.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown"
             ThemeMode="System">
    <Application.Resources />
</Application>
```

Replace `src/OnDeck.App/App.xaml.cs`:

```csharp
using System.Net.Http;
using System.Windows;
using OnDeck.App.Notifications;
using OnDeck.App.System;
using OnDeck.App.Tray;
using OnDeck.App.Windows;
using OnDeck.Core;
using OnDeck.Core.Managers;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private TrayIconService? _tray;
    private FlyoutWindow? _flyout;
    private AppOrchestrator? _orchestrator;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SingleInstance.TryAcquire(out var instance))
        {
            // Already running: hand the click to the live instance instead of a second tray icon.
            SingleInstance.SignalExistingInstance();
            Shutdown();
            return;
        }

        _singleInstance = instance;
        _singleInstance!.SecondInstanceStarted += () => Dispatcher.Invoke(OpenFlyout);

        base.OnStartup(e);

        // Everything below runs on the Dispatcher thread, which is the point: AppOrchestrator
        // captures this SynchronizationContext and posts its coalesced rebuilds back to it.
        var settings = new SettingsStore();
        var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 4 });
        var mlb = new MlbStatsApi(http);
        var fantrax = new FantraxApi(http);
        var headshots = new HeadshotCache(http, HeadshotCache.DefaultCacheDirectory());
        var states = new StateManager();

        _orchestrator = new AppOrchestrator(
            new RosterManager(fantrax, mlb, settings, headshots),
            new ScheduleManager(mlb),
            new GameMonitor(mlb),
            states,
            fantrax,
            settings,
            new LoggingNotificationSink());

        _tray = new TrayIconService(_orchestrator);
        _tray.OpenRequested += OpenFlyout;
        _tray.RefreshRequested += () => _ = _orchestrator.ResyncRosterAsync();
        _tray.QuitRequested += Shutdown;

        _flyout = new FlyoutWindow(_orchestrator);

        _ = _orchestrator.StartAsync();
    }

    private void OpenFlyout()
    {
        if (_flyout is null || _tray is null) return;

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }

        var rectangle = TrayGeometry.IconRectangle(_tray.WindowHandle, _tray.IconId)
                        ?? new Rect(SystemParameters.WorkArea.Right - 24,
                                    SystemParameters.WorkArea.Bottom, 24, 24);

        _flyout.ShowAt(rectangle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 3: Build and publish**

```bash
dotnet build windows/OnDeck.slnx
dotnet publish windows/src/OnDeck.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

- [ ] **Step 4: Run it**

Launch the debug build, confirm a tray icon appears, left-click opens the flyout showing four
zero counts (no roster configured yet), clicking elsewhere dismisses it, right-click shows
Open/Refresh/Quit, and Quit exits cleanly leaving no process behind.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.App
git commit -m "phase 6: composition root wiring the shell to Core"
```

---

## Task 7: Manual Windows 11 verification

Needs a human. Run `windows/src/OnDeck.App/bin/Debug/net10.0-windows/OnDeck.App.exe`.

**Verified 2026-08-08** on Windows 11 Home 10.0.26200, single monitor, bottom taskbar, dark mode,
by the repo owner:

- [x] Tray icon appears
- [x] Flyout opens anchored to the tray icon, not the screen corner
- [x] Flyout light-dismisses when clicking away
- [x] Right-click menu shows Open / Refresh / Quit

**Not yet exercised.** None of these are known broken; they simply have not been run, and two of
them cover code paths that have never executed. Carry them into Phase 7, where the flyout gets
its real content and every one of these gets retested anyway:

- [ ] Tray icon crisp at 100%, 125%, 150%, 200% scaling (Settings → System → Display → Scale, then restart the app)
- [ ] **Icon swaps white↔dark when the taskbar theme changes, without restarting** (Settings → Personalisation → Colours → "Choose your default Windows mode"). `ThemeWatcher.OnUserPreferenceChanged` has never fired
- [ ] Acrylic backdrop vs the solid fallback — which one fired on this build is unknown. A flat `#202020` rectangle means `DwmSetWindowAttribute` refused; note the Windows build if so
- [ ] Taskbar docked top / left / right still anchors the flyout beside the icon
- [ ] Second monitor: flyout opens on the monitor holding the tray. **Expected to be wrong** — `ShowAt` uses `SystemParameters.WorkArea`, which is always the *primary* monitor's. Fix by taking the work area of the monitor containing the anchor point
- [ ] Launching the exe twice adds no second tray icon and opens the first instance's flyout
- [ ] Quit from the context menu removes the icon and leaves no `OnDeck.App` process

## Done criteria

- [ ] `dotnet test windows/OnDeck.slnx` → `Failed: 0`
- [ ] `grep -c PackageReference windows/src/OnDeck.Core/OnDeck.Core.csproj` → `0`
- [ ] Single-file publish green
- [ ] Every box in Task 7 ticked or its failure recorded below
- [ ] `git status --short` → clean
- [ ] Deviations recorded here and in `HANDOFF.md` §8; `HANDOFF.md` §9 rewritten for Phase 7

## Deviations from the plan

Fill in during execution. Known going in:

| Deviation | Why |
|---|---|
| Native .NET 10 Fluent + DWM interop replaces WPF-UI | The framework covers it; one less dependency (user-confirmed 2026-08-08) |
| `SettingsStore` lands in Phase 6, not Phase 8 | The composition root cannot build `AppOrchestrator` without an `ISettingsStore` |
| Context menu ships Open/Refresh/Quit; Float and Settings deferred | Their windows don't exist until Phases 7 and 8 |
| Tray icons omit Tabler's six stitch strokes **at every size**, not just below 24 px | The 128 px preview showed the stitches reading as a cluttered diagonal smear even large. The tray never renders above 32 px, so circle + seams is the better icon everywhere we ship it. MIT licence permits the edit |
| Flyout content is a placeholder summary | The real sections are Phase 7; this proves binding and placement only |
| **`OnDeck.App.System` renamed to `OnDeck.App.Platform`** | Fatal, not cosmetic: a namespace named `System` nested under `OnDeck.App` shadows the global `System` inside WPF's generated `App.g.cs`, so `System.STAThreadAttribute` fails to resolve and the project will not compile. `PORT_PLAN.md`'s `App/System/` layout cannot be used as written |
| Flyout anchors on the **cursor**, not `Shell_NotifyIconGetRect` | That call needs the window handle and icon id, which Hardcodet keeps in private fields (`messageSink`, `iconData`). Reflecting into a library's internals is a worse defect than a few pixels. The cursor is over the icon whenever it is clicked. Device pixels are converted to DIPs explicitly, so non-100% scaling stays correct |
| No `MainWindow` | The app is tray-only: `ShutdownMode=OnExplicitShutdown` with no `StartupUri`. The template window was deleted |
| Multi-monitor flyout placement is known-suspect | `ShowAt` reads `SystemParameters.WorkArea` (primary monitor only). Correct on a single display; needs the per-monitor work area before the multi-monitor box in Task 7 can be ticked |
