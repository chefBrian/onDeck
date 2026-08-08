using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.App;

/// <summary>
/// Composes the real managers over a routed HTTP double and runs the body on a pumping
/// single-threaded context — the same serialization Core gets from the WPF Dispatcher.
/// Roster entries drive the Fantrax response, the per-name MLB search response and the
/// cached-roster blob at once, so a test declares its players once.
/// </summary>
internal sealed class OrchestratorHarness
{
    public static readonly DateTimeOffset Now = new(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);

    public const string LeagueUrl = "https://www.fantrax.com/fantasy/league/lg1/team/roster;teamId=t1";

    public const string LeagueUrlWithoutTeam = "https://www.fantrax.com/fantasy/league/lg1/standings";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly List<RosterEntry> _roster = [];
    private readonly List<Game> _games = [];

    public OrchestratorHarness(DateTimeOffset? now = null)
    {
        Time = new FakeTimeProvider(now ?? Now);
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Settings.RosterUrl = LeagueUrl;
    }

    public RoutingHttpMessageHandler Http { get; } = new();

    public FakeTimeProvider Time { get; }

    public InMemorySettingsStore Settings { get; } = new();

    public RecordingNotificationSink Sink { get; } = new();

    public CancellationTokenSource Lifetime { get; } = new();

    public List<FantraxTeam> Teams { get; } = [new("t1", "My Team"), new("t2", "Their Team")];

    /// <summary>Pre-populates <c>RosterCacheJson</c> so the ctor's cache load has players.</summary>
    public bool SeedCachedRoster { get; set; } = true;

    public RosterManager Roster { get; private set; } = null!;

    public ScheduleManager Schedule { get; private set; } = null!;

    public GameMonitor Monitor { get; private set; } = null!;

    public StateManager States { get; private set; } = null!;

    public AppOrchestrator App { get; private set; } = null!;

    public OrchestratorHarness AddPlayer(
        int mlbId, string name, string fantraxTeam = "LAD", string positions = "OF", int statusId = 1)
    {
        _roster.Add(new RosterEntry(mlbId, name, fantraxTeam, positions, statusId));
        return this;
    }

    public OrchestratorHarness AddGame(Game game)
    {
        _games.Add(game);
        return this;
    }

    public static Game GameOf(
        int id,
        DateTimeOffset start,
        string home = "Los Angeles Dodgers",
        string away = "San Francisco Giants",
        int homeTeamId = 119,
        int awayTeamId = 137,
        int? homeProbablePitcher = null,
        int? awayProbablePitcher = null,
        IReadOnlyList<int>? homeLineup = null,
        IReadOnlyList<int>? awayLineup = null,
        string? exclusiveCallSign = null) =>
        new(id, home, away, homeTeamId, awayTeamId, start, homeProbablePitcher, awayProbablePitcher,
            exclusiveCallSign is null ? [] : [new Game.Broadcast(exclusiveCallSign, true)],
            homeLineup ?? [], awayLineup ?? []);

    /// <summary>Builds the orchestrator. Must run on the context the test pumps.</summary>
    public AppOrchestrator Build()
    {
        Http.MapStatus("/feed/live", HttpStatusCode.ServiceUnavailable);
        Http.MapJson("fantrax.com/fxpa/req", (_, body) =>
            body.Contains("getStandings", StringComparison.Ordinal) ? StandingsJson() : RosterJson());
        Http.MapJson("/v1/people/search", (request, _) => SearchJson(request));
        Http.MapJson("/v1/schedule", (_, _) => ScheduleJson());

        if (SeedCachedRoster) Settings.RosterCacheJson = RosterCacheJson();

        var client = Http.CreateClient();
        var mlb = new MlbStatsApi(client, Time);
        var fantrax = new FantraxApi(client, Time);

        Roster = new RosterManager(fantrax, mlb, Settings, null, Time);
        Schedule = new ScheduleManager(mlb, Time);
        Monitor = new GameMonitor(mlb, Time);
        States = new StateManager();
        App = new AppOrchestrator(Roster, Schedule, Monitor, States, fantrax, Settings, Sink, Time);

        return App;
    }

    /// <summary>Builds and runs <paramref name="body"/> on a pumped single-threaded context.</summary>
    public void Run(Func<AppOrchestrator, Task> body) =>
        SingleThreadedContext.Run(async () =>
        {
            var app = Build();
            try
            {
                await body(app);
            }
            finally
            {
                Stop();
            }
        });

    /// <summary>As <see cref="Run"/>, with <c>StartAsync</c> already awaited and settled.</summary>
    public void RunStarted(Func<AppOrchestrator, Task> body) =>
        Run(async app =>
        {
            await app.StartAsync(Lifetime.Token);
            await SingleThreadedContext.Settle();
            await body(app);
        });

    /// <summary>
    /// Drives a game to Live/In Progress through the real feed-processing path, which is what
    /// flips <c>GameMonitor.IsLive</c> and fires <c>OnGameStart</c>.
    /// </summary>
    public void GoLive(int gamePk, string detailedState = "In Progress")
    {
        var game = _games.First(candidate => candidate.Id == gamePk);
        Monitor.ProcessFeed(
            new LiveFeedData { GameState = "Live", DetailedState = detailedState }, gamePk, game);
    }

    /// <summary>Puts a feed in <c>LatestFeeds</c> without polling.</summary>
    public LiveFeedData SeedFeed(int gamePk, Action<LiveFeedData>? configure = null)
    {
        var feed = new LiveFeedData
        {
            GameState = "Live",
            DetailedState = "In Progress",
            HomeTeam = "Dodgers",
            AwayTeam = "Giants",
            HomeTeamId = 119,
            AwayTeamId = 137,
        };

        configure?.Invoke(feed);
        Monitor.LatestFeeds[gamePk] = feed;
        return feed;
    }

    public Player PlayerNamed(string name) =>
        Roster.Players.First(player => player.Name == name);

    public void Stop()
    {
        Lifetime.Cancel();
        Monitor.StopMonitoring();
    }

    // MARK: - Canned responses

    private string RosterJson() => JsonSerializer.Serialize(
        new
        {
            responses = new[]
            {
                new
                {
                    data = new
                    {
                        tables = new[]
                        {
                            new
                            {
                                rows = _roster.Select(entry => new
                                {
                                    statusId = entry.StatusId,
                                    scorer = new
                                    {
                                        name = entry.Name,
                                        scorerId = $"*{entry.MlbId}*",
                                        posShortNames = entry.Positions,
                                        teamShortName = entry.FantraxTeam,
                                    },
                                }),
                            },
                        },
                    },
                },
            },
        },
        Json);

    private string StandingsJson() => JsonSerializer.Serialize(
        new
        {
            responses = new[]
            {
                new { data = new { rows = Teams.Select(team => new { teamId = team.Id, content = team.Name }) } },
            },
        },
        Json);

    private string SearchJson(HttpRequestMessage request)
    {
        var name = HttpUtility.ParseQueryString(request.RequestUri!.Query)["names"] ?? "";
        var match = _roster.FirstOrDefault(entry => NameCleaner.Clean(entry.Name) == name);
        if (match is null) return """{"people": []}""";

        return JsonSerializer.Serialize(
            new
            {
                people = new[]
                {
                    new
                    {
                        id = match.MlbId,
                        fullName = NameCleaner.Clean(match.Name),
                        currentTeam = new { id = 0, name = MlbTeamName(match) },
                    },
                },
            },
            Json);
    }

    private string ScheduleJson() => JsonSerializer.Serialize(
        new
        {
            dates = new[]
            {
                new
                {
                    games = _games.Select(game => new
                    {
                        gamePk = game.Id,
                        gameDate = game.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        status = new { abstractGameState = "Preview", detailedState = "Scheduled" },
                        teams = new
                        {
                            away = new
                            {
                                team = new { id = game.AwayTeamId, name = game.AwayTeam },
                                probablePitcher = game.AwayProbablePitcherId is { } away
                                    ? (object?)new { id = away }
                                    : null,
                            },
                            home = new
                            {
                                team = new { id = game.HomeTeamId, name = game.HomeTeam },
                                probablePitcher = game.HomeProbablePitcherId is { } home
                                    ? (object?)new { id = home }
                                    : null,
                            },
                        },
                        broadcasts = game.Broadcasts.Select(broadcast => new
                        {
                            callSign = broadcast.CallSign,
                            availability = new
                            {
                                availabilityCode = broadcast.IsExclusive ? "exclusive" : "free",
                            },
                        }),
                        lineups = new
                        {
                            homePlayers = game.HomeLineup.Select(id => new { id }),
                            awayPlayers = game.AwayLineup.Select(id => new { id }),
                        },
                    }),
                },
            },
        },
        Json);

    private string RosterCacheJson() => JsonSerializer.Serialize(
        _roster.Select(entry => new
        {
            id = entry.MlbId,
            name = NameCleaner.Clean(entry.Name),
            team = MlbTeamName(entry),
            positions = PositionsOf(entry).Select(position => position.ToString()),
            fantraxPositions = RawPositionsOf(entry),
            rosterStatus = ((RosterStatus)entry.StatusId).ToString(),
        }),
        Json);

    private static string MlbTeamName(RosterEntry entry) =>
        TeamMapping.MlbTeamName(entry.FantraxTeam) ?? entry.FantraxTeam;

    private static string[] RawPositionsOf(RosterEntry entry) =>
        entry.Positions.Length == 0
            ? []
            : [.. entry.Positions.Split(',').Select(position => position.Trim().ToUpperInvariant())];

    /// <summary>Mirrors <c>RosterManager.ParsePositions</c>: SP/RP/P are pitchers.</summary>
    private static HashSet<PlayerPosition> PositionsOf(RosterEntry entry)
    {
        string[] pitcherCodes = ["SP", "RP", "P"];
        var positions = RawPositionsOf(entry)
            .Select(position => pitcherCodes.Contains(position) ? PlayerPosition.Pitcher : PlayerPosition.Hitter)
            .ToHashSet();

        if (positions.Count == 0) positions.Add(PlayerPosition.Hitter);

        return positions;
    }

    private sealed record RosterEntry(int MlbId, string Name, string FantraxTeam, string Positions, int StatusId);
}
