namespace OnDeck.Core.Tests;

/// <summary>
/// Records an ordered call log. <see cref="DuringNotify"/> runs at the await point inside
/// every <c>Notify*</c> method, which is where the race-guard tests mutate player state.
/// </summary>
internal sealed class RecordingNotificationSink : INotificationSink
{
    public List<string> Calls { get; } = [];

    public Func<Task>? DuringNotify { get; set; }

    public async Task NotifyBattingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl)
    {
        Calls.Add($"batting:{playerId}:{gamePk}:{game}:{inning}:{streamUrl}");
        await RunHook();
    }

    public async Task NotifyPitchingAsync(
        string playerName, int playerId, int gamePk, string game, string inning, Uri? streamUrl)
    {
        Calls.Add($"pitching:{playerId}:{gamePk}:{game}:{inning}:{streamUrl}");
        await RunHook();
    }

    public async Task NotifyAtBatResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl)
    {
        Calls.Add($"atBatResult:{playerId}:{description}");
        await RunHook();
    }

    public async Task NotifyPitchingResultAsync(
        string playerName, int playerId, string description, Uri? streamUrl)
    {
        Calls.Add($"pitchingResult:{playerId}:{description}");
        await RunHook();
    }

    public async Task NotifyNotInLineupAsync(
        string playerName, int playerId, int gamePk, string game, Uri? fantraxUrl)
    {
        Calls.Add($"notInLineup:{playerId}:{gamePk}:{game}:{fantraxUrl}");
        await RunHook();
    }

    public void PurgeBatting(int gamePk, int playerId) => Calls.Add($"purgeBatting:{playerId}:{gamePk}");

    public void PurgePitching(int gamePk, int playerId) => Calls.Add($"purgePitching:{playerId}:{gamePk}");

    public Task PurgeNotInLineupAsync(int gamePk)
    {
        Calls.Add($"purgeNotInLineup:{gamePk}");
        return Task.CompletedTask;
    }

    public Task PurgeAllAsync()
    {
        Calls.Add("purgeAll");
        return Task.CompletedTask;
    }

    private Task RunHook() => DuringNotify?.Invoke() ?? Task.CompletedTask;
}
