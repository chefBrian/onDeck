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
}
