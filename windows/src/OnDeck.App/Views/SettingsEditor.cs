using System.ComponentModel;
using System.Runtime.CompilerServices;
using OnDeck.Core;

namespace OnDeck.App.Views;

/// <summary>
/// The settings form's two-way surface over <see cref="ISettingsStore"/>. Every setter writes
/// through and then raises <see cref="Changed"/>, which the window wires to
/// <c>AppOrchestrator.SettingsChanged()</c> — the C# analogue of the <c>didSet</c> on each stored
/// property in <c>App/AppState.swift:33-50</c>.
/// <para>
/// This exists so the checkboxes can bind directly (<c>IsChecked="{Binding NotifyBatting}"</c>)
/// and the window's code-behind carries no per-toggle handlers.
/// </para>
/// </summary>
public sealed class SettingsEditor(ISettingsStore settings) : INotifyPropertyChanged
{
    private readonly ISettingsStore _settings = settings;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after any write so the orchestrator can re-read and rebuild locally.</summary>
    public event Action? Changed;

    /// <summary>
    /// Trimmed on the way in and stored as null when cleared. The window commits on Enter, on
    /// focus loss and on close, so the same text arrives repeatedly — the equality guard below
    /// keeps that to one file write.
    /// </summary>
    public string RosterUrl
    {
        get => _settings.RosterUrl ?? "";
        set
        {
            var text = value.Trim();
            if (text == RosterUrl) return;

            _settings.RosterUrl = text.Length == 0 ? null : text;
            Notify();
        }
    }

    /// <summary>The empty string is the placeholder option, and clears the selection.</summary>
    public string SelectedTeamId
    {
        get => _settings.SelectedTeamId ?? "";
        set
        {
            if (value == SelectedTeamId) return;

            _settings.SelectedTeamId = value;
            Notify();
        }
    }

    public bool HideBenchPlayers
    {
        get => _settings.HideBenchPlayers;
        set
        {
            if (value == _settings.HideBenchPlayers) return;

            _settings.HideBenchPlayers = value;
            Notify();
        }
    }

    public bool AlwaysOpenPopout
    {
        get => _settings.AlwaysOpenPopout;
        set
        {
            if (value == _settings.AlwaysOpenPopout) return;

            _settings.AlwaysOpenPopout = value;
            Notify();
        }
    }

    public bool NotifyBatting
    {
        get => _settings.NotifyBatting;
        set
        {
            if (value == _settings.NotifyBatting) return;

            _settings.NotifyBatting = value;
            Notify();
        }
    }

    public bool NotifyPitching
    {
        get => _settings.NotifyPitching;
        set
        {
            if (value == _settings.NotifyPitching) return;

            _settings.NotifyPitching = value;
            Notify();
        }
    }

    public bool NotifyAtBatResult
    {
        get => _settings.NotifyAtBatResult;
        set
        {
            if (value == _settings.NotifyAtBatResult) return;

            _settings.NotifyAtBatResult = value;
            Notify();
        }
    }

    public bool NotifyPitchingResult
    {
        get => _settings.NotifyPitchingResult;
        set
        {
            if (value == _settings.NotifyPitchingResult) return;

            _settings.NotifyPitchingResult = value;
            Notify();
        }
    }

    public bool NotifyNotInLineup
    {
        get => _settings.NotifyNotInLineup;
        set
        {
            if (value == _settings.NotifyNotInLineup) return;

            _settings.NotifyNotInLineup = value;
            Notify();
        }
    }

    private void Notify([CallerMemberName] string? property = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        Changed?.Invoke();
    }
}
