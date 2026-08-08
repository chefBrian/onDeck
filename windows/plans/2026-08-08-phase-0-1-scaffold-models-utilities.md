# Phase 0–1: Scaffold, Models & Small Utilities — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `windows/` .NET solution (Core library + WPF shell + xunit tests, single-file publish proven) and port the Swift value types and pure-function utilities into `OnDeck.Core` with full test coverage.

**Architecture:** Two-project split per `windows/PORT_PLAN.md`. `OnDeck.Core` is a `net10.0` class library with zero Windows dependencies — it holds models, utilities, networking, managers, and the orchestrator. `OnDeck.App` is a `net10.0-windows` WPF shell that references Core. `OnDeck.Core.Tests` is an xunit project referencing Core. This plan builds the scaffold and the leaf-most layer (models + pure utilities) that every later phase depends on.

**Tech Stack:** .NET 10 SDK, C# with `Nullable`/`ImplicitUsings` enabled, WPF (shell, template-only in this plan), xunit.

## Global Constraints

Copied verbatim from `windows/PORT_PLAN.md` — every task's requirements implicitly include these.

- `OnDeck.Core` must have **zero** Windows-specific dependencies — it builds and tests on macOS.
- **Core concurrency model: single logical thread, mirroring Swift's `@MainActor`.** No `ConfigureAwait(false)` anywhere in Core. (No async code in this plan, but the rule stands from here on.)
- Single-file publish must stay green throughout:
  `dotnet publish src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`
- No `PublishTrimmed` — WPF does not support trimming.
- `EnableWindowsTargeting=true` in `OnDeck.App` so the shell compile-checks on macOS.
- Mirror Swift names 1:1 where possible (`GameMonitor`, `LiveFeedPatcher`, `RosterManager`…) so the two codebases stay cross-referenceable.
- macOS-only diagnostics (`MemoryStats`, `MemoryPressureRelief`) are **not ported**.
- Architecture target: `win-x64` only.

## Prerequisite

The .NET 10 SDK must be on `PATH` (`dotnet --list-sdks` shows a `10.0.*` entry). The machine has the .NET 10 **runtime** but no SDK. Install it before Task 1; all commands below assume `dotnet` resolves to a host that can see a 10.0.x SDK.

## Naming conventions for this port

Swift `lowerCamelCase` members become C# `PascalCase`; Swift nested types stay nested. Swift `Date` becomes `DateTimeOffset` throughout (matches `AppOrchestrator.LastSyncDate` in the master plan's interface contract). Swift `ID` suffix becomes `Id` (`homeTeamID` → `HomeTeamId`, `gamePk` stays `GamePk`).

## File Structure

| File | Responsibility |
|---|---|
| `windows/Directory.Build.props` | Shared MSBuild properties: `Nullable`, `ImplicitUsings`, `LangVersion`. Does **not** set `TargetFramework` (it differs per project). |
| `windows/.gitignore` | `bin/`, `obj/`, `publish/`, `*.user` |
| `windows/OnDeck.sln` | Solution binding the three projects |
| `windows/src/OnDeck.Core/OnDeck.Core.csproj` | `net10.0` class library, no dependencies |
| `windows/src/OnDeck.App/OnDeck.App.csproj` | `net10.0-windows` WPF app, references Core, `EnableWindowsTargeting=true` |
| `windows/tests/OnDeck.Core.Tests/OnDeck.Core.Tests.csproj` | `net10.0` xunit project, references Core |
| `windows/src/OnDeck.Core/Models/Player.cs` | `Player` record + nested `PlayerPosition` / `RosterStatus` enums |
| `windows/src/OnDeck.Core/Models/PlayerState.cs` | `PlayerState` closed record hierarchy + `GameContext` + `InactiveReason` |
| `windows/src/OnDeck.Core/Models/Game.cs` | `Game` record + nested `Broadcast` / `Side`; `GameLineup` |
| `windows/src/OnDeck.Core/Utilities/TeamMapping.cs` | Fantrax abbreviation ↔ MLB full-name mapping |
| `windows/src/OnDeck.Core/Utilities/NameCleaner.cs` | Fantrax name normalization |
| `windows/src/OnDeck.Core/Utilities/FantraxUrlParser.cs` | League/team ID extraction from a Fantrax URL |
| `windows/src/OnDeck.Core/Utilities/StreamLinkRouter.cs` | Broadcast callSign → streaming platform URL |
| `windows/tests/OnDeck.Core.Tests/Models/*.cs`, `Utilities/*.cs` | One test file per production file |

---

## Task 1: Solution scaffold

**Files:**
- Create: `windows/Directory.Build.props`
- Create: `windows/.gitignore`
- Create: `windows/OnDeck.sln`
- Create: `windows/src/OnDeck.Core/OnDeck.Core.csproj`
- Create: `windows/src/OnDeck.App/OnDeck.App.csproj` (+ WPF template files)
- Create: `windows/tests/OnDeck.Core.Tests/OnDeck.Core.Tests.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: the `OnDeck.Core` assembly with root namespace `OnDeck.Core`; a test project that already references it. Every later task adds files to these projects.

- [ ] **Step 1: Create the projects and solution**

Run from `windows/`:

```bash
dotnet new sln -n OnDeck
dotnet new classlib -n OnDeck.Core -o src/OnDeck.Core -f net10.0
dotnet new wpf -n OnDeck.App -o src/OnDeck.App -f net10.0
dotnet new xunit -n OnDeck.Core.Tests -o tests/OnDeck.Core.Tests -f net10.0
dotnet sln add src/OnDeck.Core src/OnDeck.App tests/OnDeck.Core.Tests
dotnet add tests/OnDeck.Core.Tests reference src/OnDeck.Core
dotnet add src/OnDeck.App reference src/OnDeck.Core
rm src/OnDeck.Core/Class1.cs
```

The `wpf` template expands `-f net10.0` to `net10.0-windows` on its own — leave it.

- [ ] **Step 2: Write `windows/Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Write `windows/.gitignore`**

```gitignore
bin/
obj/
publish/
*.user
```

- [ ] **Step 4: Add `EnableWindowsTargeting` to the App project**

In `windows/src/OnDeck.App/OnDeck.App.csproj`, inside the existing first `<PropertyGroup>`, add:

```xml
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
```

- [ ] **Step 5: Build and test**

Run from `windows/`:

```bash
dotnet build
dotnet test
```

Expected: build succeeds with 0 errors; `dotnet test` runs the template's `UnitTest1` and passes. (`UnitTest1.cs` is deleted in Task 2 once real tests exist.)

- [ ] **Step 6: Verify the single-file publish recipe**

```bash
dotnet publish src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Expected: succeeds and produces `src/OnDeck.App/bin/Release/net10.0-windows/win-x64/publish/OnDeck.App.exe`. Confirm the file exists and is >40 MB (self-contained WPF).

- [ ] **Step 7: Commit**

```bash
git add windows/
git commit -m "phase 0: scaffold windows solution (Core, App, tests)"
```

---

## Task 2: Player model

**Files:**
- Create: `windows/src/OnDeck.Core/Models/Player.cs`
- Create: `windows/tests/OnDeck.Core.Tests/Models/PlayerTests.cs`
- Delete: `windows/tests/OnDeck.Core.Tests/UnitTest1.cs`

**Spec:** `onDeck/Models/Player.swift`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `OnDeck.Core.Models.Player` — `sealed record Player(int Id, string Name, string Team, IReadOnlySet<PlayerPosition> Positions, IReadOnlySet<string> FantraxPositions, RosterStatus RosterStatus)`
  - Computed: `bool IsPitcher`, `bool IsHitter`, `bool IsOnBench`, `bool IsUnavailable`, `bool IsStartingPitcherOnly`
  - `enum PlayerPosition { Hitter, Pitcher }` — namespace level, **not** nested in `Player`
  - `enum RosterStatus { Active = 1, Reserve = 2, InjuredReserve = 3, Minors = 9 }` — namespace level; values are the Fantrax `statusId` wire values, do not renumber.

  Swift nests both in `Player`. C# cannot: the record already exposes a `RosterStatus`
  property and CS0102 forbids a nested type of the same name (Swift is fine because
  `rosterStatus` differs from `RosterStatus` by case). The property names win — they are
  what the rest of the port and the parity checklist read — so the enums move out.
  - Structural equality: `Positions`/`FantraxPositions` compared with `SetEquals`, not reference identity.

- [ ] **Step 1: Delete the template test and write the failing test**

Delete `windows/tests/OnDeck.Core.Tests/UnitTest1.cs`, then create `windows/tests/OnDeck.Core.Tests/Models/PlayerTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class PlayerTests
{
    private static Player Make(
        PlayerPosition[] positions,
        string[] fantraxPositions,
        RosterStatus status = RosterStatus.Active) =>
        new(660271, "Shohei Ohtani", "LAD",
            new HashSet<PlayerPosition>(positions),
            new HashSet<string>(fantraxPositions),
            status);

    [Fact]
    public void IsPitcher_TrueWhenPositionsContainPitcher()
    {
        Assert.True(Make([PlayerPosition.Pitcher], ["SP"]).IsPitcher);
        Assert.False(Make([PlayerPosition.Hitter], ["DH"]).IsPitcher);
    }

    [Fact]
    public void IsHitter_TrueWhenPositionsContainHitter()
    {
        Assert.True(Make([PlayerPosition.Hitter], ["DH"]).IsHitter);
        Assert.False(Make([PlayerPosition.Pitcher], ["SP"]).IsHitter);
    }

    [Fact]
    public void TwoWayPlayer_IsBothPitcherAndHitter()
    {
        var ohtani = Make([PlayerPosition.Hitter, PlayerPosition.Pitcher], ["SP", "DH"]);
        Assert.True(ohtani.IsPitcher);
        Assert.True(ohtani.IsHitter);
    }

    [Theory]
    [InlineData(RosterStatus.Active, false, false)]
    [InlineData(RosterStatus.Reserve, true, false)]
    [InlineData(RosterStatus.InjuredReserve, false, true)]
    [InlineData(RosterStatus.Minors, false, true)]
    public void RosterStatus_DrivesBenchAndUnavailable(
        RosterStatus status, bool expectedBench, bool expectedUnavailable)
    {
        var player = Make([PlayerPosition.Hitter], ["OF"], status);
        Assert.Equal(expectedBench, player.IsOnBench);
        Assert.Equal(expectedUnavailable, player.IsUnavailable);
    }

    [Fact]
    public void RosterStatus_WireValuesMatchFantraxStatusIds()
    {
        Assert.Equal(1, (int)RosterStatus.Active);
        Assert.Equal(2, (int)RosterStatus.Reserve);
        Assert.Equal(3, (int)RosterStatus.InjuredReserve);
        Assert.Equal(9, (int)RosterStatus.Minors);
    }

    [Fact]
    public void IsStartingPitcherOnly_TrueForSpWithoutRpAndNotHitter()
    {
        Assert.True(Make([PlayerPosition.Pitcher], ["SP"]).IsStartingPitcherOnly);
    }

    [Fact]
    public void IsStartingPitcherOnly_FalseWhenAlsoReliever()
    {
        Assert.False(Make([PlayerPosition.Pitcher], ["SP", "RP"]).IsStartingPitcherOnly);
    }

    [Fact]
    public void IsStartingPitcherOnly_FalseForTwoWayPlayer()
    {
        Assert.False(Make([PlayerPosition.Hitter, PlayerPosition.Pitcher], ["SP", "DH"])
            .IsStartingPitcherOnly);
    }

    [Fact]
    public void IsStartingPitcherOnly_FalseForRelieverOnly()
    {
        Assert.False(Make([PlayerPosition.Pitcher], ["RP"]).IsStartingPitcherOnly);
    }

    [Fact]
    public void Equality_IsStructuralAcrossDistinctSetInstances()
    {
        var a = Make([PlayerPosition.Hitter], ["OF", "DH"]);
        var b = Make([PlayerPosition.Hitter], ["DH", "OF"]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DistinguishesDifferentFantraxPositions()
    {
        var a = Make([PlayerPosition.Pitcher], ["SP"]);
        var b = Make([PlayerPosition.Pitcher], ["RP"]);
        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~PlayerTests
```

Expected: compile error — `Player` does not exist.

- [ ] **Step 3: Write `windows/src/OnDeck.Core/Models/Player.cs`**

```csharp
namespace OnDeck.Core.Models;

/// <summary>
/// Swift nests these in <c>Player</c>, which C# cannot do: the record already has a
/// <c>RosterStatus</c> property and CS0102 forbids a nested type of the same name
/// (Swift gets away with it because <c>rosterStatus</c> differs from <c>RosterStatus</c>
/// by case). The property names win, since those are what the rest of the port reads.
/// </summary>
public enum PlayerPosition
{
    Hitter,
    Pitcher,
}

/// <summary>Values are Fantrax <c>statusId</c> wire values — do not renumber.</summary>
public enum RosterStatus
{
    Active = 1,
    Reserve = 2,
    InjuredReserve = 3,
    Minors = 9,
}

/// <summary>Port of <c>Models/Player.swift</c>.</summary>
public sealed record Player(
    int Id,                                     // MLB player ID
    string Name,
    string Team,
    IReadOnlySet<PlayerPosition> Positions,
    IReadOnlySet<string> FantraxPositions,      // original Fantrax codes, e.g. "SP", "RP", "C"
    RosterStatus RosterStatus)
{
    public bool IsPitcher => Positions.Contains(PlayerPosition.Pitcher);
    public bool IsHitter => Positions.Contains(PlayerPosition.Hitter);
    public bool IsOnBench => RosterStatus == RosterStatus.Reserve;
    public bool IsUnavailable => RosterStatus is RosterStatus.InjuredReserve or RosterStatus.Minors;
    public bool IsStartingPitcherOnly =>
        FantraxPositions.Contains("SP") && !FantraxPositions.Contains("RP") && !IsHitter;

    // The compiler-generated record equality would compare the two set members by
    // reference. Swift's Set has value semantics, so compare contents instead.
    public bool Equals(Player? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Team == other.Team
        && RosterStatus == other.RosterStatus
        && Positions.SetEquals(other.Positions)
        && FantraxPositions.SetEquals(other.FantraxPositions);

    public override int GetHashCode()
    {
        var positionsHash = 0;
        foreach (var position in Positions) positionsHash ^= position.GetHashCode();

        var fantraxHash = 0;
        foreach (var position in FantraxPositions) fantraxHash ^= position.GetHashCode();

        return HashCode.Combine(Id, Name, Team, RosterStatus, positionsHash, fantraxHash);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~PlayerTests
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Models/Player.cs windows/tests/OnDeck.Core.Tests/
git commit -m "phase 1: port Player model"
```

---

## Task 3: PlayerState model

**Files:**
- Create: `windows/src/OnDeck.Core/Models/PlayerState.cs`
- Create: `windows/tests/OnDeck.Core.Tests/Models/PlayerStateTests.cs`

**Spec:** `onDeck/Models/PlayerState.swift`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `abstract record PlayerState` with a private constructor (closed hierarchy) and three nested cases:
    `PlayerState.Active(PlayerState.GameContext Context)`, `PlayerState.Upcoming(DateTimeOffset StartTime)`, `PlayerState.Inactive(PlayerState.InactiveReason Reason)`
  - `enum PlayerState.ActiveRole { Batting, Pitching }`
  - `sealed record PlayerState.GameContext(int GamePk, PlayerState.ActiveRole Role, string Inning, string HomeTeam, string AwayTeam, int HomeTeamId, int AwayTeamId, int HomeScore, int AwayScore, int Balls, int Strikes, int Outs, bool RunnerOnFirst, bool RunnerOnSecond, bool RunnerOnThird)`
  - `abstract record PlayerState.InactiveReason` with nested cases `GameOver(int GamePk)`, `DayOff`, `Substituted(int GamePk)`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/OnDeck.Core.Tests/Models/PlayerStateTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class PlayerStateTests
{
    private static PlayerState.GameContext Context(PlayerState.ActiveRole role) =>
        new(GamePk: 776543, Role: role, Inning: "Top 3rd",
            HomeTeam: "Los Angeles Dodgers", AwayTeam: "San Francisco Giants",
            HomeTeamId: 119, AwayTeamId: 137,
            HomeScore: 2, AwayScore: 1,
            Balls: 1, Strikes: 2, Outs: 1,
            RunnerOnFirst: true, RunnerOnSecond: false, RunnerOnThird: false);

    [Fact]
    public void Active_CarriesGameContext()
    {
        PlayerState state = new PlayerState.Active(Context(PlayerState.ActiveRole.Batting));

        var active = Assert.IsType<PlayerState.Active>(state);
        Assert.Equal(776543, active.Context.GamePk);
        Assert.Equal(PlayerState.ActiveRole.Batting, active.Context.Role);
        Assert.Equal("Top 3rd", active.Context.Inning);
        Assert.True(active.Context.RunnerOnFirst);
    }

    [Fact]
    public void Upcoming_CarriesStartTime()
    {
        var start = new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero);
        PlayerState state = new PlayerState.Upcoming(start);

        Assert.Equal(start, Assert.IsType<PlayerState.Upcoming>(state).StartTime);
    }

    [Fact]
    public void Inactive_GameOver_CarriesGamePk()
    {
        PlayerState state = new PlayerState.Inactive(new PlayerState.InactiveReason.GameOver(776543));

        var reason = Assert.IsType<PlayerState.Inactive>(state).Reason;
        Assert.Equal(776543, Assert.IsType<PlayerState.InactiveReason.GameOver>(reason).GamePk);
    }

    [Fact]
    public void Inactive_Substituted_CarriesGamePk()
    {
        PlayerState state = new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(776543));

        var reason = Assert.IsType<PlayerState.Inactive>(state).Reason;
        Assert.Equal(776543, Assert.IsType<PlayerState.InactiveReason.Substituted>(reason).GamePk);
    }

    [Fact]
    public void Inactive_DayOff_HasNoPayloadAndComparesEqual()
    {
        PlayerState.InactiveReason a = new PlayerState.InactiveReason.DayOff();
        PlayerState.InactiveReason b = new PlayerState.InactiveReason.DayOff();

        Assert.Equal(a, b);
    }

    [Fact]
    public void Cases_AreDistinguishableByPatternMatch()
    {
        PlayerState[] states =
        [
            new PlayerState.Active(Context(PlayerState.ActiveRole.Pitching)),
            new PlayerState.Upcoming(DateTimeOffset.UnixEpoch),
            new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff()),
        ];

        var labels = states.Select(s => s switch
        {
            PlayerState.Active => "active",
            PlayerState.Upcoming => "upcoming",
            PlayerState.Inactive => "inactive",
            _ => "unknown",
        });

        Assert.Equal(["active", "upcoming", "inactive"], labels);
    }

    [Fact]
    public void GameContext_HasValueEquality()
    {
        Assert.Equal(Context(PlayerState.ActiveRole.Batting), Context(PlayerState.ActiveRole.Batting));
        Assert.NotEqual(Context(PlayerState.ActiveRole.Batting), Context(PlayerState.ActiveRole.Pitching));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~PlayerStateTests
```

Expected: compile error — `PlayerState` does not exist.

- [ ] **Step 3: Write `windows/src/OnDeck.Core/Models/PlayerState.cs`**

```csharp
namespace OnDeck.Core.Models;

/// <summary>
/// Port of <c>Models/PlayerState.swift</c>. Swift's enum-with-associated-values becomes a
/// closed record hierarchy: the private constructor keeps the case list to the nested types.
/// </summary>
public abstract record PlayerState
{
    private PlayerState() { }

    public sealed record Active(GameContext Context) : PlayerState;

    public sealed record Upcoming(DateTimeOffset StartTime) : PlayerState;

    public sealed record Inactive(InactiveReason Reason) : PlayerState;

    public enum ActiveRole
    {
        Batting,
        Pitching,
    }

    public sealed record GameContext(
        int GamePk,
        ActiveRole Role,
        string Inning,
        string HomeTeam,
        string AwayTeam,
        int HomeTeamId,
        int AwayTeamId,
        int HomeScore,
        int AwayScore,
        int Balls,
        int Strikes,
        int Outs,
        bool RunnerOnFirst,
        bool RunnerOnSecond,
        bool RunnerOnThird);

    public abstract record InactiveReason
    {
        private InactiveReason() { }

        public sealed record GameOver(int GamePk) : InactiveReason;

        public sealed record DayOff : InactiveReason;

        public sealed record Substituted(int GamePk) : InactiveReason;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~PlayerStateTests
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Models/PlayerState.cs windows/tests/OnDeck.Core.Tests/Models/PlayerStateTests.cs
git commit -m "phase 1: port PlayerState model"
```

---

## Task 4: Game and GameLineup models

**Files:**
- Create: `windows/src/OnDeck.Core/Models/Game.cs`
- Create: `windows/tests/OnDeck.Core.Tests/Models/GameTests.cs`
- Create: `windows/tests/OnDeck.Core.Tests/Models/GameLineupTests.cs`

**Spec:** `onDeck/Models/Game.swift`

**Interfaces:**
- Consumes: `Player` (Task 2).
- Produces:
  - `sealed record Game(int Id, string HomeTeam, string AwayTeam, int HomeTeamId, int AwayTeamId, DateTimeOffset StartTime, int? HomeProbablePitcherId, int? AwayProbablePitcherId, IReadOnlyList<Game.Broadcast> Broadcasts, IReadOnlyList<int> HomeLineup, IReadOnlyList<int> AwayLineup)`
  - `Game.Side? SideFor(Player player)`
  - `enum Game.Side { Home, Away }`
  - `sealed record Game.Broadcast(string CallSign, bool IsExclusive)`
  - `sealed class GameLineup` with mutable `HashSet<int> Home / Away / HomePitchers / AwayPitchers`, plus `bool Excludes(Player player, Game.Side side)`, `IReadOnlySet<int> Ids(Game.Side side)`, `bool IsSubmitted(Game.Side side)`, and value equality.

**Behaviour notes from the Swift spec (preserve exactly):**
- `SideFor` uses **two-way substring containment** (`HomeTeam.Contains(player.Team) || player.Team.Contains(HomeTeam)`) so a Fantrax abbreviation matches an MLB full name, and returns `null` when neither side matches. Home is tested first.
- `GameLineup.Excludes` is **hitters only** — relievers are never on the batting card, and SP-only players are handled by the probable-pitcher day-off pass.
- `IsSubmitted` reflects the **batting card only** (`Home`/`Away`), never the pitcher sets.
- `Ids` returns the **union** of batters and pitchers for that side.

- [ ] **Step 1: Write the failing `Game` test**

Create `windows/tests/OnDeck.Core.Tests/Models/GameTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class GameTests
{
    private static Game Make(string homeTeam = "Los Angeles Dodgers", string awayTeam = "San Francisco Giants") =>
        new(776543, homeTeam, awayTeam, 119, 137,
            new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero),
            HomeProbablePitcherId: 605483, AwayProbablePitcherId: null,
            Broadcasts: [new Game.Broadcast("Peacock", IsExclusive: true)],
            HomeLineup: [660271, 605141],
            AwayLineup: []);

    private static Player PlayerOn(string team) =>
        new(660271, "Shohei Ohtani", team,
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "DH" },
            RosterStatus.Active);

    [Fact]
    public void SideFor_MatchesHomeWhenFullNameContainsAbbreviation()
    {
        Assert.Equal(Game.Side.Home, Make().SideFor(PlayerOn("Dodgers")));
    }

    [Fact]
    public void SideFor_MatchesAwayWhenFullNameContainsAbbreviation()
    {
        Assert.Equal(Game.Side.Away, Make().SideFor(PlayerOn("Giants")));
    }

    [Fact]
    public void SideFor_MatchesWhenPlayerTeamContainsGameTeam()
    {
        // "Athletics" (MLB short name) is contained in the player's longer team string.
        var game = Make(homeTeam: "Athletics", awayTeam: "Seattle Mariners");
        Assert.Equal(Game.Side.Home, game.SideFor(PlayerOn("Sacramento Athletics")));
    }

    [Fact]
    public void SideFor_ReturnsNullWhenNeitherSideMatches()
    {
        Assert.Null(Make().SideFor(PlayerOn("Red Sox")));
    }

    [Fact]
    public void SideFor_PrefersHomeWhenBothWouldMatch()
    {
        var game = Make(homeTeam: "New York Yankees", awayTeam: "New York Mets");
        Assert.Equal(Game.Side.Home, game.SideFor(PlayerOn("New York")));
    }

    [Fact]
    public void Equality_IsStructuralOverCollectionMembers()
    {
        Assert.Equal(Make(), Make());
        Assert.Equal(Make().GetHashCode(), Make().GetHashCode());
    }

    [Fact]
    public void Equality_DistinguishesDifferentLineups()
    {
        var a = Make();
        var b = a with { HomeLineup = [660271] };
        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 2: Write the failing `GameLineup` test**

Create `windows/tests/OnDeck.Core.Tests/Models/GameLineupTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class GameLineupTests
{
    private const int OhtaniId = 660271;
    private const int BettsId = 605141;
    private const int SnellId = 605483;

    private static Player Hitter(int id) =>
        new(id, "Hitter", "LAD",
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static Player Reliever(int id) =>
        new(id, "Reliever", "LAD",
            new HashSet<PlayerPosition> { PlayerPosition.Pitcher },
            new HashSet<string> { "RP" },
            RosterStatus.Active);

    [Fact]
    public void IsSubmitted_FalseWhenBattingCardEmpty()
    {
        var lineup = new GameLineup { HomePitchers = [SnellId] };
        Assert.False(lineup.IsSubmitted(Game.Side.Home));
    }

    [Fact]
    public void IsSubmitted_TrueOnlyForTheSideThatFiled()
    {
        var lineup = new GameLineup { Home = [OhtaniId, BettsId] };
        Assert.True(lineup.IsSubmitted(Game.Side.Home));
        Assert.False(lineup.IsSubmitted(Game.Side.Away));
    }

    [Fact]
    public void Ids_ReturnsUnionOfBattersAndPitchers()
    {
        var lineup = new GameLineup { Home = [OhtaniId], HomePitchers = [SnellId] };

        var ids = lineup.Ids(Game.Side.Home);
        Assert.Equal(2, ids.Count);
        Assert.Contains(OhtaniId, ids);
        Assert.Contains(SnellId, ids);
        Assert.Empty(lineup.Ids(Game.Side.Away));
    }

    [Fact]
    public void Excludes_TrueForHitterMissingFromFiledCard()
    {
        var lineup = new GameLineup { Home = [BettsId] };
        Assert.True(lineup.Excludes(Hitter(OhtaniId), Game.Side.Home));
    }

    [Fact]
    public void Excludes_FalseForHitterPresentOnCard()
    {
        var lineup = new GameLineup { Home = [OhtaniId, BettsId] };
        Assert.False(lineup.Excludes(Hitter(OhtaniId), Game.Side.Home));
    }

    [Fact]
    public void Excludes_FalseBeforeCardIsFiled()
    {
        Assert.False(new GameLineup().Excludes(Hitter(OhtaniId), Game.Side.Home));
    }

    [Fact]
    public void Excludes_FalseForPitchers_RelieversAreNeverOnTheCard()
    {
        var lineup = new GameLineup { Home = [OhtaniId, BettsId] };
        Assert.False(lineup.Excludes(Reliever(SnellId), Game.Side.Home));
    }

    [Fact]
    public void Equality_ComparesSetContents()
    {
        var a = new GameLineup { Home = [OhtaniId, BettsId], AwayPitchers = [SnellId] };
        var b = new GameLineup { Home = [BettsId, OhtaniId], AwayPitchers = [SnellId] };
        var c = new GameLineup { Home = [OhtaniId], AwayPitchers = [SnellId] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~GameTests|FullyQualifiedName~GameLineupTests"
```

Expected: compile error — `Game` / `GameLineup` do not exist.

- [ ] **Step 4: Write `windows/src/OnDeck.Core/Models/Game.cs`**

```csharp
namespace OnDeck.Core.Models;

/// <summary>Port of <c>Models/Game.swift</c>.</summary>
public sealed record Game(
    int Id,                                     // gamePk
    string HomeTeam,
    string AwayTeam,
    int HomeTeamId,
    int AwayTeamId,
    DateTimeOffset StartTime,
    int? HomeProbablePitcherId,
    int? AwayProbablePitcherId,
    IReadOnlyList<Game.Broadcast> Broadcasts,
    IReadOnlyList<int> HomeLineup,              // batting order IDs from schedule (empty until filed)
    IReadOnlyList<int> AwayLineup)
{
    public enum Side
    {
        Home,
        Away,
    }

    public sealed record Broadcast(string CallSign, bool IsExclusive);

    /// <summary>
    /// Two-way containment so a Fantrax abbreviation ("Dodgers") matches an MLB full name
    /// ("Los Angeles Dodgers") and a short MLB name ("Athletics") matches a longer player
    /// team string. Home is tested first, mirroring the Swift order.
    /// </summary>
    public Side? SideFor(Player player)
    {
        if (HomeTeam.Contains(player.Team, StringComparison.Ordinal)
            || player.Team.Contains(HomeTeam, StringComparison.Ordinal))
        {
            return Side.Home;
        }

        if (AwayTeam.Contains(player.Team, StringComparison.Ordinal)
            || player.Team.Contains(AwayTeam, StringComparison.Ordinal))
        {
            return Side.Away;
        }

        return null;
    }

    // Record equality would compare the three list members by reference; Swift's arrays
    // have value semantics, so compare element-wise.
    public bool Equals(Game? other) =>
        other is not null
        && Id == other.Id
        && HomeTeam == other.HomeTeam
        && AwayTeam == other.AwayTeam
        && HomeTeamId == other.HomeTeamId
        && AwayTeamId == other.AwayTeamId
        && StartTime == other.StartTime
        && HomeProbablePitcherId == other.HomeProbablePitcherId
        && AwayProbablePitcherId == other.AwayProbablePitcherId
        && Broadcasts.SequenceEqual(other.Broadcasts)
        && HomeLineup.SequenceEqual(other.HomeLineup)
        && AwayLineup.SequenceEqual(other.AwayLineup);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(HomeTeam);
        hash.Add(AwayTeam);
        hash.Add(HomeTeamId);
        hash.Add(AwayTeamId);
        hash.Add(StartTime);
        hash.Add(HomeProbablePitcherId);
        hash.Add(AwayProbablePitcherId);
        foreach (var broadcast in Broadcasts) hash.Add(broadcast);
        foreach (var id in HomeLineup) hash.Add(id);
        foreach (var id in AwayLineup) hash.Add(id);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Lineup IDs tracked per side so consumers can tell whether a player's own team has
/// submitted yet (vs just the opponent). Batters and pitchers are tracked separately:
/// <see cref="IsSubmitted"/> reflects the batting card only (pitchers come from a
/// different source and shouldn't gate hitter checks), while <see cref="Ids"/> returns
/// the union so membership tests cover both roles.
/// </summary>
public sealed class GameLineup : IEquatable<GameLineup>
{
    public HashSet<int> Home { get; set; } = [];
    public HashSet<int> Away { get; set; } = [];
    public HashSet<int> HomePitchers { get; set; } = [];
    public HashSet<int> AwayPitchers { get; set; } = [];

    /// <summary>
    /// True when this side's batting card was filed without the player. Hitters only:
    /// relievers are never on the card so its contents say nothing about them, and
    /// SP-only players are handled separately by the probable-pitcher day-off pass.
    /// </summary>
    public bool Excludes(Player player, Game.Side side) =>
        player.IsHitter && IsSubmitted(side) && !Ids(side).Contains(player.Id);

    public IReadOnlySet<int> Ids(Game.Side side)
    {
        var batters = side == Game.Side.Home ? Home : Away;
        var pitchers = side == Game.Side.Home ? HomePitchers : AwayPitchers;
        var union = new HashSet<int>(batters);
        union.UnionWith(pitchers);
        return union;
    }

    public bool IsSubmitted(Game.Side side) =>
        (side == Game.Side.Home ? Home : Away).Count > 0;

    public bool Equals(GameLineup? other) =>
        other is not null
        && Home.SetEquals(other.Home)
        && Away.SetEquals(other.Away)
        && HomePitchers.SetEquals(other.HomePitchers)
        && AwayPitchers.SetEquals(other.AwayPitchers);

    public override bool Equals(object? obj) => Equals(obj as GameLineup);

    public override int GetHashCode()
    {
        static int SetHash(HashSet<int> set)
        {
            var hash = 0;
            foreach (var value in set) hash ^= value;
            return hash;
        }

        return HashCode.Combine(SetHash(Home), SetHash(Away), SetHash(HomePitchers), SetHash(AwayPitchers));
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~GameTests|FullyQualifiedName~GameLineupTests"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add windows/src/OnDeck.Core/Models/Game.cs windows/tests/OnDeck.Core.Tests/Models/
git commit -m "phase 1: port Game and GameLineup models"
```

---

## Task 5: TeamMapping

**Files:**
- Create: `windows/src/OnDeck.Core/Utilities/TeamMapping.cs`
- Create: `windows/tests/OnDeck.Core.Tests/Utilities/TeamMappingTests.cs`

**Spec:** `onDeck/Utilities/TeamMapping.swift`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class OnDeck.Core.Utilities.TeamMapping` with
  `IReadOnlyDictionary<string, string> FantraxToMlb`, `string? MlbTeamName(string fantraxAbbreviation)`,
  `string Abbreviation(string mlbTeamName)`, `bool Matches(string mlbTeamName, string fantraxAbbreviation)`.

**Deliberate improvement over the Swift original:** Swift builds the reverse map by iterating a `Dictionary`, whose order is randomized per process — so `abbreviation(for: "Athletics")` could return `ATH` **or** `OAK` between runs, and the partial-match fallback loop is likewise order-dependent. The C# port drives both from an ordered pair list, making `ATH` (the current abbreviation, listed first) win deterministically. `OAK` remains a valid input to `MlbTeamName`/`Matches`.

- [ ] **Step 1: Write the failing test**

Create `windows/tests/OnDeck.Core.Tests/Utilities/TeamMappingTests.cs`:

```csharp
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class TeamMappingTests
{
    [Theory]
    [InlineData("ATH", "Athletics")]
    [InlineData("OAK", "Athletics")]      // legacy abbreviation, same club
    [InlineData("LAD", "Los Angeles Dodgers")]
    [InlineData("KC", "Kansas City Royals")]
    [InlineData("STL", "St. Louis Cardinals")]
    [InlineData("WAS", "Washington Nationals")]
    public void MlbTeamName_MapsFantraxAbbreviations(string abbreviation, string expected)
    {
        Assert.Equal(expected, TeamMapping.MlbTeamName(abbreviation));
    }

    [Fact]
    public void MlbTeamName_IsCaseInsensitiveOnInput()
    {
        Assert.Equal("Athletics", TeamMapping.MlbTeamName("ath"));
        Assert.Equal("New York Mets", TeamMapping.MlbTeamName("nym"));
    }

    [Fact]
    public void MlbTeamName_ReturnsNullForUnknownAbbreviation()
    {
        Assert.Null(TeamMapping.MlbTeamName("XYZ"));
    }

    [Fact]
    public void FantraxToMlb_CoversAllThirtyClubsPlusLegacyOak()
    {
        Assert.Equal(31, TeamMapping.FantraxToMlb.Count);
        Assert.Equal(30, TeamMapping.FantraxToMlb.Values.Distinct().Count());
    }

    [Theory]
    [InlineData("Los Angeles Dodgers", "LAD")]
    [InlineData("Kansas City Royals", "KC")]
    [InlineData("Athletics", "ATH")]                  // ATH wins over legacy OAK, deterministically
    [InlineData("Sacramento Athletics", "ATH")]       // partial-match fallback
    public void Abbreviation_ReversesToShortCode(string mlbTeamName, string expected)
    {
        Assert.Equal(expected, TeamMapping.Abbreviation(mlbTeamName));
    }

    [Fact]
    public void Abbreviation_FallsBackToLastWordForUnknownTeam()
    {
        Assert.Equal("Bananas", TeamMapping.Abbreviation("Savannah Bananas"));
    }

    [Fact]
    public void Abbreviation_ReturnsInputWhenThereIsNoLastWord()
    {
        Assert.Equal("", TeamMapping.Abbreviation(""));
    }

    [Theory]
    [InlineData("Athletics", "ATH", true)]
    [InlineData("Athletics", "OAK", true)]
    [InlineData("Sacramento Athletics", "ATH", true)]   // partial match on MLB API name
    [InlineData("Los Angeles Dodgers", "LAA", false)]
    [InlineData("Los Angeles Dodgers", "XYZ", false)]   // unknown abbreviation never matches
    public void Matches_ComparesMlbNameAgainstAbbreviation(
        string mlbTeamName, string abbreviation, bool expected)
    {
        Assert.Equal(expected, TeamMapping.Matches(mlbTeamName, abbreviation));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveOnAbbreviation()
    {
        Assert.True(TeamMapping.Matches("Athletics", "ath"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~TeamMappingTests
```

Expected: compile error — `TeamMapping` does not exist.

- [ ] **Step 3: Write `windows/src/OnDeck.Core/Utilities/TeamMapping.cs`**

```csharp
namespace OnDeck.Core.Utilities;

/// <summary>Port of <c>Utilities/TeamMapping.swift</c>.</summary>
public static class TeamMapping
{
    // Single ordered source of truth. Order matters: the reverse lookup keeps the first
    // abbreviation seen for a given MLB name (ties broken by position, shorter wins),
    // which makes "Athletics" resolve to ATH rather than the legacy OAK. The Swift
    // original iterated a Dictionary, so its reverse map was order-random per process.
    private static readonly (string Abbreviation, string MlbName)[] Pairs =
    [
        ("ARI", "Arizona Diamondbacks"),
        ("ATH", "Athletics"),
        ("ATL", "Atlanta Braves"),
        ("BAL", "Baltimore Orioles"),
        ("BOS", "Boston Red Sox"),
        ("CHC", "Chicago Cubs"),
        ("CHW", "Chicago White Sox"),
        ("CIN", "Cincinnati Reds"),
        ("CLE", "Cleveland Guardians"),
        ("COL", "Colorado Rockies"),
        ("DET", "Detroit Tigers"),
        ("HOU", "Houston Astros"),
        ("KC", "Kansas City Royals"),
        ("LAA", "Los Angeles Angels"),
        ("LAD", "Los Angeles Dodgers"),
        ("MIA", "Miami Marlins"),
        ("MIL", "Milwaukee Brewers"),
        ("MIN", "Minnesota Twins"),
        ("NYM", "New York Mets"),
        ("NYY", "New York Yankees"),
        ("OAK", "Athletics"),           // legacy abbreviation
        ("PHI", "Philadelphia Phillies"),
        ("PIT", "Pittsburgh Pirates"),
        ("SD", "San Diego Padres"),
        ("SEA", "Seattle Mariners"),
        ("SF", "San Francisco Giants"),
        ("STL", "St. Louis Cardinals"),
        ("TB", "Tampa Bay Rays"),
        ("TEX", "Texas Rangers"),
        ("TOR", "Toronto Blue Jays"),
        ("WAS", "Washington Nationals"),
    ];

    /// <summary>Fantrax team abbreviations to MLB API full team names, for disambiguation.</summary>
    public static IReadOnlyDictionary<string, string> FantraxToMlb { get; } =
        Pairs.ToDictionary(pair => pair.Abbreviation, pair => pair.MlbName, StringComparer.Ordinal);

    /// <summary>Reverse lookup: MLB full name to shortest abbreviation, in declaration order.</summary>
    private static readonly (string MlbName, string Abbreviation)[] MlbToAbbreviation = BuildReverse();

    /// <summary>Returns the MLB full team name for a Fantrax abbreviation.</summary>
    public static string? MlbTeamName(string fantraxAbbreviation) =>
        FantraxToMlb.TryGetValue(fantraxAbbreviation.ToUpperInvariant(), out var name) ? name : null;

    /// <summary>Returns a short abbreviation for an MLB full team name, or the last word as fallback.</summary>
    public static string Abbreviation(string mlbTeamName)
    {
        foreach (var (name, abbreviation) in MlbToAbbreviation)
        {
            if (name == mlbTeamName) return abbreviation;
        }

        // Partial match fallback, e.g. "Sacramento Athletics" -> "Athletics" -> ATH.
        foreach (var (name, abbreviation) in MlbToAbbreviation)
        {
            if (mlbTeamName.Contains(name, StringComparison.Ordinal)) return abbreviation;
        }

        var words = mlbTeamName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[^1] : mlbTeamName;
    }

    /// <summary>
    /// Checks if an MLB API team name matches a Fantrax abbreviation. Handles partial
    /// matches, e.g. "Athletics" matches "Sacramento Athletics".
    /// </summary>
    public static bool Matches(string mlbTeamName, string fantraxAbbreviation) =>
        MlbTeamName(fantraxAbbreviation) is { } expected
        && mlbTeamName.Contains(expected, StringComparison.Ordinal);

    private static (string MlbName, string Abbreviation)[] BuildReverse()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var (abbreviation, name) in Pairs)
        {
            if (map.TryGetValue(name, out var existing))
            {
                // Keep the shorter abbreviation (e.g. "KC" over "KCR"); ties keep the first.
                if (abbreviation.Length < existing.Length) map[name] = abbreviation;
            }
            else
            {
                map[name] = abbreviation;
                order.Add(name);
            }
        }

        return [.. order.Select(name => (name, map[name]))];
    }
}
```

Note: `Matches` drops the Swift `|| mlbTeamName == expected` clause — `Contains` already covers exact equality.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~TeamMappingTests
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/TeamMapping.cs windows/tests/OnDeck.Core.Tests/Utilities/
git commit -m "phase 1: port TeamMapping"
```

---

## Task 6: NameCleaner

**Files:**
- Create: `windows/src/OnDeck.Core/Utilities/NameCleaner.cs`
- Create: `windows/tests/OnDeck.Core.Tests/Utilities/NameCleanerTests.cs`

**Spec:** `onDeck/Utilities/NameCleaner.swift`

**Interfaces:**
- Consumes: nothing.
- Produces: `static partial class OnDeck.Core.Utilities.NameCleaner` with
  `string StripPositionSuffix(string name)`, `string StripPeriods(string name)`, `string Clean(string name)`.

- [ ] **Step 1: Write the failing test**

Create `windows/tests/OnDeck.Core.Tests/Utilities/NameCleanerTests.cs`:

```csharp
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class NameCleanerTests
{
    [Theory]
    [InlineData("Shohei Ohtani-P", "Shohei Ohtani")]
    [InlineData("Shohei Ohtani-H", "Shohei Ohtani")]
    [InlineData("Shohei Ohtani-DH", "Shohei Ohtani")]
    public void StripPositionSuffix_RemovesTrailingPositionCode(string input, string expected)
    {
        Assert.Equal(expected, NameCleaner.StripPositionSuffix(input));
    }

    [Theory]
    [InlineData("Mookie Betts")]
    [InlineData("Jean-Pierre Ramirez")]     // interior hyphen is not a suffix
    [InlineData("Shohei Ohtani-SP")]        // only P/H/DH are stripped
    [InlineData("Shohei Ohtani-P ")]        // anchored at end, trailing space defeats it
    public void StripPositionSuffix_LeavesOtherNamesUntouched(string input)
    {
        Assert.Equal(input, NameCleaner.StripPositionSuffix(input));
    }

    [Fact]
    public void StripPositionSuffix_RemovesOnlyOneSuffix()
    {
        Assert.Equal("Player-P", NameCleaner.StripPositionSuffix("Player-P-H"));
    }

    [Theory]
    [InlineData("T.J. Rumfield", "TJ Rumfield")]
    [InlineData("A.J. Puk", "AJ Puk")]
    [InlineData("Mookie Betts", "Mookie Betts")]
    public void StripPeriods_RemovesAllPeriods(string input, string expected)
    {
        Assert.Equal(expected, NameCleaner.StripPeriods(input));
    }

    [Theory]
    [InlineData("T.J. Rumfield-P", "TJ Rumfield")]
    [InlineData("A.J. Puk-DH", "AJ Puk")]
    [InlineData("Shohei Ohtani", "Shohei Ohtani")]
    public void Clean_StripsSuffixThenPeriods(string input, string expected)
    {
        Assert.Equal(expected, NameCleaner.Clean(input));
    }

    [Fact]
    public void Clean_HandlesEmptyString()
    {
        Assert.Equal("", NameCleaner.Clean(""));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~NameCleanerTests
```

Expected: compile error — `NameCleaner` does not exist.

- [ ] **Step 3: Write `windows/src/OnDeck.Core/Utilities/NameCleaner.cs`**

```csharp
using System.Text.RegularExpressions;

namespace OnDeck.Core.Utilities;

/// <summary>Port of <c>Utilities/NameCleaner.swift</c>.</summary>
public static partial class NameCleaner
{
    [GeneratedRegex(@"-(P|H|DH)$")]
    private static partial Regex PositionSuffixRegex();

    /// <summary>
    /// Strips position suffixes (-P, -H, -DH) from Fantrax player names.
    /// Example: "Shohei Ohtani-P" -> "Shohei Ohtani".
    /// </summary>
    public static string StripPositionSuffix(string name) =>
        PositionSuffixRegex().Replace(name, string.Empty);

    /// <summary>
    /// Strips periods from names for MLB API search compatibility.
    /// Example: "T.J. Rumfield" -> "TJ Rumfield".
    /// </summary>
    public static string StripPeriods(string name) => name.Replace(".", string.Empty);

    /// <summary>Full cleanup pipeline for Fantrax names before MLB API lookup.</summary>
    public static string Clean(string name) => StripPeriods(StripPositionSuffix(name));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~NameCleanerTests
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/NameCleaner.cs windows/tests/OnDeck.Core.Tests/Utilities/NameCleanerTests.cs
git commit -m "phase 1: port NameCleaner"
```

---

## Task 7: FantraxUrlParser

**Files:**
- Create: `windows/src/OnDeck.Core/Utilities/FantraxUrlParser.cs`
- Create: `windows/tests/OnDeck.Core.Tests/Utilities/FantraxUrlParserTests.cs`

**Spec:** `onDeck/Utilities/FantraxURLParser.swift` (C# name drops the all-caps `URL` per .NET conventions; the Swift file name is the spec).

**Interfaces:**
- Consumes: nothing.
- Produces: `static class OnDeck.Core.Utilities.FantraxUrlParser` with
  `sealed record ParsedUrl(string LeagueId, string? TeamId)` and `ParsedUrl? Parse(string urlString)`.

**Extraction order (mirror exactly):**
1. Query parameters `leagueId` / `teamId` (newui format) first.
2. If no league ID: path segment following a `league` segment.
3. If no team ID: matrix parameter `;teamId=` scanned out of the raw input, terminated by `&`, `;`, or `/`.
4. Return `null` when no league ID was found by any route.

- [ ] **Step 1: Write the failing test**

Create `windows/tests/OnDeck.Core.Tests/Utilities/FantraxUrlParserTests.cs`:

```csharp
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class FantraxUrlParserTests
{
    [Fact]
    public void Parse_ReadsQueryParameters()
    {
        var parsed = FantraxUrlParser.Parse(
            "https://www.fantrax.com/fantasy/league/abc123/team/roster?leagueId=qry456&teamId=tm789");

        Assert.NotNull(parsed);
        Assert.Equal("qry456", parsed.LeagueId);   // query wins over the path segment
        Assert.Equal("tm789", parsed.TeamId);
    }

    [Fact]
    public void Parse_FallsBackToPathSegmentForLeagueId()
    {
        var parsed = FantraxUrlParser.Parse("https://www.fantrax.com/fantasy/league/abc123/team/roster");

        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed.LeagueId);
        Assert.Null(parsed.TeamId);
    }

    [Fact]
    public void Parse_ReadsMatrixTeamIdParameter()
    {
        var parsed = FantraxUrlParser.Parse(
            "https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=tm789");

        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed.LeagueId);
        Assert.Equal("tm789", parsed.TeamId);
    }

    [Theory]
    [InlineData("https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=tm789/more", "tm789")]
    [InlineData("https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=tm789;view=stats", "tm789")]
    [InlineData("https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=tm789&x=1", "tm789")]
    public void Parse_TerminatesMatrixTeamIdAtDelimiter(string url, string expectedTeamId)
    {
        Assert.Equal(expectedTeamId, FantraxUrlParser.Parse(url)?.TeamId);
    }

    [Fact]
    public void Parse_ReturnsNullTeamIdWhenMatrixValueIsEmpty()
    {
        var parsed = FantraxUrlParser.Parse(
            "https://www.fantrax.com/fantasy/league/abc123/team/roster;teamId=");

        Assert.NotNull(parsed);
        Assert.Null(parsed.TeamId);
    }

    [Fact]
    public void Parse_ReturnsNullWhenNoLeagueIdAnywhere()
    {
        Assert.Null(FantraxUrlParser.Parse("https://www.fantrax.com/fantasy/team/roster?teamId=tm789"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("league/abc123")]      // relative, not absolute
    public void Parse_ReturnsNullForUnusableInput(string input)
    {
        Assert.Null(FantraxUrlParser.Parse(input));
    }

    [Fact]
    public void Parse_HandlesTrailingSlashAfterLeagueSegment()
    {
        Assert.Equal("abc123", FantraxUrlParser.Parse("https://www.fantrax.com/fantasy/league/abc123/")?.LeagueId);
    }

    [Fact]
    public void Parse_ReturnsNullWhenLeagueSegmentIsLast()
    {
        Assert.Null(FantraxUrlParser.Parse("https://www.fantrax.com/fantasy/league"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~FantraxUrlParserTests
```

Expected: compile error — `FantraxUrlParser` does not exist.

- [ ] **Step 3: Write `windows/src/OnDeck.Core/Utilities/FantraxUrlParser.cs`**

```csharp
using System.Web;

namespace OnDeck.Core.Utilities;

/// <summary>Port of <c>Utilities/FantraxURLParser.swift</c>.</summary>
public static class FantraxUrlParser
{
    public sealed record ParsedUrl(string LeagueId, string? TeamId);

    public static ParsedUrl? Parse(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url)) return null;

        // Try query parameters first (newui format).
        var query = HttpUtility.ParseQueryString(url.Query);
        var leagueId = query["leagueId"];
        var teamId = query["teamId"];

        // Try path-based extraction for leagueId: /league/{id}/
        if (string.IsNullOrEmpty(leagueId))
        {
            var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var leagueIndex = Array.IndexOf(segments, "league");
            if (leagueIndex >= 0 && leagueIndex + 1 < segments.Length)
            {
                leagueId = segments[leagueIndex + 1];
            }
        }

        // Try matrix parameters for teamId: ;teamId={id}
        if (string.IsNullOrEmpty(teamId))
        {
            const string marker = ";teamId=";
            var start = urlString.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var rest = urlString[(start + marker.Length)..];
                var end = rest.AsSpan().IndexOfAny('&', ';', '/');
                var value = end >= 0 ? rest[..end] : rest;
                if (value.Length > 0) teamId = value;
            }
        }

        if (string.IsNullOrEmpty(leagueId)) return null;

        return new ParsedUrl(leagueId, string.IsNullOrEmpty(teamId) ? null : teamId);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~FantraxUrlParserTests
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/FantraxUrlParser.cs windows/tests/OnDeck.Core.Tests/Utilities/FantraxUrlParserTests.cs
git commit -m "phase 1: port FantraxUrlParser"
```

---

## Task 8: StreamLinkRouter

**Files:**
- Create: `windows/src/OnDeck.Core/Utilities/StreamLinkRouter.cs`
- Create: `windows/tests/OnDeck.Core.Tests/Utilities/StreamLinkRouterTests.cs`

**Spec:** `onDeck/Utilities/StreamLinkRouter.swift`

**Interfaces:**
- Consumes: `Game`, `Game.Broadcast` (Task 4).
- Produces: `static class OnDeck.Core.Utilities.StreamLinkRouter` with `Uri Url(Game game)`.

**Routing rules (every one gets a test):** only the **first exclusive** broadcast is consulted; non-exclusive broadcasts are ignored entirely. `Peacock` → peacocktv.com/sports/mlb; `Apple TV` / `Apple TV+` → the Apple TV MLB room URL; `ESPN` / `ESPN2` → espn.com/watch/; `Netflix` → netflix.com; `TBS` → tbs.com/mlb-on-tbs; anything else, or no exclusive broadcast → `https://www.mlb.com/tv/g{gamePk}`.

- [ ] **Step 1: Write the failing test**

Create `windows/tests/OnDeck.Core.Tests/Utilities/StreamLinkRouterTests.cs`:

```csharp
using OnDeck.Core.Models;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class StreamLinkRouterTests
{
    private const int GamePk = 776543;

    private static Game GameWith(params Game.Broadcast[] broadcasts) =>
        new(GamePk, "Los Angeles Dodgers", "San Francisco Giants", 119, 137,
            new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero),
            HomeProbablePitcherId: null, AwayProbablePitcherId: null,
            Broadcasts: broadcasts, HomeLineup: [], AwayLineup: []);

    [Theory]
    [InlineData("Peacock", "https://www.peacocktv.com/sports/mlb")]
    [InlineData("Apple TV", "https://tv.apple.com/us/room/edt.item.62327df1-6e37-4222-86c1-056489e15668")]
    [InlineData("Apple TV+", "https://tv.apple.com/us/room/edt.item.62327df1-6e37-4222-86c1-056489e15668")]
    [InlineData("ESPN", "https://www.espn.com/watch/")]
    [InlineData("ESPN2", "https://www.espn.com/watch/")]
    [InlineData("Netflix", "https://www.netflix.com")]
    [InlineData("TBS", "https://www.tbs.com/mlb-on-tbs")]
    public void Url_RoutesExclusiveCallSignToItsPlatform(string callSign, string expected)
    {
        var game = GameWith(new Game.Broadcast(callSign, IsExclusive: true));
        Assert.Equal(expected, StreamLinkRouter.Url(game).ToString());
    }

    [Fact]
    public void Url_FallsBackToMlbTvWhenNoBroadcasts()
    {
        Assert.Equal($"https://www.mlb.com/tv/g{GamePk}", StreamLinkRouter.Url(GameWith()).ToString());
    }

    [Fact]
    public void Url_FallsBackToMlbTvWhenNoExclusiveBroadcast()
    {
        var game = GameWith(
            new Game.Broadcast("SNLA", IsExclusive: false),
            new Game.Broadcast("Peacock", IsExclusive: false));

        Assert.Equal($"https://www.mlb.com/tv/g{GamePk}", StreamLinkRouter.Url(game).ToString());
    }

    [Fact]
    public void Url_FallsBackToMlbTvForUnknownExclusiveCallSign()
    {
        var game = GameWith(new Game.Broadcast("Roku", IsExclusive: true));
        Assert.Equal($"https://www.mlb.com/tv/g{GamePk}", StreamLinkRouter.Url(game).ToString());
    }

    [Fact]
    public void Url_UsesTheFirstExclusiveBroadcast()
    {
        var game = GameWith(
            new Game.Broadcast("SNLA", IsExclusive: false),
            new Game.Broadcast("Netflix", IsExclusive: true),
            new Game.Broadcast("Peacock", IsExclusive: true));

        Assert.Equal("https://www.netflix.com", StreamLinkRouter.Url(game).ToString());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter FullyQualifiedName~StreamLinkRouterTests
```

Expected: compile error — `StreamLinkRouter` does not exist.

- [ ] **Step 3: Write `windows/src/OnDeck.Core/Utilities/StreamLinkRouter.cs`**

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Utilities;

/// <summary>Port of <c>Utilities/StreamLinkRouter.swift</c>.</summary>
public static class StreamLinkRouter
{
    /// <summary>Routes a broadcast callSign to the appropriate streaming platform URL.</summary>
    public static Uri Url(Game game)
    {
        var callSign = game.Broadcasts.FirstOrDefault(broadcast => broadcast.IsExclusive)?.CallSign;

        return callSign switch
        {
            "Peacock" => new Uri("https://www.peacocktv.com/sports/mlb"),
            "Apple TV" or "Apple TV+" =>
                new Uri("https://tv.apple.com/us/room/edt.item.62327df1-6e37-4222-86c1-056489e15668"),
            "ESPN" or "ESPN2" => new Uri("https://www.espn.com/watch/"),
            "Netflix" => new Uri("https://www.netflix.com"),
            "TBS" => new Uri("https://www.tbs.com/mlb-on-tbs"),
            _ => MlbTvUrl(game.Id),
        };
    }

    private static Uri MlbTvUrl(int gamePk) => new($"https://www.mlb.com/tv/g{gamePk}");
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter FullyQualifiedName~StreamLinkRouterTests
```

Expected: all pass.

- [ ] **Step 5: Run the whole suite and the publish check**

```bash
dotnet test
dotnet publish src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Expected: all tests pass; publish succeeds.

- [ ] **Step 6: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/StreamLinkRouter.cs windows/tests/OnDeck.Core.Tests/Utilities/StreamLinkRouterTests.cs
git commit -m "phase 1: port StreamLinkRouter"
```

---

## Done criteria

- `dotnet build` and `dotnet test` green from `windows/`.
- Single-file publish recipe produces `OnDeck.App.exe`.
- `OnDeck.Core` contains `Models/{Player,PlayerState,Game}.cs` and `Utilities/{TeamMapping,NameCleaner,FantraxUrlParser,StreamLinkRouter}.cs` with no Windows-specific references.
- Every public behaviour in the five Swift spec files has at least one test.

## Deviations from the Swift original (recorded for the port log)

1. **`TeamMapping.Abbreviation` is now deterministic.** Swift derived the reverse map from a randomly-ordered `Dictionary`, so `Athletics` could resolve to `ATH` or `OAK` per process. The C# port drives it from an ordered list; `ATH` always wins.
2. **`TeamMapping.Matches` drops the redundant `== expected` clause** — `Contains` subsumes it.
3. **`Player` and `Game` implement structural equality by hand**, because C# records compare collection members by reference while Swift's `Set`/`Array` are value types.
4. **`FantraxUrlParser` requires an absolute URI.** Swift's `URL(string:)` accepts relative strings, but every such input reaches the same `nil` result via the missing-league-ID guard, so behaviour is unchanged for real inputs.
5. **Swift `Date` → C# `DateTimeOffset`** throughout.
6. **`PlayerPosition` and `RosterStatus` are namespace-level, not nested in `Player`** — CS0102 forbids a nested type sharing a name with the `RosterStatus` property. See Task 2.
7. **`windows/NuGet.config` added** (not in the original file structure). The machine-wide `NuGet.Config` has an empty `<packageSources>`, clearing the implicit nuget.org default and failing every restore with NU1100.
8. **`dotnet new sln` produced `OnDeck.slnx`**, the .NET 10 default XML solution format, rather than `OnDeck.sln`. Kept as-is.
9. **`StreamLinkRouter` Netflix URL gains a trailing slash.** .NET's `Uri` normalizes a bare authority (`https://www.netflix.com`) to `https://www.netflix.com/`; Swift's `URL` keeps the string verbatim. Same destination once handed to a browser — the only affected route is Netflix, since every other target already has a path.
