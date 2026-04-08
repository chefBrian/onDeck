import Foundation

nonisolated enum NameCleaner {
    /// Strips position suffixes (-P, -H, -DH) from Fantrax player names.
    /// Example: "Shohei Ohtani-P" -> "Shohei Ohtani"
    static func stripPositionSuffix(_ name: String) -> String {
        name.replacingOccurrences(of: #"-(P|H|DH)$"#, with: "", options: .regularExpression)
    }

    /// Strips periods from names for MLB API search compatibility.
    /// Example: "T.J. Rumfield" -> "TJ Rumfield"
    static func stripPeriods(_ name: String) -> String {
        name.replacingOccurrences(of: ".", with: "")
    }

    /// Full cleanup pipeline for Fantrax names before MLB API lookup.
    static func clean(_ name: String) -> String {
        stripPeriods(stripPositionSuffix(name))
    }
}
