using System.Text.RegularExpressions;

namespace OnDeck.Core.Utilities;

/// <summary>Port of <c>Utilities/NameCleaner.swift</c>.</summary>
public static partial class NameCleaner
{
    [GeneratedRegex(@"-(P|H|DH)$")]
    private static partial Regex PositionSuffixRegex();

    /// <summary>
    /// Strips position suffixes (-P, -H, -DH) from Fantrax player names.
    /// Example: "Shohei Ohtani-P" -> "Shohei Ohtani".
    /// </summary>
    public static string StripPositionSuffix(string name) =>
        PositionSuffixRegex().Replace(name, string.Empty);

    /// <summary>
    /// Strips periods from names for MLB API search compatibility.
    /// Example: "T.J. Rumfield" -> "TJ Rumfield".
    /// </summary>
    public static string StripPeriods(string name) => name.Replace(".", string.Empty);

    /// <summary>Full cleanup pipeline for Fantrax names before MLB API lookup.</summary>
    public static string Clean(string name) => StripPeriods(StripPositionSuffix(name));
}
