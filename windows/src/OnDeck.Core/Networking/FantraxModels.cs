namespace OnDeck.Core.Networking;

public sealed record FantraxTeam(string Id, string Name);

/// <summary><paramref name="StatusId"/>: 1=Active, 2=Reserve, 3=Inj Res, 9=Minors.</summary>
public sealed record FantraxPlayer(
    string Name,
    string TeamShortName,
    IReadOnlyList<string> Positions,
    int StatusId);

public enum FantraxErrorKind
{
    InvalidResponse,
    HttpError,
    NoTeamsFound,
}

public sealed class FantraxException(FantraxErrorKind kind, string message, int? statusCode = null)
    : Exception(message)
{
    public FantraxErrorKind Kind { get; } = kind;

    public int? StatusCode { get; } = statusCode;

    public static FantraxException InvalidResponse() =>
        new(FantraxErrorKind.InvalidResponse, "Invalid response from Fantrax");

    public static FantraxException HttpError(int statusCode) =>
        new(FantraxErrorKind.HttpError, $"Fantrax API returned HTTP {statusCode}", statusCode);

    public static FantraxException NoTeamsFound() =>
        new(FantraxErrorKind.NoTeamsFound, "No teams found in league");
}
