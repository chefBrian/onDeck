using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using OnDeck.Core;

namespace OnDeck.App;

/// <summary>
/// <see cref="ISettingsStore"/> over a JSON file at <c>%APPDATA%\onDeck\settings.json</c> — the
/// Windows stand-in for the Mac's UserDefaults. Every setter rewrites the file through a temp
/// file and a move, so a crash mid-write cannot truncate it.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly Lock _gate = new();
    private Snapshot _values;

    public SettingsStore(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory();
        _path = Path.Combine(_directory, "settings.json");
        _values = Load();
    }

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "onDeck");

    public string? RosterUrl
    {
        get => _values.RosterUrl;
        set => Update(values => values with { RosterUrl = value });
    }

    public string? SelectedTeamId
    {
        get => _values.SelectedTeamId;
        set => Update(values => values with { SelectedTeamId = value });
    }

    public bool HideBenchPlayers
    {
        get => _values.HideBenchPlayers;
        set => Update(values => values with { HideBenchPlayers = value });
    }

    public bool AlwaysOpenPopout
    {
        get => _values.AlwaysOpenPopout;
        set => Update(values => values with { AlwaysOpenPopout = value });
    }

    public bool NotifyBatting
    {
        get => _values.NotifyBatting;
        set => Update(values => values with { NotifyBatting = value });
    }

    public bool NotifyPitching
    {
        get => _values.NotifyPitching;
        set => Update(values => values with { NotifyPitching = value });
    }

    public bool NotifyAtBatResult
    {
        get => _values.NotifyAtBatResult;
        set => Update(values => values with { NotifyAtBatResult = value });
    }

    public bool NotifyPitchingResult
    {
        get => _values.NotifyPitchingResult;
        set => Update(values => values with { NotifyPitchingResult = value });
    }

    public bool NotifyNotInLineup
    {
        get => _values.NotifyNotInLineup;
        set => Update(values => values with { NotifyNotInLineup = value });
    }

    public string? RosterCacheJson
    {
        get => _values.RosterCacheJson;
        set => Update(values => values with { RosterCacheJson = value });
    }

    /// <summary>
    /// The floating panel's last frame. Shell-only and deliberately absent from
    /// <see cref="ISettingsStore"/> — Core has no business knowing a window exists.
    /// </summary>
    public Rect? FloatingPanelFrame
    {
        get => _values.PanelFrame?.ToRect();
        set => Update(values => values with { PanelFrame = StoredRect.From(value) });
    }

    private void Update(Func<Snapshot, Snapshot> change)
    {
        lock (_gate)
        {
            _values = change(_values);
            Save();
        }
    }

    private Snapshot Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Snapshot();
            return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), Options)
                   ?? new Snapshot();
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file is not worth failing startup over.
            return new Snapshot();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_values, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing one write is better than taking the app down mid-session.
        }
    }

    /// <summary>The persisted shape. Defaults here are the defaults the app starts with.</summary>
    private sealed record Snapshot
    {
        public string? RosterUrl { get; init; }
        public string? SelectedTeamId { get; init; }
        public bool HideBenchPlayers { get; init; }
        public bool AlwaysOpenPopout { get; init; }
        public bool NotifyBatting { get; init; } = true;
        public bool NotifyPitching { get; init; } = true;
        public bool NotifyAtBatResult { get; init; } = true;
        public bool NotifyPitchingResult { get; init; } = true;
        public bool NotifyNotInLineup { get; init; } = true;
        public string? RosterCacheJson { get; init; }
        public StoredRect? PanelFrame { get; init; }
    }

    /// <summary><see cref="Rect"/> has no parameterless constructor, so it is persisted flat.</summary>
    private sealed record StoredRect(double X, double Y, double Width, double Height)
    {
        public Rect ToRect() => new(X, Y, Width, Height);

        public static StoredRect? From(Rect? rect) =>
            rect is { } value ? new StoredRect(value.X, value.Y, value.Width, value.Height) : null;
    }
}
