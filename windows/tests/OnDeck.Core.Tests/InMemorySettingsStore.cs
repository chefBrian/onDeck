namespace OnDeck.Core.Tests;

public sealed class InMemorySettingsStore : ISettingsStore
{
    public string? RosterUrl { get; set; }
    public string? SelectedTeamId { get; set; }
    public bool HideBenchPlayers { get; set; }
    public bool AlwaysOpenPopout { get; set; }
    public bool NotifyBatting { get; set; } = true;
    public bool NotifyPitching { get; set; } = true;
    public bool NotifyAtBatResult { get; set; } = true;
    public bool NotifyPitchingResult { get; set; } = true;
    public bool NotifyNotInLineup { get; set; } = true;
    public string? RosterCacheJson { get; set; }
}
