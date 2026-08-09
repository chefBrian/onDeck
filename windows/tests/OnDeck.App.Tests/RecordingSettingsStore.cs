using System.Runtime.CompilerServices;
using OnDeck.Core;

namespace OnDeck.App.Tests;

/// <summary>
/// An <see cref="ISettingsStore"/> that holds values in memory and records the name of every
/// property written, in order. That lets a test tell "wrote the same value again" from "did not
/// write at all" — the difference between a form that rewrites settings.json on every render and
/// one that doesn't.
/// </summary>
public sealed class RecordingSettingsStore : ISettingsStore
{
    private string? _rosterUrl;
    private string? _selectedTeamId;
    private bool _hideBenchPlayers;
    private bool _alwaysOpenPopout;
    private bool _notifyBatting = true;
    private bool _notifyPitching = true;
    private bool _notifyAtBatResult = true;
    private bool _notifyPitchingResult = true;
    private bool _notifyNotInLineup = true;
    private string? _rosterCacheJson;

    public List<string> Writes { get; } = [];

    public string? RosterUrl { get => _rosterUrl; set => Record(ref _rosterUrl, value); }

    public string? SelectedTeamId { get => _selectedTeamId; set => Record(ref _selectedTeamId, value); }

    public bool HideBenchPlayers { get => _hideBenchPlayers; set => Record(ref _hideBenchPlayers, value); }

    public bool AlwaysOpenPopout { get => _alwaysOpenPopout; set => Record(ref _alwaysOpenPopout, value); }

    public bool NotifyBatting { get => _notifyBatting; set => Record(ref _notifyBatting, value); }

    public bool NotifyPitching { get => _notifyPitching; set => Record(ref _notifyPitching, value); }

    public bool NotifyAtBatResult { get => _notifyAtBatResult; set => Record(ref _notifyAtBatResult, value); }

    public bool NotifyPitchingResult
    {
        get => _notifyPitchingResult;
        set => Record(ref _notifyPitchingResult, value);
    }

    public bool NotifyNotInLineup { get => _notifyNotInLineup; set => Record(ref _notifyNotInLineup, value); }

    public string? RosterCacheJson { get => _rosterCacheJson; set => Record(ref _rosterCacheJson, value); }

    private void Record<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        field = value;
        Writes.Add(property!);
    }
}
