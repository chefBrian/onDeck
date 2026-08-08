# Phase 2: LiveFeedPatcher — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port `LiveFeedPatcher` — the typed RFC 6902 patch applier that keeps `/feed/live` state current from ~2-byte diffPatch responses — together with the `LiveFeedData` model, the `feed/live` decoder it round-trips against, and `UnknownPatchLogger`.

**Architecture:** Swift applies patches to a `LiveFeedData` **struct** via `inout`, copying to a working value first so partial state never escapes. C# has no free value semantics here, so `LiveFeedData` is a mutable class with an explicit deep `Clone()`; `Apply` clones, mutates, and returns the new instance. Untyped Swift `[String: Any]` patch values become `JsonElement?`. Two-tier dispatch is preserved exactly: a `(op, path)` tuple switch for registered leaves, then prefix handlers for boxscore arrays and player stats, then a decorative-prefix table, then the unknown-patch logger.

**Tech Stack:** .NET 10, `System.Text.Json` (`JsonElement`/`JsonDocument`), xunit.

## Global Constraints

From `windows/PORT_PLAN.md` — every task's requirements implicitly include these.

- `OnDeck.Core` must have **zero** Windows-specific dependencies — it builds and tests on macOS.
- No `ConfigureAwait(false)` anywhere in Core. (No async code in this plan.)
- Single-file publish must stay green:
  `dotnet publish src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`
- Mirror Swift names 1:1 where possible.
- Keep the custom dict-fallback handling; do **NOT** swap the patcher for a generic RFC 6902 library.
- Commands run from `windows/`; test filters use `dotnet test tests/OnDeck.Core.Tests --filter ...`.

## Scope note — pulled forward from Phase 3

`LiveFeedData`, `PlayerGameStats`, `PlayerBattingStats`, `PlayerPitchingStats` and the `feed/live` decoder live in `Networking/MLBStatsAPI.swift`, which the master plan assigns to Phase 3. They land here instead because the patcher's primary correctness test (`LiveFeedPatcherTests.swift:14-22`) asserts *patched state equals decoder output for the equivalent JSON* — the test that catches field-mapping drift. Phase 3 keeps the HTTP client, schedule/lineup parsing, `fetchDiffPatch` transport, timecode formation and Fantrax; it consumes what this phase builds.

## Swift → C# mapping decisions

| Swift | C# | Why |
|---|---|---|
| `LiveFeedData` struct + `inout` | mutable `class` + `Clone()`, `Apply` returns the patched copy | C# structs in dictionaries/closures make in-place mutation error-prone; the clone preserves the all-or-nothing guarantee |
| `[[String: Any]]` op | `PatchOperation(string Op, string Path, JsonElement? Value, string? From)` | Typed at the boundary; `op`/`path` guard moves into `TryParse` |
| `value as? String` | `StringValue(JsonElement?)` → `null` unless `JsonValueKind.String` | Faithful: non-strings null the field where Swift does |
| `intValue(_:)` | `IntValue(JsonElement?)` — number (int or truncated double) or parseable string | Mirrors the Int/Double/NSNumber/String ladder |
| `#if DEBUG` gating | `UnknownPatchLogger?` parameter; `null` skips both the decorative scan and the record | Same "no cost when nothing consumes it" property, and testable |
| CSV file + rotation | in-memory records + `Debug.WriteLine` | Master plan: "log target becomes `ILogger`/Debug" |

## File Structure

| File | Responsibility |
|---|---|
| `src/OnDeck.Core/Models/LiveFeedData.cs` | `LiveFeedData` (mutable, structural equality, `Clone`) + the three stats types with `Formatted` |
| `src/OnDeck.Core/Networking/LiveFeedDecoder.cs` | `feed/live` JSON → `LiveFeedData`; private DTOs |
| `src/OnDeck.Core/Utilities/UnknownPatchLogger.cs` | Per-key-sampled record of unhandled `(op, path)` pairs |
| `src/OnDeck.Core/Utilities/PatchOperation.cs` | One RFC 6902 op + `TryParse` |
| `src/OnDeck.Core/Utilities/LiveFeedPatcher.cs` | The patcher (built up across Tasks 4–8) |
| `tests/OnDeck.Core.Tests/Fixtures/LiveFeedPatcherFixtures.cs` | Translated from `LiveFeedPatcherFixtures.swift` |
| `tests/OnDeck.Core.Tests/Models/LiveFeedDataTests.cs`, `Networking/LiveFeedDecoderTests.cs`, `Utilities/UnknownPatchLoggerTests.cs`, `Utilities/PatchOperationTests.cs`, `Utilities/LiveFeedPatcher*Tests.cs` | One test file per production concern |

---

## Task 1: LiveFeedData and stats models

**Files:**
- Create: `src/OnDeck.Core/Models/LiveFeedData.cs`
- Create: `tests/OnDeck.Core.Tests/Models/LiveFeedDataTests.cs`

**Spec:** `Networking/MLBStatsAPI.swift:255-339`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed class LiveFeedData` with settable properties: `string? TimeStamp`, `string GameState`, `string? DetailedState`, `int? CurrentBatterId`, `string? CurrentBatterName`, `int? CurrentPitcherId`, `string? CurrentPitcherName`, `int? Inning`, `string? InningHalf`, `string? InningState`, `int HomeScore`, `int AwayScore`, `string HomeTeam`, `string AwayTeam`, `int HomeTeamId`, `int AwayTeamId`, `int Balls`, `int Strikes`, `int Outs`, `int? RunnerOnFirst`, `int? RunnerOnSecond`, `int? RunnerOnThird`, `bool IsPlayComplete`, `string? LastPlayEvent`, `string? LastPlayDescription`, `List<int> HomeBattingOrder`, `List<int> AwayBattingOrder`, `List<int> HomePitchers`, `List<int> AwayPitchers`, `Dictionary<int, PlayerGameStats> PlayerStats`
  - `LiveFeedData Clone()` — deep: all four lists and every `PlayerGameStats` (and its batting/pitching) copied
  - Structural `Equals`/`GetHashCode` over every field including list order and dictionary contents
  - `sealed class PlayerGameStats { PlayerBattingStats? Batting; PlayerPitchingStats? Pitching; PlayerGameStats Clone(); }`
  - `sealed class PlayerBattingStats` — `int?` `AtBats, Hits, Runs, Doubles, Triples, HomeRuns, Rbi, BaseOnBalls, StrikeOuts, StolenBases`; `string? Formatted`; `Clone()`
  - `sealed class PlayerPitchingStats` — `string? InningsPitched`; `int?` `Hits, EarnedRuns, StrikeOuts, BaseOnBalls, NumberOfPitches`; `string? Formatted`; `Clone()`
  - All three stats types get structural equality.

**`Formatted` rules (copy exactly — `MLBStatsAPI.swift:305-338`):**
- Batting: `null` unless `AtBats` is non-null **and** (`AtBats > 0` or `BaseOnBalls > 0` or `StolenBases > 0`). Base is `"{Hits ?? 0}-{AtBats}"`. Extras appended in order 2B, 3B, HR, RBI, R, BB, SB, each only when `> 0`; count-prefixed only when `> 1` except RBI and R which are **always** count-prefixed (`"2 RBI"`, `"1 R"`). Joined `", "` and attached with `" · "` (U+00B7).
- Pitching: `null` when `InningsPitched` is null or `"0.0"`. Parts: `"{ip} IP"`, then `"{K}K"` when `StrikeOuts > 0`, `"{ER}ER"` when `EarnedRuns` is non-null (**including 0**), `"{P}P"` when `NumberOfPitches > 0`. Joined `", "`.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Models/LiveFeedDataTests.cs`:

```csharp
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class LiveFeedDataTests
{
    private static LiveFeedData Sample() => new()
    {
        TimeStamp = "20260416_180000",
        GameState = "Live",
        DetailedState = "In Progress",
        HomeTeam = "Home",
        AwayTeam = "Away",
        HomeTeamId = 222,
        AwayTeamId = 111,
        HomeBattingOrder = [1, 2, 3],
        AwayPitchers = [9],
        PlayerStats = { [1] = new PlayerGameStats { Batting = new PlayerBattingStats { AtBats = 2 } } },
    };

    [Fact]
    public void Clone_CopiesListsSoMutationDoesNotLeak()
    {
        var original = Sample();
        var clone = original.Clone();

        clone.HomeBattingOrder.Add(4);
        clone.AwayPitchers[0] = 10;

        Assert.Equal([1, 2, 3], original.HomeBattingOrder);
        Assert.Equal([9], original.AwayPitchers);
    }

    [Fact]
    public void Clone_CopiesPlayerStatsDeeply()
    {
        var original = Sample();
        var clone = original.Clone();

        clone.PlayerStats[1].Batting!.AtBats = 99;
        clone.PlayerStats[2] = new PlayerGameStats();

        Assert.Equal(2, original.PlayerStats[1].Batting!.AtBats);
        Assert.False(original.PlayerStats.ContainsKey(2));
    }

    [Fact]
    public void Equality_IsStructural()
    {
        Assert.Equal(Sample(), Sample());
        Assert.Equal(Sample().GetHashCode(), Sample().GetHashCode());
        Assert.Equal(Sample(), Sample().Clone());
    }

    [Fact]
    public void Equality_DistinguishesListOrder()
    {
        var a = Sample();
        var b = Sample();
        b.HomeBattingOrder = [3, 2, 1];
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_DistinguishesNestedStats()
    {
        var a = Sample();
        var b = Sample();
        b.PlayerStats[1].Batting!.AtBats = 3;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BattingFormatted_NullWithoutAtBats()
    {
        Assert.Null(new PlayerBattingStats().Formatted);
    }

    [Fact]
    public void BattingFormatted_NullWhenNoActivity()
    {
        Assert.Null(new PlayerBattingStats { AtBats = 0 }.Formatted);
    }

    [Theory]
    [InlineData(0, 0, 1, 0, "0-0 · BB")]           // walk with no at-bat still counts as activity
    [InlineData(0, 0, 0, 1, "0-0 · SB")]
    public void BattingFormatted_ActivityWithoutAtBats(
        int atBats, int hits, int walks, int steals, string expected)
    {
        var stats = new PlayerBattingStats
        {
            AtBats = atBats, Hits = hits, BaseOnBalls = walks, StolenBases = steals,
        };
        Assert.Equal(expected, stats.Formatted);
    }

    [Fact]
    public void BattingFormatted_MatchesLegacyOutput()
    {
        // LiveFeedPatcherTests.swift:171-180
        var stats = new PlayerBattingStats
        {
            AtBats = 4, Hits = 2, HomeRuns = 1, Rbi = 2, Runs = 1,
        };
        Assert.Equal("2-4 · HR, 2 RBI, 1 R", stats.Formatted);
    }

    [Fact]
    public void BattingFormatted_PluralizesCountsAboveOne()
    {
        var stats = new PlayerBattingStats
        {
            AtBats = 5, Hits = 4, Doubles = 2, Triples = 2, HomeRuns = 2,
            Rbi = 1, Runs = 1, BaseOnBalls = 2, StolenBases = 2,
        };
        Assert.Equal("4-5 · 2 2B, 2 3B, 2 HR, 1 RBI, 1 R, 2 BB, 2 SB", stats.Formatted);
    }

    [Fact]
    public void BattingFormatted_SingularExtrasOmitTheCount()
    {
        var stats = new PlayerBattingStats
        {
            AtBats = 3, Hits = 1, Doubles = 1, BaseOnBalls = 1, StolenBases = 1,
        };
        Assert.Equal("1-3 · 2B, BB, SB", stats.Formatted);
    }

    [Fact]
    public void BattingFormatted_NoExtrasIsBareLine()
    {
        Assert.Equal("0-4", new PlayerBattingStats { AtBats = 4, Hits = 0 }.Formatted);
    }

    [Fact]
    public void PitchingFormatted_NullWhenNotYetPitched()
    {
        Assert.Null(new PlayerPitchingStats().Formatted);
        Assert.Null(new PlayerPitchingStats { InningsPitched = "0.0" }.Formatted);
    }

    [Fact]
    public void PitchingFormatted_MatchesLegacyOutput()
    {
        // LiveFeedPatcherTests.swift:182-190
        var stats = new PlayerPitchingStats
        {
            InningsPitched = "6.1", StrikeOuts = 7, EarnedRuns = 2, NumberOfPitches = 98,
        };
        Assert.Equal("6.1 IP, 7K, 2ER, 98P", stats.Formatted);
    }

    [Fact]
    public void PitchingFormatted_IncludesZeroEarnedRunsButDropsZeroKAndPitches()
    {
        var stats = new PlayerPitchingStats
        {
            InningsPitched = "2.0", StrikeOuts = 0, EarnedRuns = 0, NumberOfPitches = 0,
        };
        Assert.Equal("2.0 IP, 0ER", stats.Formatted);
    }

    [Fact]
    public void PitchingFormatted_OmitsEarnedRunsWhenNull()
    {
        Assert.Equal("1.0 IP", new PlayerPitchingStats { InningsPitched = "1.0" }.Formatted);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedDataTests`
Expected: compile error — `LiveFeedData` does not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Models/LiveFeedData.cs`**

```csharp
namespace OnDeck.Core.Models;

/// <summary>
/// Port of <c>LiveFeedData</c> from <c>Networking/MLBStatsAPI.swift</c>. Swift models this
/// as a struct patched through <c>inout</c>; C# uses a mutable class plus an explicit deep
/// <see cref="Clone"/> so <c>LiveFeedPatcher.Apply</c> can keep the same guarantee that
/// partial state never escapes.
/// </summary>
public sealed class LiveFeedData : IEquatable<LiveFeedData>
{
    public string? TimeStamp { get; set; }          // /metaData/timeStamp - diffPatch startTimecode
    public string GameState { get; set; } = "";     // "Preview", "Live", "Final"
    public string? DetailedState { get; set; }      // "Pre-Game", "Warmup", "In Progress", ...
    public int? CurrentBatterId { get; set; }
    public string? CurrentBatterName { get; set; }
    public int? CurrentPitcherId { get; set; }
    public string? CurrentPitcherName { get; set; }
    public int? Inning { get; set; }
    public string? InningHalf { get; set; }
    public string? InningState { get; set; }
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int HomeTeamId { get; set; }
    public int AwayTeamId { get; set; }
    public int Balls { get; set; }
    public int Strikes { get; set; }
    public int Outs { get; set; }
    public int? RunnerOnFirst { get; set; }
    public int? RunnerOnSecond { get; set; }
    public int? RunnerOnThird { get; set; }
    public bool IsPlayComplete { get; set; }
    public string? LastPlayEvent { get; set; }
    public string? LastPlayDescription { get; set; }
    public List<int> HomeBattingOrder { get; set; } = [];
    public List<int> AwayBattingOrder { get; set; } = [];
    public List<int> HomePitchers { get; set; } = [];
    public List<int> AwayPitchers { get; set; } = [];
    public Dictionary<int, PlayerGameStats> PlayerStats { get; set; } = [];

    public LiveFeedData Clone()
    {
        var clone = (LiveFeedData)MemberwiseClone();
        clone.HomeBattingOrder = [.. HomeBattingOrder];
        clone.AwayBattingOrder = [.. AwayBattingOrder];
        clone.HomePitchers = [.. HomePitchers];
        clone.AwayPitchers = [.. AwayPitchers];
        clone.PlayerStats = PlayerStats.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        return clone;
    }

    public bool Equals(LiveFeedData? other) =>
        other is not null
        && TimeStamp == other.TimeStamp
        && GameState == other.GameState
        && DetailedState == other.DetailedState
        && CurrentBatterId == other.CurrentBatterId
        && CurrentBatterName == other.CurrentBatterName
        && CurrentPitcherId == other.CurrentPitcherId
        && CurrentPitcherName == other.CurrentPitcherName
        && Inning == other.Inning
        && InningHalf == other.InningHalf
        && InningState == other.InningState
        && HomeScore == other.HomeScore
        && AwayScore == other.AwayScore
        && HomeTeam == other.HomeTeam
        && AwayTeam == other.AwayTeam
        && HomeTeamId == other.HomeTeamId
        && AwayTeamId == other.AwayTeamId
        && Balls == other.Balls
        && Strikes == other.Strikes
        && Outs == other.Outs
        && RunnerOnFirst == other.RunnerOnFirst
        && RunnerOnSecond == other.RunnerOnSecond
        && RunnerOnThird == other.RunnerOnThird
        && IsPlayComplete == other.IsPlayComplete
        && LastPlayEvent == other.LastPlayEvent
        && LastPlayDescription == other.LastPlayDescription
        && HomeBattingOrder.SequenceEqual(other.HomeBattingOrder)
        && AwayBattingOrder.SequenceEqual(other.AwayBattingOrder)
        && HomePitchers.SequenceEqual(other.HomePitchers)
        && AwayPitchers.SequenceEqual(other.AwayPitchers)
        && PlayerStatsEqual(other.PlayerStats);

    private bool PlayerStatsEqual(Dictionary<int, PlayerGameStats> other)
    {
        if (PlayerStats.Count != other.Count) return false;
        foreach (var (id, stats) in PlayerStats)
        {
            if (!other.TryGetValue(id, out var otherStats) || !stats.Equals(otherStats)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as LiveFeedData);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TimeStamp);
        hash.Add(GameState);
        hash.Add(DetailedState);
        hash.Add(CurrentBatterId);
        hash.Add(CurrentPitcherId);
        hash.Add(Inning);
        hash.Add(HomeScore);
        hash.Add(AwayScore);
        hash.Add(Balls);
        hash.Add(Strikes);
        hash.Add(Outs);
        hash.Add(RunnerOnFirst);
        hash.Add(RunnerOnSecond);
        hash.Add(RunnerOnThird);
        hash.Add(IsPlayComplete);
        foreach (var id in HomeBattingOrder) hash.Add(id);
        foreach (var id in AwayBattingOrder) hash.Add(id);
        foreach (var id in HomePitchers) hash.Add(id);
        foreach (var id in AwayPitchers) hash.Add(id);
        hash.Add(PlayerStats.Count);
        return hash.ToHashCode();
    }
}

public sealed class PlayerGameStats : IEquatable<PlayerGameStats>
{
    public PlayerBattingStats? Batting { get; set; }
    public PlayerPitchingStats? Pitching { get; set; }

    public PlayerGameStats Clone() => new()
    {
        Batting = Batting?.Clone(),
        Pitching = Pitching?.Clone(),
    };

    public bool Equals(PlayerGameStats? other) =>
        other is not null
        && Equals(Batting, other.Batting)
        && Equals(Pitching, other.Pitching);

    public override bool Equals(object? obj) => Equals(obj as PlayerGameStats);

    public override int GetHashCode() => HashCode.Combine(Batting, Pitching);
}

public sealed class PlayerBattingStats : IEquatable<PlayerBattingStats>
{
    public int? AtBats { get; set; }
    public int? Hits { get; set; }
    public int? Runs { get; set; }
    public int? Doubles { get; set; }
    public int? Triples { get; set; }
    public int? HomeRuns { get; set; }
    public int? Rbi { get; set; }
    public int? BaseOnBalls { get; set; }
    public int? StrikeOuts { get; set; }
    public int? StolenBases { get; set; }

    public string? Formatted
    {
        get
        {
            if (AtBats is not { } atBats) return null;

            var hasActivity = atBats > 0 || (BaseOnBalls ?? 0) > 0 || (StolenBases ?? 0) > 0;
            if (!hasActivity) return null;

            var line = $"{Hits ?? 0}-{atBats}";
            var extras = new List<string>();
            if (Doubles is > 0 and var doubles) extras.Add(doubles > 1 ? $"{doubles} 2B" : "2B");
            if (Triples is > 0 and var triples) extras.Add(triples > 1 ? $"{triples} 3B" : "3B");
            if (HomeRuns is > 0 and var homeRuns) extras.Add(homeRuns > 1 ? $"{homeRuns} HR" : "HR");
            if (Rbi is > 0 and var rbi) extras.Add($"{rbi} RBI");
            if (Runs is > 0 and var runs) extras.Add($"{runs} R");
            if (BaseOnBalls is > 0 and var walks) extras.Add(walks > 1 ? $"{walks} BB" : "BB");
            if (StolenBases is > 0 and var steals) extras.Add(steals > 1 ? $"{steals} SB" : "SB");

            return extras.Count > 0 ? line + " · " + string.Join(", ", extras) : line;
        }
    }

    public PlayerBattingStats Clone() => (PlayerBattingStats)MemberwiseClone();

    public bool Equals(PlayerBattingStats? other) =>
        other is not null
        && AtBats == other.AtBats
        && Hits == other.Hits
        && Runs == other.Runs
        && Doubles == other.Doubles
        && Triples == other.Triples
        && HomeRuns == other.HomeRuns
        && Rbi == other.Rbi
        && BaseOnBalls == other.BaseOnBalls
        && StrikeOuts == other.StrikeOuts
        && StolenBases == other.StolenBases;

    public override bool Equals(object? obj) => Equals(obj as PlayerBattingStats);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(AtBats);
        hash.Add(Hits);
        hash.Add(Runs);
        hash.Add(Doubles);
        hash.Add(Triples);
        hash.Add(HomeRuns);
        hash.Add(Rbi);
        hash.Add(BaseOnBalls);
        hash.Add(StrikeOuts);
        hash.Add(StolenBases);
        return hash.ToHashCode();
    }
}

public sealed class PlayerPitchingStats : IEquatable<PlayerPitchingStats>
{
    public string? InningsPitched { get; set; }
    public int? Hits { get; set; }
    public int? EarnedRuns { get; set; }
    public int? StrikeOuts { get; set; }
    public int? BaseOnBalls { get; set; }
    public int? NumberOfPitches { get; set; }

    public string? Formatted
    {
        get
        {
            if (InningsPitched is not { } ip || ip == "0.0") return null;

            var parts = new List<string> { $"{ip} IP" };
            if (StrikeOuts is > 0 and var strikeOuts) parts.Add($"{strikeOuts}K");
            if (EarnedRuns is { } earnedRuns) parts.Add($"{earnedRuns}ER");
            if (NumberOfPitches is > 0 and var pitches) parts.Add($"{pitches}P");
            return string.Join(", ", parts);
        }
    }

    public PlayerPitchingStats Clone() => (PlayerPitchingStats)MemberwiseClone();

    public bool Equals(PlayerPitchingStats? other) =>
        other is not null
        && InningsPitched == other.InningsPitched
        && Hits == other.Hits
        && EarnedRuns == other.EarnedRuns
        && StrikeOuts == other.StrikeOuts
        && BaseOnBalls == other.BaseOnBalls
        && NumberOfPitches == other.NumberOfPitches;

    public override bool Equals(object? obj) => Equals(obj as PlayerPitchingStats);

    public override int GetHashCode() =>
        HashCode.Combine(InningsPitched, Hits, EarnedRuns, StrikeOuts, BaseOnBalls, NumberOfPitches);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedDataTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Models/LiveFeedData.cs windows/tests/OnDeck.Core.Tests/Models/LiveFeedDataTests.cs
git commit -m "phase 2: port LiveFeedData and game stats models"
```

---

## Task 2: LiveFeedDecoder

**Files:**
- Create: `src/OnDeck.Core/Networking/LiveFeedDecoder.cs`
- Create: `tests/OnDeck.Core.Tests/Fixtures/LiveFeedPatcherFixtures.cs`
- Create: `tests/OnDeck.Core.Tests/Networking/LiveFeedDecoderTests.cs`

**Spec:** `MLBStatsAPI.swift:95-98, 153-192, 210-228, 418-541`; fixtures from `LiveFeedPatcherFixtures.swift:9-114`

**Interfaces:**
- Consumes: `LiveFeedData` and stats types (Task 1).
- Produces:
  - `static class OnDeck.Core.Networking.LiveFeedDecoder` with `LiveFeedData Decode(string json)` and `LiveFeedData Decode(ReadOnlySpan<byte> utf8Json)`. Throws `JsonException` on malformed input.
  - `static class OnDeck.Core.Tests.Fixtures.LiveFeedPatcherFixtures` with `const string BaseFeedJson` and `const string AfterScalarReplacesJson` (verbatim from the Swift fixtures). Patch fixtures are added in Task 8.

**Defaulting rules (`parseLiveFeedResponse`) — preserve exactly:**
- `gameState` and both team `name`/`id` are **required**; everything else tolerates absence.
- `homeScore`/`awayScore`/`balls`/`strikes`/`outs` default to `0`; `isPlayComplete` to `false`; the four boxscore arrays to empty.
- `playerStats` keys come from boxscore player keys shaped `ID<n>`; a player is included **only if** its `stats` object carries a `batting` or `pitching` object. An empty `"batting": {}` counts as present.

- [ ] **Step 1: Write the fixtures**

Create `tests/OnDeck.Core.Tests/Fixtures/LiveFeedPatcherFixtures.cs` holding the two JSON documents from `LiveFeedPatcherFixtures.swift:9-114`, copied byte-for-byte into C# raw string literals:

```csharp
namespace OnDeck.Core.Tests.Fixtures;

/// <summary>
/// Translated from <c>Utilities/LiveFeedPatcherFixtures.swift</c>. Fixtures are small by
/// design — these test dispatch correctness, not volume.
/// </summary>
public static class LiveFeedPatcherFixtures
{
    /// <summary>Minimal canonical feed — just enough shape for parse + patch round-trips.</summary>
    public const string BaseFeedJson = """
    {
      "metaData": {"timeStamp": "20260416_180000"},
      "gameData": {
        "status": {"abstractGameState": "Live", "detailedState": "In Progress"},
        "teams": {
          "away": {"id": 111, "name": "Away"},
          "home": {"id": 222, "name": "Home"}
        }
      },
      "liveData": {
        "plays": {
          "currentPlay": {
            "about": {"isComplete": false},
            "matchup": {
              "batter": {"id": 1, "fullName": "Batter One"},
              "pitcher": {"id": 2, "fullName": "Pitcher Two"}
            },
            "count": {"balls": 0, "strikes": 0, "outs": 0}
          }
        },
        "linescore": {
          "currentInning": 1,
          "inningHalf": "Top",
          "inningState": "Top",
          "teams": {
            "home": {"runs": 0},
            "away": {"runs": 0}
          }
        },
        "boxscore": {
          "teams": {
            "home": {
              "battingOrder": [],
              "pitchers": [2],
              "players": {
                "ID2": {"stats": {"pitching": {"inningsPitched": "0.0"}}}
              }
            },
            "away": {
              "battingOrder": [1],
              "pitchers": [],
              "players": {
                "ID1": {"stats": {"batting": {"atBats": 0}}}
              }
            }
          }
        }
      }
    }
    """;

    /// <summary>
    /// Feed after a single plate appearance ends with a 2-run HR. Equivalent terminal state
    /// for the <c>ScalarReplacesPatch</c> fixture added in Task 8.
    /// </summary>
    public const string AfterScalarReplacesJson = """
    {
      "metaData": {"timeStamp": "20260416_180010"},
      "gameData": {
        "status": {"abstractGameState": "Live", "detailedState": "In Progress"},
        "teams": {
          "away": {"id": 111, "name": "Away"},
          "home": {"id": 222, "name": "Home"}
        }
      },
      "liveData": {
        "plays": {
          "currentPlay": {
            "about": {"isComplete": true},
            "matchup": {
              "batter": {"id": 1, "fullName": "Batter One"},
              "pitcher": {"id": 2, "fullName": "Pitcher Two"}
            },
            "count": {"balls": 3, "strikes": 2, "outs": 0},
            "result": {"event": "Home Run", "description": "Batter One hits a 2-run HR"}
          }
        },
        "linescore": {
          "currentInning": 1,
          "inningHalf": "Top",
          "inningState": "Top",
          "teams": {
            "home": {"runs": 0},
            "away": {"runs": 2}
          }
        },
        "boxscore": {
          "teams": {
            "home": {
              "battingOrder": [],
              "pitchers": [2],
              "players": {
                "ID2": {"stats": {"pitching": {"inningsPitched": "0.0", "earnedRuns": 2, "hits": 1, "numberOfPitches": 6}}}
              }
            },
            "away": {
              "battingOrder": [1],
              "pitchers": [],
              "players": {
                "ID1": {"stats": {"batting": {"atBats": 1, "hits": 1, "homeRuns": 1, "rbi": 2, "runs": 1}}}
              }
            }
          }
        }
      }
    }
    """;
}
```

- [ ] **Step 2: Write the failing decoder test**

Create `tests/OnDeck.Core.Tests/Networking/LiveFeedDecoderTests.cs`:

```csharp
using System.Text.Json;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;

namespace OnDeck.Core.Tests.Networking;

public class LiveFeedDecoderTests
{
    [Fact]
    public void Decode_ReadsEveryModeledFieldFromTheBaseFixture()
    {
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

        Assert.Equal("20260416_180000", feed.TimeStamp);
        Assert.Equal("Live", feed.GameState);
        Assert.Equal("In Progress", feed.DetailedState);
        Assert.Equal(1, feed.CurrentBatterId);
        Assert.Equal("Batter One", feed.CurrentBatterName);
        Assert.Equal(2, feed.CurrentPitcherId);
        Assert.Equal("Pitcher Two", feed.CurrentPitcherName);
        Assert.Equal(1, feed.Inning);
        Assert.Equal("Top", feed.InningHalf);
        Assert.Equal("Top", feed.InningState);
        Assert.Equal(0, feed.HomeScore);
        Assert.Equal(0, feed.AwayScore);
        Assert.Equal("Home", feed.HomeTeam);
        Assert.Equal("Away", feed.AwayTeam);
        Assert.Equal(222, feed.HomeTeamId);
        Assert.Equal(111, feed.AwayTeamId);
        Assert.Equal(0, feed.Balls);
        Assert.Equal(0, feed.Strikes);
        Assert.Equal(0, feed.Outs);
        Assert.False(feed.IsPlayComplete);
        Assert.Null(feed.LastPlayEvent);
        Assert.Null(feed.LastPlayDescription);
        Assert.Empty(feed.HomeBattingOrder);
        Assert.Equal([1], feed.AwayBattingOrder);
        Assert.Equal([2], feed.HomePitchers);
        Assert.Empty(feed.AwayPitchers);
    }

    [Fact]
    public void Decode_ReadsRunnersAsNullWhenOffenseAbsent()
    {
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

        Assert.Null(feed.RunnerOnFirst);
        Assert.Null(feed.RunnerOnSecond);
        Assert.Null(feed.RunnerOnThird);
    }

    [Fact]
    public void Decode_ReadsOffenseRunnerIds()
    {
        const string json = """
        {
          "gameData": {
            "status": {"abstractGameState": "Live"},
            "teams": {"away": {"id": 1, "name": "A"}, "home": {"id": 2, "name": "H"}}
          },
          "liveData": {
            "linescore": {
              "offense": {"first": {"id": 10}, "third": {"id": 30}}
            }
          }
        }
        """;

        var feed = LiveFeedDecoder.Decode(json);

        Assert.Equal(10, feed.RunnerOnFirst);
        Assert.Null(feed.RunnerOnSecond);
        Assert.Equal(30, feed.RunnerOnThird);
    }

    [Fact]
    public void Decode_KeysPlayerStatsByNumericIdFromIdPrefixedKeys()
    {
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

        Assert.Equal([1, 2], feed.PlayerStats.Keys.Order());
        Assert.Equal(0, feed.PlayerStats[1].Batting!.AtBats);
        Assert.Null(feed.PlayerStats[1].Pitching);
        Assert.Equal("0.0", feed.PlayerStats[2].Pitching!.InningsPitched);
        Assert.Null(feed.PlayerStats[2].Batting);
    }

    [Fact]
    public void Decode_SkipsPlayersWithNoBattingOrPitchingObject()
    {
        const string json = """
        {
          "gameData": {
            "status": {"abstractGameState": "Live"},
            "teams": {"away": {"id": 1, "name": "A"}, "home": {"id": 2, "name": "H"}}
          },
          "liveData": {
            "boxscore": {
              "teams": {
                "away": {"players": {
                  "ID5": {"stats": {}},
                  "ID6": {"stats": {"batting": {}}},
                  "notAnId": {"stats": {"batting": {"atBats": 1}}}
                }},
                "home": {"players": {}}
              }
            }
          }
        }
        """;

        var feed = LiveFeedDecoder.Decode(json);

        // ID5 has no batting/pitching object; "notAnId" is not ID-prefixed.
        Assert.Equal([6], feed.PlayerStats.Keys.Order());
        Assert.NotNull(feed.PlayerStats[6].Batting);
        Assert.Null(feed.PlayerStats[6].Batting!.AtBats);
    }

    [Fact]
    public void Decode_DefaultsMissingOptionalSections()
    {
        const string json = """
        {
          "gameData": {
            "status": {"abstractGameState": "Preview"},
            "teams": {"away": {"id": 1, "name": "A"}, "home": {"id": 2, "name": "H"}}
          },
          "liveData": {}
        }
        """;

        var feed = LiveFeedDecoder.Decode(json);

        Assert.Null(feed.TimeStamp);
        Assert.Equal("Preview", feed.GameState);
        Assert.Null(feed.DetailedState);
        Assert.Null(feed.CurrentBatterId);
        Assert.Equal(0, feed.HomeScore);
        Assert.Equal(0, feed.Balls);
        Assert.False(feed.IsPlayComplete);
        Assert.Empty(feed.HomeBattingOrder);
        Assert.Empty(feed.PlayerStats);
    }

    [Fact]
    public void Decode_ReadsPlayResult()
    {
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.AfterScalarReplacesJson);

        Assert.Equal("Home Run", feed.LastPlayEvent);
        Assert.Equal("Batter One hits a 2-run HR", feed.LastPlayDescription);
        Assert.True(feed.IsPlayComplete);
        Assert.Equal(3, feed.Balls);
        Assert.Equal(2, feed.Strikes);
        Assert.Equal(2, feed.AwayScore);
    }

    [Fact]
    public void Decode_ThrowsOnMalformedJson()
    {
        Assert.Throws<JsonException>(() => LiveFeedDecoder.Decode("{not json"));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedDecoderTests`
Expected: compile error — `LiveFeedDecoder` does not exist.

- [ ] **Step 4: Write `src/OnDeck.Core/Networking/LiveFeedDecoder.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using OnDeck.Core.Models;

namespace OnDeck.Core.Networking;

/// <summary>
/// Decodes MLB <c>/feed/live</c> JSON into <see cref="LiveFeedData"/>. Port of
/// <c>MLBStatsAPI.decodeLiveFeed</c> + <c>parseLiveFeedResponse</c> + <c>parsePlayerStats</c>.
/// </summary>
public static class LiveFeedDecoder
{
    // The wire format is camelCase (timeStamp, abstractGameState, battingOrder) and the DTO
    // properties are PascalCase, so case-insensitive matching is required.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static LiveFeedData Decode(string json) =>
        Parse(JsonSerializer.Deserialize<FeedResponse>(json, Options)
              ?? throw new JsonException("feed/live payload decoded to null"));

    public static LiveFeedData Decode(ReadOnlySpan<byte> utf8Json) =>
        Parse(JsonSerializer.Deserialize<FeedResponse>(utf8Json, Options)
              ?? throw new JsonException("feed/live payload decoded to null"));

    private static LiveFeedData Parse(FeedResponse response)
    {
        var currentPlay = response.LiveData?.Plays?.CurrentPlay;
        var linescore = response.LiveData?.Linescore;
        var offense = linescore?.Offense;
        var boxscore = response.LiveData?.Boxscore;

        return new LiveFeedData
        {
            TimeStamp = response.MetaData?.TimeStamp,
            GameState = response.GameData.Status.AbstractGameState,
            DetailedState = response.GameData.Status.DetailedState,
            CurrentBatterId = currentPlay?.Matchup?.Batter?.Id,
            CurrentBatterName = currentPlay?.Matchup?.Batter?.FullName,
            CurrentPitcherId = currentPlay?.Matchup?.Pitcher?.Id,
            CurrentPitcherName = currentPlay?.Matchup?.Pitcher?.FullName,
            Inning = linescore?.CurrentInning,
            InningHalf = linescore?.InningHalf,
            InningState = linescore?.InningState,
            HomeScore = linescore?.Teams?.Home?.Runs ?? 0,
            AwayScore = linescore?.Teams?.Away?.Runs ?? 0,
            HomeTeam = response.GameData.Teams.Home.Name,
            AwayTeam = response.GameData.Teams.Away.Name,
            HomeTeamId = response.GameData.Teams.Home.Id,
            AwayTeamId = response.GameData.Teams.Away.Id,
            Balls = currentPlay?.Count?.Balls ?? 0,
            Strikes = currentPlay?.Count?.Strikes ?? 0,
            Outs = currentPlay?.Count?.Outs ?? 0,
            RunnerOnFirst = offense?.First?.Id,
            RunnerOnSecond = offense?.Second?.Id,
            RunnerOnThird = offense?.Third?.Id,
            IsPlayComplete = currentPlay?.About?.IsComplete ?? false,
            LastPlayEvent = currentPlay?.Result?.Event,
            LastPlayDescription = currentPlay?.Result?.Description,
            HomeBattingOrder = [.. boxscore?.Teams?.Home?.BattingOrder ?? []],
            AwayBattingOrder = [.. boxscore?.Teams?.Away?.BattingOrder ?? []],
            HomePitchers = [.. boxscore?.Teams?.Home?.Pitchers ?? []],
            AwayPitchers = [.. boxscore?.Teams?.Away?.Pitchers ?? []],
            PlayerStats = ParsePlayerStats(boxscore),
        };
    }

    private static Dictionary<int, PlayerGameStats> ParsePlayerStats(FeedBoxscore? boxscore)
    {
        var result = new Dictionary<int, PlayerGameStats>();
        if (boxscore?.Teams is not { } teams) return result;

        foreach (var team in new[] { teams.Home, teams.Away })
        {
            if (team?.Players is not { } players) continue;

            foreach (var (key, player) in players)
            {
                if (!key.StartsWith("ID", StringComparison.Ordinal)) continue;
                if (!int.TryParse(key.AsSpan(2), out var id)) continue;
                if (player.Stats is not { } stats) continue;
                if (stats.Batting is null && stats.Pitching is null) continue;

                result[id] = new PlayerGameStats { Batting = stats.Batting, Pitching = stats.Pitching };
            }
        }

        return result;
    }

    // --- DTOs. Property names match the wire format exactly; no naming policy is applied.

    private sealed class FeedResponse
    {
        public FeedMetaData? MetaData { get; set; }
        public required FeedGameData GameData { get; set; }
        public FeedLiveData? LiveData { get; set; }
    }

    private sealed class FeedMetaData
    {
        public string? TimeStamp { get; set; }
    }

    private sealed class FeedGameData
    {
        public required FeedGameStatus Status { get; set; }
        public required FeedGameTeams Teams { get; set; }
    }

    private sealed class FeedGameStatus
    {
        public required string AbstractGameState { get; set; }
        public string? DetailedState { get; set; }
    }

    private sealed class FeedGameTeams
    {
        public required FeedTeamEntry Away { get; set; }
        public required FeedTeamEntry Home { get; set; }
    }

    private sealed class FeedTeamEntry
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }

    private sealed class FeedLiveData
    {
        public FeedPlays? Plays { get; set; }
        public FeedLinescore? Linescore { get; set; }
        public FeedBoxscore? Boxscore { get; set; }
    }

    private sealed class FeedPlays
    {
        public FeedCurrentPlay? CurrentPlay { get; set; }
    }

    private sealed class FeedCurrentPlay
    {
        public FeedPlayResult? Result { get; set; }
        public FeedPlayAbout? About { get; set; }
        public FeedMatchup? Matchup { get; set; }
        public FeedPlayCount? Count { get; set; }
    }

    private sealed class FeedPlayResult
    {
        public string? Type { get; set; }
        public string? Event { get; set; }
        public string? Description { get; set; }
    }

    private sealed class FeedPlayAbout
    {
        public bool IsComplete { get; set; }
    }

    private sealed class FeedMatchup
    {
        public FeedPlayer? Batter { get; set; }
        public FeedPlayer? Pitcher { get; set; }
    }

    private sealed class FeedPlayer
    {
        public int Id { get; set; }
        public string? FullName { get; set; }
    }

    private sealed class FeedPlayCount
    {
        public int Balls { get; set; }
        public int Strikes { get; set; }
        public int Outs { get; set; }
    }

    private sealed class FeedLinescore
    {
        public int? CurrentInning { get; set; }
        public string? InningHalf { get; set; }
        public string? InningState { get; set; }
        public FeedLinescoreTeams? Teams { get; set; }
        public FeedOffense? Offense { get; set; }
    }

    private sealed class FeedOffense
    {
        public FeedRunner? First { get; set; }
        public FeedRunner? Second { get; set; }
        public FeedRunner? Third { get; set; }
    }

    private sealed class FeedRunner
    {
        public int? Id { get; set; }
        public string? FullName { get; set; }
    }

    private sealed class FeedLinescoreTeams
    {
        public FeedLinescoreTeam? Home { get; set; }
        public FeedLinescoreTeam? Away { get; set; }
    }

    private sealed class FeedLinescoreTeam
    {
        public int? Runs { get; set; }
    }

    private sealed class FeedBoxscore
    {
        public FeedBoxscoreTeams? Teams { get; set; }
    }

    private sealed class FeedBoxscoreTeams
    {
        public FeedBoxscoreTeamEntry? Away { get; set; }
        public FeedBoxscoreTeamEntry? Home { get; set; }
    }

    private sealed class FeedBoxscoreTeamEntry
    {
        public List<int>? BattingOrder { get; set; }
        public List<int>? Pitchers { get; set; }
        public Dictionary<string, FeedBoxscorePlayer>? Players { get; set; }
    }

    private sealed class FeedBoxscorePlayer
    {
        public FeedBoxscorePlayerStats? Stats { get; set; }
    }

    private sealed class FeedBoxscorePlayerStats
    {
        public PlayerBattingStats? Batting { get; set; }
        public PlayerPitchingStats? Pitching { get; set; }
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedDecoderTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add windows/src/OnDeck.Core/Networking/LiveFeedDecoder.cs windows/tests/OnDeck.Core.Tests/
git commit -m "phase 2: port feed/live decoder and patcher fixtures"
```

---

## Task 3: PatchOperation and UnknownPatchLogger

**Files:**
- Create: `src/OnDeck.Core/Utilities/PatchOperation.cs`
- Create: `src/OnDeck.Core/Utilities/UnknownPatchLogger.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/PatchOperationTests.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/UnknownPatchLoggerTests.cs`

**Spec:** `LiveFeedPatcher.swift:16-30` (the op/path guard), `UnknownPatchLogger.swift`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed record PatchOperation(string Op, string Path, JsonElement? Value, string? From)` with
    `static PatchOperation? TryParse(JsonElement element)` — returns `null` unless both `op` and `path` are present strings — and `static IReadOnlyList<PatchOperation> ParseArray(JsonElement array)`.
  - `sealed class UnknownPatchLogger` with `void Record(string op, string path, string? from, JsonElement? value)`,
    `IReadOnlyList<UnknownPatchLogger.Entry> Entries { get; }`, `IReadOnlyDictionary<string, int> Counts { get; }`,
    `const int MaxPerKey = 3`, and `sealed record Entry(string Op, string Path, string? From, string ValuePreview)`.

**Behaviour:** each unique `"{op}|{path}"` key is retained up to `MaxPerKey` times; later occurrences only increment `Counts`. `ValuePreview` renders JSON compactly, `""` for a missing value, `"null"` for JSON null, and truncates to 120 characters. Every `Record` call also emits `Debug.WriteLine($"[LiveFeedPatcher] unknown: {op} {path}")` (plus ` from={from}` when present). No file I/O — the CSV and rotation from the Swift original are dropped per the master plan's `ILogger`/Debug note.

- [ ] **Step 1: Write the failing tests**

Create `tests/OnDeck.Core.Tests/Utilities/PatchOperationTests.cs`:

```csharp
using System.Text.Json;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class PatchOperationTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void TryParse_ReadsOpPathValueAndFrom()
    {
        var op = PatchOperation.TryParse(Json("""
            {"op": "copy", "path": "/a", "from": "/b", "value": 7}
            """));

        Assert.NotNull(op);
        Assert.Equal("copy", op.Op);
        Assert.Equal("/a", op.Path);
        Assert.Equal("/b", op.From);
        Assert.Equal(7, op.Value!.Value.GetInt32());
    }

    [Fact]
    public void TryParse_LeavesValueAndFromNullWhenAbsent()
    {
        var op = PatchOperation.TryParse(Json("""{"op": "remove", "path": "/a"}"""));

        Assert.NotNull(op);
        Assert.Null(op.Value);
        Assert.Null(op.From);
    }

    [Theory]
    [InlineData("""{"path": "/a"}""")]              // no op
    [InlineData("""{"op": "add"}""")]               // no path
    [InlineData("""{"op": 1, "path": "/a"}""")]     // op not a string
    [InlineData("""{"op": "add", "path": 2}""")]    // path not a string
    [InlineData("""[1, 2]""")]                      // not an object
    public void TryParse_ReturnsNullForMalformedOps(string json)
    {
        Assert.Null(PatchOperation.TryParse(Json(json)));
    }

    [Fact]
    public void ParseArray_SkipsMalformedEntries()
    {
        var ops = PatchOperation.ParseArray(Json("""
            [
              {"op": "replace", "path": "/a", "value": 1},
              {"op": "replace"},
              {"op": "remove", "path": "/b"}
            ]
            """));

        Assert.Equal(["/a", "/b"], ops.Select(o => o.Path));
    }

    [Fact]
    public void ParseArray_ReturnsEmptyForNonArray()
    {
        Assert.Empty(PatchOperation.ParseArray(Json("""{"op": "add", "path": "/a"}""")));
    }
}
```

Create `tests/OnDeck.Core.Tests/Utilities/UnknownPatchLoggerTests.cs`:

```csharp
using System.Text.Json;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class UnknownPatchLoggerTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Record_CapturesOpPathAndFrom()
    {
        var logger = new UnknownPatchLogger();
        logger.Record("replace", "/a/b", "/c", Json("42"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("replace", entry.Op);
        Assert.Equal("/a/b", entry.Path);
        Assert.Equal("/c", entry.From);
        Assert.Equal("42", entry.ValuePreview);
    }

    [Fact]
    public void Record_SamplesEachKeyAtMostMaxPerKeyTimes()
    {
        var logger = new UnknownPatchLogger();
        for (var i = 0; i < 10; i++) logger.Record("add", "/same", null, null);

        Assert.Equal(UnknownPatchLogger.MaxPerKey, logger.Entries.Count);
        Assert.Equal(10, logger.Counts["add|/same"]);
    }

    [Fact]
    public void Record_TracksDistinctKeysIndependently()
    {
        var logger = new UnknownPatchLogger();
        logger.Record("add", "/one", null, null);
        logger.Record("remove", "/one", null, null);   // same path, different op
        logger.Record("add", "/two", null, null);

        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(1, logger.Counts["add|/one"]);
        Assert.Equal(1, logger.Counts["remove|/one"]);
        Assert.Equal(1, logger.Counts["add|/two"]);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("null", "null")]
    [InlineData("\"text\"", "\"text\"")]
    [InlineData("{\"id\":5}", "{\"id\":5}")]
    [InlineData("[1,2,3]", "[1,2,3]")]
    public void Record_RendersValuePreview(string? json, string expected)
    {
        var logger = new UnknownPatchLogger();
        logger.Record("add", "/p", null, json is null ? null : Json(json));

        Assert.Equal(expected, Assert.Single(logger.Entries).ValuePreview);
    }

    [Fact]
    public void Record_TruncatesLongPreviewsTo120Characters()
    {
        var logger = new UnknownPatchLogger();
        logger.Record("add", "/p", null, Json($"\"{new string('x', 500)}\""));

        Assert.Equal(120, Assert.Single(logger.Entries).ValuePreview.Length);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/OnDeck.Core.Tests --filter "FullyQualifiedName~PatchOperationTests|FullyQualifiedName~UnknownPatchLoggerTests"`
Expected: compile errors — `PatchOperation` and `UnknownPatchLogger` do not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Utilities/PatchOperation.cs`**

```csharp
using System.Text.Json;

namespace OnDeck.Core.Utilities;

/// <summary>
/// One RFC 6902 operation from MLB's <c>/feed/live/diffPatch</c> response. Swift passes these
/// around as untyped <c>[String: Any]</c> and guards <c>op</c>/<c>path</c> inside
/// <c>LiveFeedPatcher.apply</c>; here the guard lives in <see cref="TryParse"/>.
/// </summary>
public sealed record PatchOperation(string Op, string Path, JsonElement? Value, string? From)
{
    public static PatchOperation? TryParse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (!element.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String) return null;
        if (!element.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) return null;

        var value = element.TryGetProperty("value", out var rawValue) ? rawValue : (JsonElement?)null;
        var from = element.TryGetProperty("from", out var rawFrom) && rawFrom.ValueKind == JsonValueKind.String
            ? rawFrom.GetString()
            : null;

        return new PatchOperation(op.GetString()!, path.GetString()!, value, from);
    }

    /// <summary>Parses an array of ops, skipping any entry missing <c>op</c> or <c>path</c>.</summary>
    public static IReadOnlyList<PatchOperation> ParseArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) return [];

        var ops = new List<PatchOperation>();
        foreach (var element in array.EnumerateArray())
        {
            if (TryParse(element) is { } op) ops.Add(op);
        }
        return ops;
    }
}
```

- [ ] **Step 4: Write `src/OnDeck.Core/Utilities/UnknownPatchLogger.cs`**

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace OnDeck.Core.Utilities;

/// <summary>
/// Record of RFC 6902 ops the typed patcher has no handler for. Port of
/// <c>Utilities/UnknownPatchLogger.swift</c>, with the CSV file and 10 MB rotation replaced
/// by in-memory entries plus <see cref="Debug"/> output (see PORT_PLAN: log target becomes
/// <c>ILogger</c>/Debug).
///
/// Per-key sampling: each unique <c>(op, path)</c> pair is retained up to
/// <see cref="MaxPerKey"/> times, after which occurrences are only counted. This keeps the
/// logger from allocating ~500 rows/min on decorative paths while still surfacing new
/// handlers to register.
/// </summary>
public sealed class UnknownPatchLogger
{
    public const int MaxPerKey = 3;
    private const int PreviewLength = 120;

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, int> _counts = [];

    public IReadOnlyList<Entry> Entries => _entries;

    public IReadOnlyDictionary<string, int> Counts => _counts;

    public void Record(string op, string path, string? from, JsonElement? value)
    {
        var key = $"{op}|{path}";
        var count = _counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
        _counts[key] = count;

        if (count > MaxPerKey) return;

        Debug.WriteLine($"[LiveFeedPatcher] unknown: {op} {path}{(from is null ? "" : $" from={from}")}");
        _entries.Add(new Entry(op, path, from, PreviewValue(value)));
    }

    private static string PreviewValue(JsonElement? value)
    {
        if (value is not { } element) return "";

        var rendered = element.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.String => $"\"{element.GetString()}\"",
            _ => element.GetRawText(),
        };

        return rendered.Length <= PreviewLength ? rendered : rendered[..PreviewLength];
    }

    public sealed record Entry(string Op, string Path, string? From, string ValuePreview);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/OnDeck.Core.Tests --filter "FullyQualifiedName~PatchOperationTests|FullyQualifiedName~UnknownPatchLoggerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/PatchOperation.cs windows/src/OnDeck.Core/Utilities/UnknownPatchLogger.cs windows/tests/OnDeck.Core.Tests/Utilities/
git commit -m "phase 2: add PatchOperation and UnknownPatchLogger"
```

---

## Task 4: LiveFeedPatcher — scalar leaves

**Files:**
- Create: `src/OnDeck.Core/Utilities/LiveFeedPatcher.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherScalarTests.cs`

**Spec:** `LiveFeedPatcher.swift:16-30, 42-163, 528-534`

**Interfaces:**
- Consumes: `LiveFeedData` (Task 1), `PatchOperation`, `UnknownPatchLogger` (Task 3).
- Produces:
  - `static class LiveFeedPatcher` with
    `LiveFeedData Apply(IReadOnlyList<PatchOperation> ops, LiveFeedData feed, UnknownPatchLogger? logger = null)`
  - Internal helpers used by later tasks: `static int? IntValue(JsonElement? value)`, `static string? StringValue(JsonElement? value)`.

**Registered leaves in this task (op set in parentheses):**

| Path | Ops | Effect |
|---|---|---|
| `/metaData/timeStamp` | replace, add | `TimeStamp = StringValue` |
| `/gameData/status/abstractGameState` | replace | `GameState = s` **only if the value is a string** |
| `/gameData/status/detailedState` | replace, add / remove | `DetailedState = StringValue` / `null` |
| `/gameData/teams/{home,away}/name` | replace | assign only if string |
| `/gameData/teams/{home,away}/id` | replace | assign only if `IntValue` non-null |
| `/liveData/plays/currentPlay/matchup/{batter,pitcher}/id` | replace, add | `Current*Id = IntValue` (may null) |
| `/liveData/plays/currentPlay/matchup/{batter,pitcher}/fullName` | replace, add | `Current*Name = StringValue` (may null) |
| `/liveData/plays/currentPlay/about/isComplete` | replace, add | `IsPlayComplete = bool ?? existing` |
| `/liveData/plays/currentPlay/result/{event,description}` | replace, add / remove | `LastPlay* = StringValue` / `null` |
| `/liveData/plays/currentPlay/count/{balls,strikes,outs}` | replace, add | `= IntValue ?? existing` |
| `/liveData/linescore/currentInning` | replace, add | `Inning = IntValue` (may null) |
| `/liveData/linescore/{inningHalf,inningState}` | replace, add | `= StringValue` (may null) |
| `/liveData/linescore/teams/{home,away}/runs` | replace, add | `= IntValue ?? existing` |
| `/liveData/linescore/{balls,strikes,outs}` | replace, add | `= IntValue ?? existing` — MLB emits both these and the currentPlay mirrors; linescore fires more often |

**The `?? existing` vs plain-assign split is load-bearing** — non-`int` payloads must not zero a score or count, but *must* null an optional ID/name. Copy the table exactly.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherScalarTests.cs`:

```csharp
using System.Text.Json;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherScalarTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    internal static PatchOperation Op(string op, string path, string? valueJson = null, string? from = null)
    {
        var value = valueJson is null
            ? (JsonElement?)null
            : JsonDocument.Parse(valueJson).RootElement.Clone();
        return new PatchOperation(op, path, value, from);
    }

    internal static LiveFeedData Patch(LiveFeedData feed, params PatchOperation[] ops) =>
        LiveFeedPatcher.Apply(ops, feed);

    [Fact]
    public void Apply_DoesNotMutateTheInputFeed()
    {
        var feed = BaseFeed();
        var patched = Patch(feed, Op("replace", "/metaData/timeStamp", "\"20260416_180010\""));

        Assert.Equal("20260416_180000", feed.TimeStamp);
        Assert.Equal("20260416_180010", patched.TimeStamp);
        Assert.NotSame(feed, patched);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("add")]
    public void TimeStamp_IsSetByReplaceAndAdd(string op)
    {
        Assert.Equal("t", Patch(BaseFeed(), Op(op, "/metaData/timeStamp", "\"t\"")).TimeStamp);
    }

    [Fact]
    public void AbstractGameState_KeepsPreviousValueWhenPayloadIsNotAString()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/gameData/status/abstractGameState", "5"));
        Assert.Equal("Live", patched.GameState);
    }

    [Fact]
    public void AbstractGameState_IsReplacedByStringPayload()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/gameData/status/abstractGameState", "\"Final\""));
        Assert.Equal("Final", patched.GameState);
    }

    [Fact]
    public void DetailedState_IsClearedByRemove()
    {
        Assert.Null(Patch(BaseFeed(), Op("remove", "/gameData/status/detailedState")).DetailedState);
    }

    [Fact]
    public void DetailedState_IsNulledByNonStringPayload()
    {
        Assert.Null(Patch(BaseFeed(), Op("replace", "/gameData/status/detailedState", "7")).DetailedState);
    }

    [Fact]
    public void TeamNamesAndIds_AreReplaced()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/gameData/teams/home/name", "\"New Home\""),
            Op("replace", "/gameData/teams/home/id", "999"),
            Op("replace", "/gameData/teams/away/name", "\"New Away\""),
            Op("replace", "/gameData/teams/away/id", "888"));

        Assert.Equal("New Home", patched.HomeTeam);
        Assert.Equal(999, patched.HomeTeamId);
        Assert.Equal("New Away", patched.AwayTeam);
        Assert.Equal(888, patched.AwayTeamId);
    }

    [Fact]
    public void TeamIds_KeepPreviousValueWhenPayloadIsNotNumeric()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/gameData/teams/home/id", "true"));
        Assert.Equal(222, patched.HomeTeamId);
    }

    [Fact]
    public void Matchup_UpdatesBatterAndPitcher()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/plays/currentPlay/matchup/batter/id", "10"),
            Op("replace", "/liveData/plays/currentPlay/matchup/batter/fullName", "\"New Batter\""),
            Op("add", "/liveData/plays/currentPlay/matchup/pitcher/id", "20"),
            Op("add", "/liveData/plays/currentPlay/matchup/pitcher/fullName", "\"New Pitcher\""));

        Assert.Equal(10, patched.CurrentBatterId);
        Assert.Equal("New Batter", patched.CurrentBatterName);
        Assert.Equal(20, patched.CurrentPitcherId);
        Assert.Equal("New Pitcher", patched.CurrentPitcherName);
    }

    [Fact]
    public void IsComplete_KeepsPreviousValueWhenPayloadIsNotBoolean()
    {
        var feed = BaseFeed();
        feed.IsPlayComplete = true;
        Assert.True(Patch(feed, Op("replace", "/liveData/plays/currentPlay/about/isComplete", "\"yes\"")).IsPlayComplete);
    }

    [Fact]
    public void IsComplete_IsReplacedByBooleanPayload()
    {
        Assert.True(Patch(BaseFeed(), Op("replace", "/liveData/plays/currentPlay/about/isComplete", "true")).IsPlayComplete);
    }

    [Fact]
    public void PlayResult_IsSetAndCleared()
    {
        var withResult = Patch(
            BaseFeed(),
            Op("add", "/liveData/plays/currentPlay/result/event", "\"Home Run\""),
            Op("add", "/liveData/plays/currentPlay/result/description", "\"blast\""));

        Assert.Equal("Home Run", withResult.LastPlayEvent);
        Assert.Equal("blast", withResult.LastPlayDescription);

        var cleared = Patch(
            withResult,
            Op("remove", "/liveData/plays/currentPlay/result/event"),
            Op("remove", "/liveData/plays/currentPlay/result/description"));

        Assert.Null(cleared.LastPlayEvent);
        Assert.Null(cleared.LastPlayDescription);
    }

    [Fact]
    public void Count_IsUpdatedFromCurrentPlay()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/plays/currentPlay/count/balls", "3"),
            Op("replace", "/liveData/plays/currentPlay/count/strikes", "2"),
            Op("replace", "/liveData/plays/currentPlay/count/outs", "1"));

        Assert.Equal(3, patched.Balls);
        Assert.Equal(2, patched.Strikes);
        Assert.Equal(1, patched.Outs);
    }

    [Fact]
    public void Count_IsAlsoUpdatedFromLinescoreMirrors()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/linescore/balls", "1"),
            Op("replace", "/liveData/linescore/strikes", "1"),
            Op("replace", "/liveData/linescore/outs", "2"));

        Assert.Equal(1, patched.Balls);
        Assert.Equal(1, patched.Strikes);
        Assert.Equal(2, patched.Outs);
    }

    [Fact]
    public void Count_KeepsPreviousValueWhenPayloadIsNotNumeric()
    {
        var feed = BaseFeed();
        feed.Balls = 2;
        Assert.Equal(2, Patch(feed, Op("replace", "/liveData/plays/currentPlay/count/balls", "null")).Balls);
    }

    [Fact]
    public void Linescore_UpdatesInningAndScores()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/linescore/currentInning", "7"),
            Op("replace", "/liveData/linescore/inningHalf", "\"Bottom\""),
            Op("replace", "/liveData/linescore/inningState", "\"Middle\""),
            Op("replace", "/liveData/linescore/teams/home/runs", "4"),
            Op("replace", "/liveData/linescore/teams/away/runs", "5"));

        Assert.Equal(7, patched.Inning);
        Assert.Equal("Bottom", patched.InningHalf);
        Assert.Equal("Middle", patched.InningState);
        Assert.Equal(4, patched.HomeScore);
        Assert.Equal(5, patched.AwayScore);
    }

    [Fact]
    public void Scores_KeepPreviousValueWhenPayloadIsNotNumeric()
    {
        var feed = BaseFeed();
        feed.HomeScore = 3;
        Assert.Equal(3, Patch(feed, Op("replace", "/liveData/linescore/teams/home/runs", "\"x\"")).HomeScore);
    }

    [Fact]
    public void IntValue_AcceptsNumbersTruncatedDoublesAndNumericStrings()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/linescore/currentInning", "\"9\""));
        Assert.Equal(9, patched.Inning);

        var truncated = Patch(
            BaseFeed(),
            Op("replace", "/liveData/linescore/currentInning", "9.7"));
        Assert.Equal(9, truncated.Inning);
    }

    [Fact]
    public void Apply_ProcessesOpsInOrder()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/linescore/currentInning", "3"),
            Op("replace", "/liveData/linescore/currentInning", "4"));

        Assert.Equal(4, patched.Inning);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedPatcherScalarTests`
Expected: compile error — `LiveFeedPatcher` does not exist.

- [ ] **Step 3: Write `src/OnDeck.Core/Utilities/LiveFeedPatcher.cs`**

```csharp
using System.Text.Json;
using OnDeck.Core.Models;

namespace OnDeck.Core.Utilities;

/// <summary>
/// Applies RFC 6902 JSON patches produced by MLB's <c>/feed/live/diffPatch</c> endpoint
/// directly to a <see cref="LiveFeedData"/> — no JSON object graph, no reserialize.
/// Port of <c>Utilities/LiveFeedPatcher.swift</c>.
///
/// Two-tier dispatch: registered (op, path) pairs mutate typed fields; any other op is
/// recorded via <see cref="UnknownPatchLogger"/> and silently skipped. Decorative paths
/// under <c>/currentPlay</c> outnumber modeled ones ~30:1, so reseed-on-unknown-under-
/// relevant-prefix would fire every cycle.
/// </summary>
public static partial class LiveFeedPatcher
{
    /// <summary>
    /// Applies <paramref name="ops"/> to a working copy of <paramref name="feed"/> and returns it.
    /// Partial state never escapes — callers get either the fully patched feed or, on any
    /// handler-internal error, nothing at all (there are none currently). Pass
    /// <paramref name="logger"/> to capture unhandled ops; when it is <c>null</c> the
    /// decorative-prefix scan is skipped too, since nothing would consume the result.
    /// </summary>
    public static LiveFeedData Apply(
        IReadOnlyList<PatchOperation> ops, LiveFeedData feed, UnknownPatchLogger? logger = null)
    {
        var working = feed.Clone();
        foreach (var op in ops) ApplyOne(op, working, logger);
        return working;
    }

    private static void ApplyOne(PatchOperation op, LiveFeedData feed, UnknownPatchLogger? logger)
    {
        var value = op.Value;

        // Registered scalar leaves (replace-only unless noted)
        switch (op.Op, op.Path)
        {
            // --- metaData
            case ("replace", "/metaData/timeStamp"):
            case ("add", "/metaData/timeStamp"):
                feed.TimeStamp = StringValue(value);
                return;

            // --- gameData/status
            case ("replace", "/gameData/status/abstractGameState"):
                if (StringValue(value) is { } gameState) feed.GameState = gameState;
                return;
            case ("replace", "/gameData/status/detailedState"):
            case ("add", "/gameData/status/detailedState"):
                feed.DetailedState = StringValue(value);
                return;
            case ("remove", "/gameData/status/detailedState"):
                feed.DetailedState = null;
                return;

            // --- gameData/teams (rare - name/id shouldn't change mid-game, but register defensively)
            case ("replace", "/gameData/teams/home/name"):
                if (StringValue(value) is { } homeName) feed.HomeTeam = homeName;
                return;
            case ("replace", "/gameData/teams/home/id"):
                if (IntValue(value) is { } homeId) feed.HomeTeamId = homeId;
                return;
            case ("replace", "/gameData/teams/away/name"):
                if (StringValue(value) is { } awayName) feed.AwayTeam = awayName;
                return;
            case ("replace", "/gameData/teams/away/id"):
                if (IntValue(value) is { } awayId) feed.AwayTeamId = awayId;
                return;

            // --- currentPlay / matchup
            case ("replace", "/liveData/plays/currentPlay/matchup/batter/id"):
            case ("add", "/liveData/plays/currentPlay/matchup/batter/id"):
                feed.CurrentBatterId = IntValue(value);
                return;
            case ("replace", "/liveData/plays/currentPlay/matchup/batter/fullName"):
            case ("add", "/liveData/plays/currentPlay/matchup/batter/fullName"):
                feed.CurrentBatterName = StringValue(value);
                return;
            case ("replace", "/liveData/plays/currentPlay/matchup/pitcher/id"):
            case ("add", "/liveData/plays/currentPlay/matchup/pitcher/id"):
                feed.CurrentPitcherId = IntValue(value);
                return;
            case ("replace", "/liveData/plays/currentPlay/matchup/pitcher/fullName"):
            case ("add", "/liveData/plays/currentPlay/matchup/pitcher/fullName"):
                feed.CurrentPitcherName = StringValue(value);
                return;

            // --- currentPlay / about
            case ("replace", "/liveData/plays/currentPlay/about/isComplete"):
            case ("add", "/liveData/plays/currentPlay/about/isComplete"):
                feed.IsPlayComplete = BoolValue(value) ?? feed.IsPlayComplete;
                return;

            // --- currentPlay / result
            case ("replace", "/liveData/plays/currentPlay/result/event"):
            case ("add", "/liveData/plays/currentPlay/result/event"):
                feed.LastPlayEvent = StringValue(value);
                return;
            case ("remove", "/liveData/plays/currentPlay/result/event"):
                feed.LastPlayEvent = null;
                return;
            case ("replace", "/liveData/plays/currentPlay/result/description"):
            case ("add", "/liveData/plays/currentPlay/result/description"):
                feed.LastPlayDescription = StringValue(value);
                return;
            case ("remove", "/liveData/plays/currentPlay/result/description"):
                feed.LastPlayDescription = null;
                return;

            // --- currentPlay / count
            case ("replace", "/liveData/plays/currentPlay/count/balls"):
            case ("add", "/liveData/plays/currentPlay/count/balls"):
                feed.Balls = IntValue(value) ?? feed.Balls;
                return;
            case ("replace", "/liveData/plays/currentPlay/count/strikes"):
            case ("add", "/liveData/plays/currentPlay/count/strikes"):
                feed.Strikes = IntValue(value) ?? feed.Strikes;
                return;
            case ("replace", "/liveData/plays/currentPlay/count/outs"):
            case ("add", "/liveData/plays/currentPlay/count/outs"):
                feed.Outs = IntValue(value) ?? feed.Outs;
                return;

            // --- linescore
            case ("replace", "/liveData/linescore/currentInning"):
            case ("add", "/liveData/linescore/currentInning"):
                feed.Inning = IntValue(value);
                return;
            case ("replace", "/liveData/linescore/inningHalf"):
            case ("add", "/liveData/linescore/inningHalf"):
                feed.InningHalf = StringValue(value);
                return;
            case ("replace", "/liveData/linescore/inningState"):
            case ("add", "/liveData/linescore/inningState"):
                feed.InningState = StringValue(value);
                return;
            case ("replace", "/liveData/linescore/teams/home/runs"):
            case ("add", "/liveData/linescore/teams/home/runs"):
                feed.HomeScore = IntValue(value) ?? feed.HomeScore;
                return;
            case ("replace", "/liveData/linescore/teams/away/runs"):
            case ("add", "/liveData/linescore/teams/away/runs"):
                feed.AwayScore = IntValue(value) ?? feed.AwayScore;
                return;

            // --- linescore mirrors of currentPlay count (MLB emits both; linescore fires more often)
            case ("replace", "/liveData/linescore/balls"):
            case ("add", "/liveData/linescore/balls"):
                feed.Balls = IntValue(value) ?? feed.Balls;
                return;
            case ("replace", "/liveData/linescore/strikes"):
            case ("add", "/liveData/linescore/strikes"):
                feed.Strikes = IntValue(value) ?? feed.Strikes;
                return;
            case ("replace", "/liveData/linescore/outs"):
            case ("add", "/liveData/linescore/outs"):
                feed.Outs = IntValue(value) ?? feed.Outs;
                return;
        }

        // Task 5 inserts the offense/runner cases above this line.
        // Task 6 and 7 insert the prefix-dispatched handlers here.
        // Task 8 inserts the decorative-prefix check and the unknown fallthrough here.
    }

    // MARK: - Helpers

    internal static int? IntValue(JsonElement? value)
    {
        if (value is not { } element) return null;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var n)
                ? n
                : element.TryGetDouble(out var d) ? (int)d : null,
            JsonValueKind.String => int.TryParse(element.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    internal static string? StringValue(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

    internal static bool? BoolValue(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedPatcherScalarTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/LiveFeedPatcher.cs windows/tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherScalarTests.cs
git commit -m "phase 2: LiveFeedPatcher scalar leaf dispatch"
```

---

## Task 5: LiveFeedPatcher — offense runners

**Files:**
- Modify: `src/OnDeck.Core/Utilities/LiveFeedPatcher.cs` (add cases to the `switch` in `ApplyOne`, plus two helpers)
- Create: `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherRunnerTests.cs`

**Spec:** `LiveFeedPatcher.swift:165-266, 536-551`

**Interfaces:**
- Consumes: everything from Task 4.
- Produces: no new public surface; adds private `static int? RunnerIdFromObject(JsonElement? value)` and `static bool IsBatterFromPath(string? from)`.

**Cases to add, in this order (order matters — `remove /offense/first` must not be shadowed):**

1. `("replace"|"add", "/liveData/linescore/offense/{first,second,third}/id")` → `RunnerOn* = IntValue(value)`
2. `("remove", "/liveData/linescore/offense/{slot}")` and `("remove", ".../{slot}/id")` → `RunnerOn* = null`
3. `("add", "/liveData/linescore/offense/{slot}")` → `RunnerOn* = RunnerIdFromObject(value)`. The server sends the whole runner object when a runner advances into an empty slot: `{"id":N,"fullName":"…","link":"…"}`.
4. `("copy", "/liveData/linescore/offense/{slot}")` → if `IsBatterFromPath(op.From)` then `RunnerOn* = feed.CurrentBatterId`, **else `feed.TimeStamp = null`** to force a reseed. At this point in the batch `CurrentBatterId` is still the batter who just reached base; the `replace` for the next batter arrives later. Verified against real MLB diffPatch output on 2026-04-18.
5. Decorative base-slot sub-paths — `("replace"|"add", ".../{slot}/{fullName,link}")` → explicit no-op `return`. These stay as real cases rather than joining the Task 8 decorative table, because `/liveData/linescore/offense/first` *is* modeled; only `fullName` and `link` under it are silenced.
6. `("move", "/liveData/linescore/offense/second")` with `From == ".../offense/first"` → second takes first's value, first cleared. `("move", ".../offense/third")` from first **or** second, likewise. A `move` whose `From` matches neither must **fall through** (`break`) to the later handlers, not return.

`IsBatterFromPath` is true when `from` ends with `/matchup/batter` **and** starts with `/liveData/plays/` — covering both `/currentPlay/matchup/batter` and `/allPlays/N/matchup/batter`.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherRunnerTests.cs`:

```csharp
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;
using static OnDeck.Core.Tests.Utilities.LiveFeedPatcherScalarTests;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherRunnerTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    [InlineData("third")]
    public void RunnerId_IsSetByReplaceAndAdd(string slot)
    {
        var replaced = Patch(BaseFeed(), Op("replace", $"/liveData/linescore/offense/{slot}/id", "77"));
        var added = Patch(BaseFeed(), Op("add", $"/liveData/linescore/offense/{slot}/id", "77"));

        Assert.Equal(77, RunnerAt(replaced, slot));
        Assert.Equal(77, RunnerAt(added, slot));
    }

    [Theory]
    [InlineData("first", "/liveData/linescore/offense/first")]
    [InlineData("first", "/liveData/linescore/offense/first/id")]
    [InlineData("second", "/liveData/linescore/offense/second")]
    [InlineData("second", "/liveData/linescore/offense/second/id")]
    [InlineData("third", "/liveData/linescore/offense/third")]
    [InlineData("third", "/liveData/linescore/offense/third/id")]
    public void RunnerId_IsClearedByRemoveOnSlotOrId(string slot, string path)
    {
        var feed = BaseFeed();
        SetRunner(feed, slot, 55);

        Assert.Null(RunnerAt(Patch(feed, Op("remove", path)), slot));
    }

    [Fact]
    public void WholeObjectAdd_SetsRunnerFromIdField()
    {
        // LiveFeedPatcherTests.swift:102-114
        var patched = Patch(BaseFeed(), Op(
            "add",
            "/liveData/linescore/offense/third",
            """{"id": 805367, "fullName": "Chase Meidroth", "link": "/api/v1/people/805367"}"""));

        Assert.Equal(805367, patched.RunnerOnThird);
    }

    [Fact]
    public void WholeObjectAdd_ClearsRunnerWhenValueHasNoId()
    {
        var feed = BaseFeed();
        feed.RunnerOnFirst = 12;

        Assert.Null(Patch(feed, Op("add", "/liveData/linescore/offense/first", """{"fullName": "x"}""")).RunnerOnFirst);
    }

    [Fact]
    public void Copy_FromAllPlaysBatterResolvesBatterReachesBase()
    {
        // Regression: Luis Robert Jr. singled 2026-04-18 19:06:31 UTC; the patcher had no copy
        // handler, so runnerOnFirst stayed nil and the UI showed an empty diamond.
        var feed = BaseFeed();
        feed.CurrentBatterId = 673357;
        feed.RunnerOnFirst = null;

        var patched = Patch(feed, Op(
            "copy", "/liveData/linescore/offense/first",
            from: "/liveData/plays/allPlays/21/matchup/batter"));

        Assert.Equal(673357, patched.RunnerOnFirst);
    }

    [Fact]
    public void Copy_FromCurrentPlayBatterAlsoResolves()
    {
        var feed = BaseFeed();
        feed.CurrentBatterId = 42;

        var patched = Patch(feed, Op(
            "copy", "/liveData/linescore/offense/second",
            from: "/liveData/plays/currentPlay/matchup/batter"));

        Assert.Equal(42, patched.RunnerOnSecond);
    }

    [Fact]
    public void Copy_FromNonBatterPathForcesReseedByNullingTimeStamp()
    {
        // LiveFeedPatcherTests.swift:88-100
        var feed = BaseFeed();
        feed.TimeStamp = "20260418_190600";

        var patched = Patch(feed, Op(
            "copy", "/liveData/linescore/offense/first",
            from: "/liveData/linescore/offense/second"));

        Assert.Null(patched.TimeStamp);
    }

    [Fact]
    public void Copy_WithNoFromForcesReseed()
    {
        var feed = BaseFeed();
        feed.TimeStamp = "20260418_190600";

        Assert.Null(Patch(feed, Op("copy", "/liveData/linescore/offense/third")).TimeStamp);
    }

    [Fact]
    public void Move_FirstToSecondTransfersIdAndClearsFirst()
    {
        // LiveFeedPatcherTests.swift:24-34
        var feed = BaseFeed();
        feed.RunnerOnFirst = 99;
        feed.RunnerOnSecond = null;

        var patched = Patch(feed, Op(
            "move", "/liveData/linescore/offense/second",
            from: "/liveData/linescore/offense/first"));

        Assert.Null(patched.RunnerOnFirst);
        Assert.Equal(99, patched.RunnerOnSecond);
    }

    [Fact]
    public void Move_FirstToThirdTransfersIdAndClearsFirst()
    {
        var feed = BaseFeed();
        feed.RunnerOnFirst = 99;

        var patched = Patch(feed, Op(
            "move", "/liveData/linescore/offense/third",
            from: "/liveData/linescore/offense/first"));

        Assert.Null(patched.RunnerOnFirst);
        Assert.Equal(99, patched.RunnerOnThird);
    }

    [Fact]
    public void Move_SecondToThirdTransfersIdAndClearsSecond()
    {
        var feed = BaseFeed();
        feed.RunnerOnSecond = 88;

        var patched = Patch(feed, Op(
            "move", "/liveData/linescore/offense/third",
            from: "/liveData/linescore/offense/second"));

        Assert.Null(patched.RunnerOnSecond);
        Assert.Equal(88, patched.RunnerOnThird);
    }

    [Fact]
    public void Move_WithUnrecognisedFromLeavesRunnersUntouched()
    {
        var feed = BaseFeed();
        feed.RunnerOnFirst = 1;
        feed.RunnerOnSecond = 2;

        var patched = Patch(feed, Op(
            "move", "/liveData/linescore/offense/second",
            from: "/liveData/linescore/offense/third"));

        Assert.Equal(1, patched.RunnerOnFirst);
        Assert.Equal(2, patched.RunnerOnSecond);
    }

    [Fact]
    public void DecorativeBaseSlotFields_AreSilentNoOps()
    {
        // LiveFeedPatcherTests.swift:142-151
        var feed = BaseFeed();
        var patched = Patch(
            feed,
            Op("replace", "/liveData/linescore/offense/first/fullName", "\"Luis Robert Jr.\""),
            Op("replace", "/liveData/linescore/offense/first/link", "\"/api/v1/people/673357\""),
            Op("add", "/liveData/linescore/offense/second/fullName", "\"x\""),
            Op("add", "/liveData/linescore/offense/third/link", "\"y\""));

        Assert.Equal(feed, patched);
    }

    private static int? RunnerAt(LiveFeedData feed, string slot) => slot switch
    {
        "first" => feed.RunnerOnFirst,
        "second" => feed.RunnerOnSecond,
        _ => feed.RunnerOnThird,
    };

    private static void SetRunner(LiveFeedData feed, string slot, int id)
    {
        switch (slot)
        {
            case "first": feed.RunnerOnFirst = id; break;
            case "second": feed.RunnerOnSecond = id; break;
            default: feed.RunnerOnThird = id; break;
        }
    }
}
```

Make the two helpers in `LiveFeedPatcherScalarTests` accessible by marking the class `public` and the `Op`/`Patch` members `internal static` (already specified in Task 4).

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedPatcherRunnerTests`
Expected: FAIL — runner ops fall through unhandled, so `RunnerOnFirst` stays null and the move/copy assertions fail.

- [ ] **Step 3: Add the runner cases to `ApplyOne`**

Insert immediately after the `("add", "/liveData/linescore/outs")` case and before the closing `}` of the `switch`:

```csharp
            // --- linescore / offense - scalar runner IDs
            case ("replace", "/liveData/linescore/offense/first/id"):
            case ("add", "/liveData/linescore/offense/first/id"):
                feed.RunnerOnFirst = IntValue(value);
                return;
            case ("replace", "/liveData/linescore/offense/second/id"):
            case ("add", "/liveData/linescore/offense/second/id"):
                feed.RunnerOnSecond = IntValue(value);
                return;
            case ("replace", "/liveData/linescore/offense/third/id"):
            case ("add", "/liveData/linescore/offense/third/id"):
                feed.RunnerOnThird = IntValue(value);
                return;
            case ("remove", "/liveData/linescore/offense/first"):
            case ("remove", "/liveData/linescore/offense/first/id"):
                feed.RunnerOnFirst = null;
                return;
            case ("remove", "/liveData/linescore/offense/second"):
            case ("remove", "/liveData/linescore/offense/second/id"):
                feed.RunnerOnSecond = null;
                return;
            case ("remove", "/liveData/linescore/offense/third"):
            case ("remove", "/liveData/linescore/offense/third/id"):
                feed.RunnerOnThird = null;
                return;

            // --- linescore / offense - whole-object add when a runner advances into an empty slot
            // Server sends: {"op":"add","path":"/liveData/linescore/offense/second",
            //                "value":{"id":N,"fullName":"...","link":"..."}}
            case ("add", "/liveData/linescore/offense/first"):
                feed.RunnerOnFirst = RunnerIdFromObject(value);
                return;
            case ("add", "/liveData/linescore/offense/second"):
                feed.RunnerOnSecond = RunnerIdFromObject(value);
                return;
            case ("add", "/liveData/linescore/offense/third"):
                feed.RunnerOnThird = RunnerIdFromObject(value);
                return;

            // --- linescore / offense - batter-reaches-base copy ops
            // Server sends: {"op":"copy","path":"/liveData/linescore/offense/first",
            //                "from":"/liveData/plays/allPlays/N/matchup/batter"}
            // At this point in the patch batch, CurrentBatterId is still the batter who just
            // reached base (the replace for the NEXT batter arrives in a later entry).
            // Verified against real MLB diffPatch output on 2026-04-18.
            case ("copy", "/liveData/linescore/offense/first"):
                if (IsBatterFromPath(op.From)) feed.RunnerOnFirst = feed.CurrentBatterId;
                else feed.TimeStamp = null;
                return;
            case ("copy", "/liveData/linescore/offense/second"):
                if (IsBatterFromPath(op.From)) feed.RunnerOnSecond = feed.CurrentBatterId;
                else feed.TimeStamp = null;
                return;
            case ("copy", "/liveData/linescore/offense/third"):
                if (IsBatterFromPath(op.From)) feed.RunnerOnThird = feed.CurrentBatterId;
                else feed.TimeStamp = null;
                return;

            // --- linescore / offense - decorative sub-paths on base slots (runner name/link).
            // Explicit no-op cases rather than entries in the Task 8 decorative table, because
            // /liveData/linescore/offense/first itself IS modeled (id + the copy/add handlers
            // above) — only fullName and link under those slots get silenced.
            case ("replace", "/liveData/linescore/offense/first/fullName"):
            case ("add", "/liveData/linescore/offense/first/fullName"):
            case ("replace", "/liveData/linescore/offense/first/link"):
            case ("add", "/liveData/linescore/offense/first/link"):
            case ("replace", "/liveData/linescore/offense/second/fullName"):
            case ("add", "/liveData/linescore/offense/second/fullName"):
            case ("replace", "/liveData/linescore/offense/second/link"):
            case ("add", "/liveData/linescore/offense/second/link"):
            case ("replace", "/liveData/linescore/offense/third/fullName"):
            case ("add", "/liveData/linescore/offense/third/fullName"):
            case ("replace", "/liveData/linescore/offense/third/link"):
            case ("add", "/liveData/linescore/offense/third/link"):
                return;

            // --- linescore / offense - typed runner advance (move ops)
            case ("move", "/liveData/linescore/offense/second"):
                if (op.From == "/liveData/linescore/offense/first")
                {
                    feed.RunnerOnSecond = feed.RunnerOnFirst;
                    feed.RunnerOnFirst = null;
                    return;
                }
                break;
            case ("move", "/liveData/linescore/offense/third"):
                if (op.From == "/liveData/linescore/offense/first")
                {
                    feed.RunnerOnThird = feed.RunnerOnFirst;
                    feed.RunnerOnFirst = null;
                    return;
                }
                if (op.From == "/liveData/linescore/offense/second")
                {
                    feed.RunnerOnThird = feed.RunnerOnSecond;
                    feed.RunnerOnSecond = null;
                    return;
                }
                break;
```

Add these helpers next to `IntValue`:

```csharp
    /// <summary>
    /// Extracts an <c>id</c> field from a JSON object value. Used by the whole-object
    /// <c>add</c> handlers for <c>/liveData/linescore/offense/{first|second|third}</c>,
    /// whose value shape is <c>{"id": N, "fullName": "...", "link": "..."}</c>.
    /// </summary>
    private static int? RunnerIdFromObject(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty("id", out var id)
            ? IntValue(id)
            : null;

    /// <summary>
    /// True if <paramref name="from"/> points at a batter field — either
    /// <c>/plays/currentPlay/matchup/batter</c> or <c>/plays/allPlays/N/matchup/batter</c>.
    /// Guards the copy-op shortcut that resolves "batter reaches base" from CurrentBatterId.
    /// </summary>
    private static bool IsBatterFromPath(string? from) =>
        from is not null
        && from.EndsWith("/matchup/batter", StringComparison.Ordinal)
        && from.StartsWith("/liveData/plays/", StringComparison.Ordinal);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedPatcherRunnerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/LiveFeedPatcher.cs windows/tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherRunnerTests.cs
git commit -m "phase 2: LiveFeedPatcher offense runner ops"
```

---

## Task 6: LiveFeedPatcher — boxscore arrays

**Files:**
- Modify: `src/OnDeck.Core/Utilities/LiveFeedPatcher.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherArrayTests.cs`

**Spec:** `LiveFeedPatcher.swift:273, 291-367`

**Interfaces:**
- Consumes: Task 5 output.
- Produces: private `static bool TryApplyBoxscoreArrayPatch(PatchOperation op, LiveFeedData feed)`, invoked from `ApplyOne` after the switch. Returns `true` when it handled the op.

**Behaviour for each of the four arrays (`/liveData/boxscore/teams/{home,away}/{battingOrder,pitchers}`):**
- `path == base` → replace the whole list from the array payload, mapping each element through `IntValue` and dropping non-numeric entries (`compactMap`). Handled for **any** op type.
- `path == base + "/-"` with op `add` and numeric value → append.
- `path == base + "/<index>"`:
  - `replace` → assign when `index < Count` **and** value is numeric; otherwise no change
  - `add` → insert at `index` when `index <= Count`, else append; only when value is numeric
  - `remove` → remove at `index` when `index < Count`
  - any other op → return `false` (fall through to later handlers)
- A non-numeric, non-`-` suffix falls through to the next base rather than being handled.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherArrayTests.cs`:

```csharp
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;
using static OnDeck.Core.Tests.Utilities.LiveFeedPatcherScalarTests;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherArrayTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    [Fact]
    public void BattingOrder_WholeArrayReplace()
    {
        // LiveFeedPatcherTests.swift:153-160
        var patched = Patch(BaseFeed(), Op("replace", "/liveData/boxscore/teams/home/battingOrder", "[10, 11, 12]"));
        Assert.Equal([10, 11, 12], patched.HomeBattingOrder);
    }

    [Fact]
    public void WholeArrayReplace_DropsNonNumericEntries()
    {
        var patched = Patch(BaseFeed(), Op(
            "replace", "/liveData/boxscore/teams/away/battingOrder", """[1, "2", null, 3.9, {}]"""));

        Assert.Equal([1, 2, 3], patched.AwayBattingOrder);
    }

    [Fact]
    public void Pitchers_AppendViaDashIndex()
    {
        // LiveFeedPatcherTests.swift:162-169
        var patched = Patch(BaseFeed(), Op("add", "/liveData/boxscore/teams/home/pitchers/-", "9999"));
        Assert.Equal([2, 9999], patched.HomePitchers);
    }

    [Fact]
    public void IndexedReplace_UpdatesInPlace()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/liveData/boxscore/teams/home/pitchers/0", "77"));
        Assert.Equal([77], patched.HomePitchers);
    }

    [Fact]
    public void IndexedReplace_IsIgnoredWhenOutOfRange()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/liveData/boxscore/teams/home/pitchers/5", "77"));
        Assert.Equal([2], patched.HomePitchers);
    }

    [Fact]
    public void IndexedAdd_InsertsAtPosition()
    {
        var feed = BaseFeed();
        feed.HomeBattingOrder = [1, 2, 3];

        var patched = Patch(feed, Op("add", "/liveData/boxscore/teams/home/battingOrder/1", "99"));
        Assert.Equal([1, 99, 2, 3], patched.HomeBattingOrder);
    }

    [Fact]
    public void IndexedAdd_AppendsWhenIndexEqualsCount()
    {
        var feed = BaseFeed();
        feed.HomeBattingOrder = [1, 2];

        var patched = Patch(feed, Op("add", "/liveData/boxscore/teams/home/battingOrder/2", "3"));
        Assert.Equal([1, 2, 3], patched.HomeBattingOrder);
    }

    [Fact]
    public void IndexedAdd_AppendsWhenIndexBeyondCount()
    {
        var feed = BaseFeed();
        feed.HomeBattingOrder = [1];

        var patched = Patch(feed, Op("add", "/liveData/boxscore/teams/home/battingOrder/9", "5"));
        Assert.Equal([1, 5], patched.HomeBattingOrder);
    }

    [Fact]
    public void IndexedRemove_DropsTheEntry()
    {
        var feed = BaseFeed();
        feed.AwayPitchers = [1, 2, 3];

        var patched = Patch(feed, Op("remove", "/liveData/boxscore/teams/away/pitchers/1"));
        Assert.Equal([1, 3], patched.AwayPitchers);
    }

    [Fact]
    public void IndexedRemove_IsIgnoredWhenOutOfRange()
    {
        var feed = BaseFeed();
        feed.AwayPitchers = [1];

        var patched = Patch(feed, Op("remove", "/liveData/boxscore/teams/away/pitchers/4"));
        Assert.Equal([1], patched.AwayPitchers);
    }

    [Fact]
    public void AllFourArraysAreAddressable()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/boxscore/teams/home/battingOrder", "[1]"),
            Op("replace", "/liveData/boxscore/teams/away/battingOrder", "[2]"),
            Op("replace", "/liveData/boxscore/teams/home/pitchers", "[3]"),
            Op("replace", "/liveData/boxscore/teams/away/pitchers", "[4]"));

        Assert.Equal([1], patched.HomeBattingOrder);
        Assert.Equal([2], patched.AwayBattingOrder);
        Assert.Equal([3], patched.HomePitchers);
        Assert.Equal([4], patched.AwayPitchers);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedPatcherArrayTests`
Expected: FAIL — array ops are unhandled, so lists keep their fixture values.

- [ ] **Step 3: Add the handler**

Replace the `// Task 6 and 7 insert the prefix-dispatched handlers here.` comment in `ApplyOne` with:

```csharp
        // Prefix-dispatched handlers (lineup arrays, player boxscore)
        if (TryApplyBoxscoreArrayPatch(op, feed)) return;
```

and add the method:

```csharp
    // MARK: - Boxscore array patches (batting orders, pitcher lists)

    private static bool TryApplyBoxscoreArrayPatch(PatchOperation op, LiveFeedData feed)
    {
        (string Side, string Key, Func<LiveFeedData, List<int>> Select)[] targets =
        [
            ("home", "battingOrder", f => f.HomeBattingOrder),
            ("away", "battingOrder", f => f.AwayBattingOrder),
            ("home", "pitchers", f => f.HomePitchers),
            ("away", "pitchers", f => f.AwayPitchers),
        ];

        foreach (var (side, key, select) in targets)
        {
            var basePath = $"/liveData/boxscore/teams/{side}/{key}";

            if (op.Path == basePath)
            {
                if (op.Value is { ValueKind: JsonValueKind.Array } array)
                {
                    var list = select(feed);
                    list.Clear();
                    foreach (var element in array.EnumerateArray())
                    {
                        if (IntValue(element) is { } n) list.Add(n);
                    }
                }
                return true;
            }

            if (!op.Path.StartsWith(basePath + "/", StringComparison.Ordinal)) continue;

            var suffix = op.Path[(basePath.Length + 1)..];

            if (suffix == "-")
            {
                if (op.Op == "add" && IntValue(op.Value) is { } appended) select(feed).Add(appended);
                return true;
            }

            if (!int.TryParse(suffix, out var index)) continue;

            var target = select(feed);
            switch (op.Op)
            {
                case "replace":
                    if (index < target.Count && IntValue(op.Value) is { } replacement) target[index] = replacement;
                    return true;
                case "add":
                    if (IntValue(op.Value) is { } inserted)
                    {
                        if (index <= target.Count) target.Insert(index, inserted);
                        else target.Add(inserted);
                    }
                    return true;
                case "remove":
                    if (index < target.Count) target.RemoveAt(index);
                    return true;
                default:
                    return false;
            }
        }

        return false;
    }
```

Note the Swift original returns `true` for `"-"` only when the value parses; the C# version returns `true` unconditionally for a `"-"` suffix, matching Swift's behaviour for `add` and treating a non-`add` `"-"` op as handled-and-ignored rather than logging it as unknown. Keep this — an unparseable append is not a missing handler.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedPatcherArrayTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/LiveFeedPatcher.cs windows/tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherArrayTests.cs
git commit -m "phase 2: LiveFeedPatcher boxscore array ops"
```

---

## Task 7: LiveFeedPatcher — player stats

**Files:**
- Modify: `src/OnDeck.Core/Utilities/LiveFeedPatcher.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherStatsTests.cs`

**Spec:** `LiveFeedPatcher.swift:274, 376-524`

**Interfaces:**
- Consumes: Task 6 output.
- Produces: private `static bool TryApplyPlayerStatsPatch(PatchOperation op, LiveFeedData feed)` plus decode/field helpers.

**Recognised shapes** under `/liveData/boxscore/teams/{home,away}/players/ID<n>`:
- suffix empty, op `add` → decode the whole player; store **only if** the value has a `stats` object carrying `batting` or `pitching`. A player dict without usable stats is *handled* (returns `true`) but stores nothing.
- suffix empty, op `remove` → drop the entry.
- suffix `/stats/batting` or `/stats/pitching` → replace that half from an object payload; on `remove` set it to null. The entry is created if absent and **always written back**, even when nothing changed.
- suffix `/stats/batting/<field>` or `/stats/pitching/<field>` → set one field; `remove` sets null. Unknown field names are ignored but still count as handled.
- Any other suffix (`/person`, `/position`, `/seasonStats`, …) → return `false`.
- A non-numeric ID → return `false`.

**Batting fields:** `atBats, hits, runs, doubles, triples, homeRuns, rbi, baseOnBalls, strikeOuts, stolenBases` — all `int?` via `IntValue`.
**Pitching fields:** `inningsPitched` (**string**, via `StringValue`), `hits, earnedRuns, strikeOuts, baseOnBalls, numberOfPitches` — `int?`.

- [ ] **Step 1: Write the failing test**

Create `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherStatsTests.cs`:

```csharp
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;
using static OnDeck.Core.Tests.Utilities.LiveFeedPatcherScalarTests;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherStatsTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    [Fact]
    public void FullPlayerAdd_StoresDecodedStats()
    {
        var patched = Patch(BaseFeed(), Op(
            "add", "/liveData/boxscore/teams/home/players/ID500",
            """{"person": {"id": 500}, "stats": {"batting": {"atBats": 3, "hits": 2}}}"""));

        Assert.Equal(3, patched.PlayerStats[500].Batting!.AtBats);
        Assert.Equal(2, patched.PlayerStats[500].Batting!.Hits);
        Assert.Null(patched.PlayerStats[500].Pitching);
    }

    [Fact]
    public void FullPlayerAdd_StoresNothingWhenStatsHaveNoBattingOrPitching()
    {
        var patched = Patch(BaseFeed(), Op(
            "add", "/liveData/boxscore/teams/home/players/ID501",
            """{"stats": {}}"""));

        Assert.False(patched.PlayerStats.ContainsKey(501));
    }

    [Fact]
    public void FullPlayerRemove_DropsTheEntry()
    {
        var patched = Patch(BaseFeed(), Op("remove", "/liveData/boxscore/teams/away/players/ID1"));
        Assert.False(patched.PlayerStats.ContainsKey(1));
    }

    [Fact]
    public void BattingSubtree_ReplacesTheWholeHalf()
    {
        var patched = Patch(BaseFeed(), Op(
            "replace", "/liveData/boxscore/teams/away/players/ID1/stats/batting",
            """{"atBats": 4, "hits": 3, "rbi": 1}"""));

        var batting = patched.PlayerStats[1].Batting!;
        Assert.Equal(4, batting.AtBats);
        Assert.Equal(3, batting.Hits);
        Assert.Equal(1, batting.Rbi);
    }

    [Fact]
    public void BattingSubtree_RemoveClearsIt()
    {
        var patched = Patch(BaseFeed(), Op("remove", "/liveData/boxscore/teams/away/players/ID1/stats/batting"));
        Assert.Null(patched.PlayerStats[1].Batting);
    }

    [Fact]
    public void PitchingSubtree_ReplacesTheWholeHalf()
    {
        var patched = Patch(BaseFeed(), Op(
            "replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching",
            """{"inningsPitched": "5.2", "strikeOuts": 8}"""));

        Assert.Equal("5.2", patched.PlayerStats[2].Pitching!.InningsPitched);
        Assert.Equal(8, patched.PlayerStats[2].Pitching!.StrikeOuts);
    }

    [Fact]
    public void StatsSubtree_CreatesEntryForUnknownPlayer()
    {
        var patched = Patch(BaseFeed(), Op(
            "add", "/liveData/boxscore/teams/home/players/ID777/stats/batting",
            """{"atBats": 1}"""));

        Assert.Equal(1, patched.PlayerStats[777].Batting!.AtBats);
    }

    [Theory]
    [InlineData("atBats")]
    [InlineData("hits")]
    [InlineData("runs")]
    [InlineData("doubles")]
    [InlineData("triples")]
    [InlineData("homeRuns")]
    [InlineData("rbi")]
    [InlineData("baseOnBalls")]
    [InlineData("strikeOuts")]
    [InlineData("stolenBases")]
    public void BattingField_IsSetIndividually(string field)
    {
        var patched = Patch(BaseFeed(), Op(
            "replace", $"/liveData/boxscore/teams/away/players/ID1/stats/batting/{field}", "6"));

        var batting = patched.PlayerStats[1].Batting!;
        var actual = field switch
        {
            "atBats" => batting.AtBats,
            "hits" => batting.Hits,
            "runs" => batting.Runs,
            "doubles" => batting.Doubles,
            "triples" => batting.Triples,
            "homeRuns" => batting.HomeRuns,
            "rbi" => batting.Rbi,
            "baseOnBalls" => batting.BaseOnBalls,
            "strikeOuts" => batting.StrikeOuts,
            _ => batting.StolenBases,
        };
        Assert.Equal(6, actual);
    }

    [Fact]
    public void BattingField_RemoveNullsIt()
    {
        var patched = Patch(BaseFeed(), Op(
            "remove", "/liveData/boxscore/teams/away/players/ID1/stats/batting/atBats"));

        Assert.Null(patched.PlayerStats[1].Batting!.AtBats);
    }

    [Fact]
    public void BattingField_UnknownNameIsIgnoredButHandled()
    {
        var feed = BaseFeed();
        var patched = Patch(feed, Op(
            "replace", "/liveData/boxscore/teams/away/players/ID1/stats/batting/leftOnBase", "4"));

        Assert.Equal(feed.PlayerStats[1].Batting, patched.PlayerStats[1].Batting);
    }

    [Fact]
    public void PitchingFields_AreSetIndividually()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/inningsPitched", "\"6.1\""),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/hits", "4"),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/earnedRuns", "2"),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/strikeOuts", "7"),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/baseOnBalls", "1"),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/numberOfPitches", "98"));

        var pitching = patched.PlayerStats[2].Pitching!;
        Assert.Equal("6.1", pitching.InningsPitched);
        Assert.Equal(4, pitching.Hits);
        Assert.Equal(2, pitching.EarnedRuns);
        Assert.Equal(7, pitching.StrikeOuts);
        Assert.Equal(1, pitching.BaseOnBalls);
        Assert.Equal(98, pitching.NumberOfPitches);
    }

    [Fact]
    public void PitchingField_RemoveNullsIt()
    {
        var patched = Patch(BaseFeed(), Op(
            "remove", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/inningsPitched"));

        Assert.Null(patched.PlayerStats[2].Pitching!.InningsPitched);
    }

    [Fact]
    public void ZeroInitCopyIntoModeledStatFieldIsSkippedSafely()
    {
        // LiveFeedPatcherTests.swift:43-53 — a copy op carries no value, so the field is
        // nulled rather than zeroed... and because the whole feed must stay untouched, the
        // patcher must treat it as handled without inventing data.
        var feed = BaseFeed();
        var patched = Patch(feed, Op(
            "copy", "/liveData/boxscore/teams/away/players/ID1/stats/batting/hits",
            from: "/liveData/plays/currentPlay/result/rbi"));

        Assert.Equal(feed, patched);
    }

    [Fact]
    public void UnmodeledPlayerSubtreeIsNotHandled()
    {
        var logger = new UnknownPatchLogger();
        LiveFeedPatcher.Apply(
            [Op("replace", "/liveData/boxscore/teams/away/players/ID1/person/fullName", "\"x\"")],
            BaseFeed(),
            logger);

        Assert.Contains(logger.Entries, e => e.Path.EndsWith("/person/fullName", StringComparison.Ordinal));
    }
}
```

**Note on `ZeroInitCopyIntoModeledStatFieldIsSkippedSafely`:** Swift's `applyBattingField` computes `n = (opType == "remove") ? nil : intValue(value)`, so a `copy` op with no value sets `hits = nil`. In the fixture `ID1` has `batting.hits == nil` already, so the feed is unchanged and the Swift assertion holds. Do **not** add special-casing for `copy` — the equality assertion passes because the field was already null.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedPatcherStatsTests`
Expected: FAIL — stats paths are unhandled.

- [ ] **Step 3: Add the handler**

After the `TryApplyBoxscoreArrayPatch` call in `ApplyOne`, add:

```csharp
        if (TryApplyPlayerStatsPatch(op, feed)) return;
```

and add the methods:

```csharp
    // MARK: - Player stats patches

    /// <summary>
    /// Recognises paths like
    /// <c>/liveData/boxscore/teams/&lt;side&gt;/players/ID&lt;n&gt;</c>,
    /// <c>…/ID&lt;n&gt;/stats/batting</c> and <c>…/ID&lt;n&gt;/stats/batting/&lt;field&gt;</c>
    /// (and the <c>/stats/pitching</c> equivalents).
    /// </summary>
    private static bool TryApplyPlayerStatsPatch(PatchOperation op, LiveFeedData feed)
    {
        string[] prefixes =
        [
            "/liveData/boxscore/teams/home/players/ID",
            "/liveData/boxscore/teams/away/players/ID",
        ];

        foreach (var prefix in prefixes)
        {
            if (!op.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var suffix = op.Path[prefix.Length..];
            var slash = suffix.IndexOf('/');
            var idText = slash >= 0 ? suffix[..slash] : suffix;
            var rest = slash >= 0 ? suffix[slash..] : "";      // rest starts with "/"

            if (!int.TryParse(idText, out var id)) return false;

            // Full player add (new player enters game)
            if (op.Op == "add" && rest.Length == 0)
            {
                if (DecodePlayerStats(op.Value) is { } stats) feed.PlayerStats[id] = stats;
                return true;
            }

            if (op.Op == "remove" && rest.Length == 0)
            {
                feed.PlayerStats.Remove(id);
                return true;
            }

            if (rest == "/stats/batting")
            {
                var entry = GetOrCreate(feed, id);
                if (op.Value is { ValueKind: JsonValueKind.Object } batting) entry.Batting = DecodeBatting(batting);
                else if (op.Op == "remove") entry.Batting = null;
                return true;
            }

            if (rest == "/stats/pitching")
            {
                var entry = GetOrCreate(feed, id);
                if (op.Value is { ValueKind: JsonValueKind.Object } pitching) entry.Pitching = DecodePitching(pitching);
                else if (op.Op == "remove") entry.Pitching = null;
                return true;
            }

            if (rest.StartsWith("/stats/batting/", StringComparison.Ordinal))
            {
                var entry = GetOrCreate(feed, id);
                entry.Batting ??= new PlayerBattingStats();
                ApplyBattingField(rest["/stats/batting/".Length..], op, entry.Batting);
                return true;
            }

            if (rest.StartsWith("/stats/pitching/", StringComparison.Ordinal))
            {
                var entry = GetOrCreate(feed, id);
                entry.Pitching ??= new PlayerPitchingStats();
                ApplyPitchingField(rest["/stats/pitching/".Length..], op, entry.Pitching);
                return true;
            }

            // Other player subtrees (person, position, seasonStats, ...) - not modeled
            return false;
        }

        return false;
    }

    private static PlayerGameStats GetOrCreate(LiveFeedData feed, int id)
    {
        if (!feed.PlayerStats.TryGetValue(id, out var entry))
        {
            entry = new PlayerGameStats();
            feed.PlayerStats[id] = entry;
        }
        return entry;
    }

    private static void ApplyBattingField(string field, PatchOperation op, PlayerBattingStats batting)
    {
        var n = op.Op == "remove" ? null : IntValue(op.Value);

        switch (field)
        {
            case "atBats": batting.AtBats = n; break;
            case "hits": batting.Hits = n; break;
            case "runs": batting.Runs = n; break;
            case "doubles": batting.Doubles = n; break;
            case "triples": batting.Triples = n; break;
            case "homeRuns": batting.HomeRuns = n; break;
            case "rbi": batting.Rbi = n; break;
            case "baseOnBalls": batting.BaseOnBalls = n; break;
            case "strikeOuts": batting.StrikeOuts = n; break;
            case "stolenBases": batting.StolenBases = n; break;
            default: break;   // Decorative batting field - ignored.
        }
    }

    private static void ApplyPitchingField(string field, PatchOperation op, PlayerPitchingStats pitching)
    {
        var isRemove = op.Op == "remove";

        switch (field)
        {
            case "inningsPitched": pitching.InningsPitched = isRemove ? null : StringValue(op.Value); break;
            case "hits": pitching.Hits = isRemove ? null : IntValue(op.Value); break;
            case "earnedRuns": pitching.EarnedRuns = isRemove ? null : IntValue(op.Value); break;
            case "strikeOuts": pitching.StrikeOuts = isRemove ? null : IntValue(op.Value); break;
            case "baseOnBalls": pitching.BaseOnBalls = isRemove ? null : IntValue(op.Value); break;
            case "numberOfPitches": pitching.NumberOfPitches = isRemove ? null : IntValue(op.Value); break;
            default: break;   // Decorative pitching field - ignored.
        }
    }

    private static PlayerGameStats? DecodePlayerStats(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } player) return null;
        if (!player.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object) return null;

        var batting = stats.TryGetProperty("batting", out var b) && b.ValueKind == JsonValueKind.Object
            ? DecodeBatting(b)
            : null;
        var pitching = stats.TryGetProperty("pitching", out var p) && p.ValueKind == JsonValueKind.Object
            ? DecodePitching(p)
            : null;

        if (batting is null && pitching is null) return null;
        return new PlayerGameStats { Batting = batting, Pitching = pitching };
    }

    private static PlayerBattingStats DecodeBatting(JsonElement d) => new()
    {
        AtBats = Field(d, "atBats"),
        Hits = Field(d, "hits"),
        Runs = Field(d, "runs"),
        Doubles = Field(d, "doubles"),
        Triples = Field(d, "triples"),
        HomeRuns = Field(d, "homeRuns"),
        Rbi = Field(d, "rbi"),
        BaseOnBalls = Field(d, "baseOnBalls"),
        StrikeOuts = Field(d, "strikeOuts"),
        StolenBases = Field(d, "stolenBases"),
    };

    private static PlayerPitchingStats DecodePitching(JsonElement d) => new()
    {
        InningsPitched = d.TryGetProperty("inningsPitched", out var ip) ? StringValue(ip) : null,
        Hits = Field(d, "hits"),
        EarnedRuns = Field(d, "earnedRuns"),
        StrikeOuts = Field(d, "strikeOuts"),
        BaseOnBalls = Field(d, "baseOnBalls"),
        NumberOfPitches = Field(d, "numberOfPitches"),
    };

    private static int? Field(JsonElement d, string name) =>
        d.TryGetProperty(name, out var element) ? IntValue(element) : null;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/OnDeck.Core.Tests --filter FullyQualifiedName~LiveFeedPatcherStatsTests`
Expected: PASS. (`UnmodeledPlayerSubtreeIsNotHandled` needs the Task 8 fallthrough; if it fails here, move it to Task 8's test file rather than weakening the handler.)

- [ ] **Step 5: Commit**

```bash
git add windows/src/OnDeck.Core/Utilities/LiveFeedPatcher.cs windows/tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherStatsTests.cs
git commit -m "phase 2: LiveFeedPatcher player stats ops"
```

---

## Task 8: Decorative prefixes, unknown fallthrough, and fixture parity

**Files:**
- Modify: `src/OnDeck.Core/Utilities/LiveFeedPatcher.cs`
- Modify: `tests/OnDeck.Core.Tests/Fixtures/LiveFeedPatcherFixtures.cs` (add the three patch fixtures)
- Create: `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherDecorativeTests.cs`
- Create: `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherParityTests.cs`

**Spec:** `LiveFeedPatcher.swift:276-287, 553-648`; `LiveFeedPatcherTests.swift` (all cases); `LiveFeedPatcherFixtures.swift:116-144`

**Interfaces:**
- Consumes: Task 7 output.
- Produces: private `static readonly string[] DecorativePrefixes` and `static bool IsDecorative(string path)`; fixtures gain
  `IReadOnlyList<PatchOperation> ScalarReplacesPatch`, `RunnerMoveFirstToSecondPatch`, `DecorativePatch`.

**The decorative table must be copied verbatim from `LiveFeedPatcher.swift:562-640`** — all 60 prefixes, same order. A path matches when it equals a prefix **or** starts with `prefix + "/"`.

**Ordering guarantee to preserve:** the specific `(op, path)` switch and both `tryApply*` handlers run *before* the decorative scan, so listing `/liveData/plays/currentPlay/about` as decorative cannot mask the modeled `/currentPlay/about/isComplete` handler. There is a test for exactly this.

**Logger gating:** when `logger` is `null`, return immediately without running `IsDecorative` — mirrors Swift's `#if DEBUG` gate, where the prefix scan exists only to keep the DEBUG-only logger quiet.

- [ ] **Step 1: Add the patch fixtures**

Append to `tests/OnDeck.Core.Tests/Fixtures/LiveFeedPatcherFixtures.cs`, inside the class:

```csharp
    /// <summary>Scalar-leaf patches — the 75% case.</summary>
    public static IReadOnlyList<PatchOperation> ScalarReplacesPatch { get; } = Parse("""
        [
          {"op": "replace", "path": "/metaData/timeStamp", "value": "20260416_180010"},
          {"op": "add", "path": "/liveData/plays/currentPlay/result/event", "value": "Home Run"},
          {"op": "add", "path": "/liveData/plays/currentPlay/result/description", "value": "Batter One hits a 2-run HR"},
          {"op": "replace", "path": "/liveData/plays/currentPlay/about/isComplete", "value": true},
          {"op": "replace", "path": "/liveData/plays/currentPlay/count/balls", "value": 3},
          {"op": "replace", "path": "/liveData/plays/currentPlay/count/strikes", "value": 2},
          {"op": "replace", "path": "/liveData/linescore/teams/away/runs", "value": 2},
          {"op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/atBats", "value": 1},
          {"op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/hits", "value": 1},
          {"op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/homeRuns", "value": 1},
          {"op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/rbi", "value": 2},
          {"op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/runs", "value": 1},
          {"op": "replace", "path": "/liveData/boxscore/teams/home/players/ID2/stats/pitching/hits", "value": 1},
          {"op": "replace", "path": "/liveData/boxscore/teams/home/players/ID2/stats/pitching/earnedRuns", "value": 2},
          {"op": "replace", "path": "/liveData/boxscore/teams/home/players/ID2/stats/pitching/numberOfPitches", "value": 6}
        ]
        """);

    /// <summary><c>move</c> on offense — runner advance from first to second.</summary>
    public static IReadOnlyList<PatchOperation> RunnerMoveFirstToSecondPatch { get; } = Parse("""
        [{"op": "move", "from": "/liveData/linescore/offense/first", "path": "/liveData/linescore/offense/second"}]
        """);

    /// <summary>Decorative path — must be skipped, not throw.</summary>
    public static IReadOnlyList<PatchOperation> DecorativePatch { get; } = Parse("""
        [{"op": "replace", "path": "/liveData/plays/currentPlay/playEvents/0/details/code", "value": "F"}]
        """);

    private static IReadOnlyList<PatchOperation> Parse(string json) =>
        PatchOperation.ParseArray(JsonDocument.Parse(json).RootElement.Clone());
```

and add `using System.Text.Json;` plus `using OnDeck.Core.Utilities;` at the top of the file.

- [ ] **Step 2: Write the failing tests**

Create `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherDecorativeTests.cs`:

```csharp
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;
using static OnDeck.Core.Tests.Utilities.LiveFeedPatcherScalarTests;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherDecorativeTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    [Theory]
    [InlineData("/liveData/plays/allPlays/99/playEvents/0")]
    [InlineData("/liveData/plays/currentPlay/playEvents/0/details/code")]
    [InlineData("/liveData/plays/currentPlay/matchup/batterHotColdZones")]
    [InlineData("/liveData/plays/currentPlay/runners")]
    [InlineData("/liveData/boxscore/teams/home/teamStats")]
    [InlineData("/liveData/linescore/defense")]
    [InlineData("/liveData/linescore/offense/onDeck")]
    [InlineData("/liveData/linescore/innings")]
    [InlineData("/metaData/gameEvents")]
    [InlineData("/gameData/weather")]
    [InlineData("/gameData/status/statusCode")]
    [InlineData("/gameData/players")]
    public void DecorativePaths_AreSkippedWithoutLogging(string path)
    {
        var logger = new UnknownPatchLogger();
        var feed = BaseFeed();

        var patched = LiveFeedPatcher.Apply([Op("replace", path, "\"x\"")], feed, logger);

        Assert.Equal(feed, patched);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void DecorativePrefix_MatchesExactPathAndChildrenOnly()
    {
        var logger = new UnknownPatchLogger();

        // "/gameData/weather" is decorative; "/gameData/weatherStation" is not a child of it.
        LiveFeedPatcher.Apply([Op("replace", "/gameData/weatherStation", "\"x\"")], BaseFeed(), logger);

        Assert.Single(logger.Entries);
    }

    [Fact]
    public void HandledPathUnderDecorativeSubtreeStillWins()
    {
        // LiveFeedPatcherTests.swift:127-140. /liveData/plays/currentPlay/about is a decorative
        // prefix, but /currentPlay/about/isComplete IS handled; the specific case must win.
        var feed = BaseFeed();
        feed.IsPlayComplete = false;

        var patched = LiveFeedPatcher.Apply(
            [Op("replace", "/liveData/plays/currentPlay/about/isComplete", "true")],
            feed,
            new UnknownPatchLogger());

        Assert.True(patched.IsPlayComplete);
    }

    [Fact]
    public void HandledCountAndResultPathsSurviveTheirDecorativePrefixes()
    {
        var patched = LiveFeedPatcher.Apply(
            [
                Op("replace", "/liveData/plays/currentPlay/count/balls", "2"),
                Op("add", "/liveData/plays/currentPlay/result/event", "\"Single\""),
            ],
            BaseFeed(),
            new UnknownPatchLogger());

        Assert.Equal(2, patched.Balls);
        Assert.Equal("Single", patched.LastPlayEvent);
    }

    [Fact]
    public void UnknownPathIsRecorded()
    {
        var logger = new UnknownPatchLogger();
        LiveFeedPatcher.Apply([Op("replace", "/liveData/somethingNew", "42")], BaseFeed(), logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("replace", entry.Op);
        Assert.Equal("/liveData/somethingNew", entry.Path);
        Assert.Equal("42", entry.ValuePreview);
    }

    [Fact]
    public void UnknownPathIsSkippedSilentlyWithoutALogger()
    {
        var feed = BaseFeed();
        var patched = LiveFeedPatcher.Apply([Op("replace", "/liveData/somethingNew", "42")], feed);

        Assert.Equal(feed, patched);
    }
}
```

Create `tests/OnDeck.Core.Tests/Utilities/LiveFeedPatcherParityTests.cs`:

```csharp
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

/// <summary>
/// Direct translations of the Swift in-process self-tests in
/// <c>Utilities/LiveFeedPatcherTests.swift</c>.
/// </summary>
public class LiveFeedPatcherParityTests
{
    [Fact]
    public void ScalarReplaceRoundTripEqualsDecoderOutput()
    {
        // LiveFeedPatcherTests.swift:14-22 — the anchor test: patching the base feed must land
        // exactly where decoding the equivalent JSON lands.
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);
        var patched = LiveFeedPatcher.Apply(LiveFeedPatcherFixtures.ScalarReplacesPatch, feed);

        var expected = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.AfterScalarReplacesJson);

        Assert.Equal(expected, patched);
    }

    [Fact]
    public void RunnerMoveFixtureTransfersIdAndClearsFirst()
    {
        // LiveFeedPatcherTests.swift:24-34
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);
        feed.RunnerOnFirst = 99;
        feed.RunnerOnSecond = null;

        var patched = LiveFeedPatcher.Apply(LiveFeedPatcherFixtures.RunnerMoveFirstToSecondPatch, feed);

        Assert.Null(patched.RunnerOnFirst);
        Assert.Equal(99, patched.RunnerOnSecond);
    }

    [Fact]
    public void DecorativeFixtureLeavesStateUntouched()
    {
        // LiveFeedPatcherTests.swift:36-41
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);
        var patched = LiveFeedPatcher.Apply(LiveFeedPatcherFixtures.DecorativePatch, feed);

        Assert.Equal(feed, patched);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/OnDeck.Core.Tests --filter "FullyQualifiedName~LiveFeedPatcherDecorativeTests|FullyQualifiedName~LiveFeedPatcherParityTests"`
Expected: FAIL — decorative paths get logged as unknown and `logger.Entries` is not empty.

- [ ] **Step 4: Add the decorative table and fallthrough**

Replace the `// Task 8 inserts …` comment at the end of `ApplyOne` with:

```csharp
        // Nothing downstream consumes the result when there is no logger, so skip the scan.
        if (logger is null) return;

        // Known-decorative paths — silently skip without polluting the unknown-patch log.
        // Paths that matter are caught by the specific cases above; everything matching a
        // prefix here is a tree we deliberately don't model (heatmaps, pitch-by-pitch,
        // historical plays, venue metadata, etc.). This check runs after the switch and both
        // tryApply* handlers, so silencing a prefix cannot mask a real handler.
        if (IsDecorative(op.Path)) return;

        // Fallthrough: unknown op - log and skip
        logger.Record(op.Op, op.Path, op.From, op.Value);
```

and add, at the end of the class:

```csharp
    /// <summary>
    /// Path prefixes for subtrees the app deliberately doesn't model. Any patch whose path
    /// matches one of these (exact, or followed by <c>/</c>) is dropped rather than logged as
    /// unknown. Copied verbatim from <c>LiveFeedPatcher.swift:562-640</c>.
    /// </summary>
    private static readonly string[] DecorativePrefixes =
    [
        // currentPlay subtrees — matchup.batter/pitcher id+fullName, about.isComplete,
        // result.event, result.description are all handled above.
        "/liveData/plays/currentPlay/matchup/batterHotColdZone",
        "/liveData/plays/currentPlay/matchup/batterHotColdZones",
        "/liveData/plays/currentPlay/matchup/batterHotColdZoneStats",
        "/liveData/plays/currentPlay/matchup/batSide",
        "/liveData/plays/currentPlay/matchup/pitchHand",
        "/liveData/plays/currentPlay/matchup/splits",
        "/liveData/plays/currentPlay/matchup/postOnFirst",
        "/liveData/plays/currentPlay/matchup/postOnSecond",
        "/liveData/plays/currentPlay/matchup/postOnThird",
        "/liveData/plays/currentPlay/matchup/batter/link",
        "/liveData/plays/currentPlay/matchup/pitcher/link",
        "/liveData/plays/currentPlay/playEvents",
        "/liveData/plays/currentPlay/runners",
        "/liveData/plays/currentPlay/runnerIndex",
        "/liveData/plays/currentPlay/pitchIndex",
        "/liveData/plays/currentPlay/actionIndex",
        "/liveData/plays/currentPlay/about",
        "/liveData/plays/currentPlay/result",
        "/liveData/plays/currentPlay/count",
        "/liveData/plays/currentPlay/atBatIndex",
        "/liveData/plays/currentPlay/playEndTime",
        // Historical plays — entire subtrees.
        "/liveData/plays/allPlays",
        "/liveData/plays/playsByInning",
        "/liveData/plays/scoringPlays",
        // Boxscore — battingOrder, pitchers arrays, and per-player stats handled via
        // TryApplyBoxscoreArrayPatch / TryApplyPlayerStatsPatch above.
        "/liveData/boxscore/topPerformers",
        "/liveData/boxscore/info",
        "/liveData/boxscore/pitchingNotes",
        "/liveData/boxscore/teams/home/teamStats",
        "/liveData/boxscore/teams/away/teamStats",
        "/liveData/boxscore/teams/home/battingTotals",
        "/liveData/boxscore/teams/away/battingTotals",
        "/liveData/boxscore/teams/home/pitchingTotals",
        "/liveData/boxscore/teams/away/pitchingTotals",
        "/liveData/boxscore/teams/home/note",
        "/liveData/boxscore/teams/away/note",
        "/liveData/boxscore/teams/home/info",
        "/liveData/boxscore/teams/away/info",
        "/liveData/boxscore/teams/home/team",
        "/liveData/boxscore/teams/away/team",
        // Linescore — teams/*/runs, currentInning, inningHalf, inningState, balls, strikes,
        // outs, and offense/{first,second,third} handled above.
        "/liveData/linescore/defense",
        "/liveData/linescore/offense/onDeck",
        "/liveData/linescore/offense/inHole",
        "/liveData/linescore/offense/batter",
        "/liveData/linescore/offense/pitcher",
        "/liveData/linescore/offense/team",
        "/liveData/linescore/offense/battingOrder",
        "/liveData/linescore/innings",
        "/liveData/linescore/teams/home/hits",
        "/liveData/linescore/teams/away/hits",
        "/liveData/linescore/teams/home/errors",
        "/liveData/linescore/teams/away/errors",
        "/liveData/linescore/teams/home/leftOnBase",
        "/liveData/linescore/teams/away/leftOnBase",
        "/liveData/linescore/isTopInning",
        "/liveData/linescore/currentInningOrdinal",
        // metaData event streams we don't consume.
        "/metaData/gameEvents",
        "/metaData/logicalEvents",
        // gameData — status.abstractGameState + status.detailedState handled above;
        // everything else here is admin/narrative metadata.
        "/gameData/absChallenges",
        "/gameData/moundVisits",
        "/gameData/review",
        "/gameData/weather",
        "/gameData/gameInfo",
        "/gameData/status/statusCode",
        "/gameData/status/reason",
        "/gameData/status/codedGameState",
        "/gameData/status/abstractGameCode",
        "/gameData/players",
    ];

    private static bool IsDecorative(string path)
    {
        foreach (var prefix in DecorativePrefixes)
        {
            if (path == prefix) return true;
            if (path.Length > prefix.Length
                && path[prefix.Length] == '/'
                && path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/OnDeck.Core.Tests --filter "FullyQualifiedName~LiveFeedPatcherDecorativeTests|FullyQualifiedName~LiveFeedPatcherParityTests"`
Expected: PASS.

- [ ] **Step 6: Run the whole suite and the publish check**

```bash
dotnet test
dotnet publish src/OnDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Expected: all tests pass; publish succeeds.

- [ ] **Step 7: Commit**

```bash
git add windows/
git commit -m "phase 2: LiveFeedPatcher decorative prefixes and unknown fallthrough"
```

---

## Done criteria

- `dotnet build` and `dotnet test` green from `windows/`; single-file publish still produces `OnDeck.App.exe`.
- Every `(op, path)` case in `LiveFeedPatcher.swift` has a C# counterpart, and all 60 decorative prefixes are present verbatim.
- All 15 Swift self-tests from `LiveFeedPatcherTests.swift` have an xunit equivalent (Tasks 1, 5, 6, 8).
- `OnDeck.Core` still has zero package references.
