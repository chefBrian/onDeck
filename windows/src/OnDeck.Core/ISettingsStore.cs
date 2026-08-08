namespace OnDeck.Core;

/// <summary>
/// The complete persisted surface Core reads, implemented by the shell. Swift keys are in
/// comments so the two codebases stay cross-referenceable. Floating-panel frame and
/// launch-at-login are shell-only and deliberately absent.
/// </summary>
public interface ISettingsStore
{
    string? RosterUrl { get; set; }             // rosterURL
    string? SelectedTeamId { get; set; }        // selectedTeamID
    bool HideBenchPlayers { get; set; }         // hideBenchPlayers
    bool AlwaysOpenPopout { get; set; }         // alwaysOpenPopout
    bool NotifyBatting { get; set; }            // notifyBatting, default true
    bool NotifyPitching { get; set; }           // notifyPitching, default true
    bool NotifyAtBatResult { get; set; }        // notifyAtBatResult, default true
    bool NotifyPitchingResult { get; set; }     // notifyPitchingResult, default true
    bool NotifyNotInLineup { get; set; }        // notifyNotInLineup, default true
    string? RosterCacheJson { get; set; }       // RosterManager cache blob
}
