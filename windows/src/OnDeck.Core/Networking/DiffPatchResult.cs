using OnDeck.Core.Utilities;

namespace OnDeck.Core.Networking;

/// <summary>Result of a diffPatch request. Port of <c>DiffPatchResult</c> in MLBStatsAPI.swift.</summary>
public abstract record DiffPatchResult
{
    private DiffPatchResult() { }

    public sealed record NoChanges : DiffPatchResult;

    public sealed record Patches(IReadOnlyList<PatchOperation> Operations) : DiffPatchResult;

    /// <summary>The API returned a full feed object instead of patches.</summary>
    public sealed record FullUpdate(byte[] Json) : DiffPatchResult;
}
