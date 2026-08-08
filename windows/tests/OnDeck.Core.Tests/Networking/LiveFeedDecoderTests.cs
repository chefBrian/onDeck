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
