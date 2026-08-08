using System.Text.Json;
using OnDeck.Core.Utilities;

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
    /// for the <c>ScalarReplacesPatch</c> fixture.
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
}
