using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

/// <summary>
/// Direct translations of the Swift in-process self-tests in
/// <c>Utilities/LiveFeedPatcherTests.swift</c>.
/// </summary>
public class LiveFeedPatcherParityTests
{
    [Fact]
    public void ScalarReplaceRoundTripEqualsDecoderOutput()
    {
        // LiveFeedPatcherTests.swift:14-22 — the anchor test: patching the base feed must land
        // exactly where decoding the equivalent JSON lands.
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);
        var patched = LiveFeedPatcher.Apply(LiveFeedPatcherFixtures.ScalarReplacesPatch, feed);

        var expected = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.AfterScalarReplacesJson);

        Assert.Equal(expected, patched);
    }

    [Fact]
    public void RunnerMoveFixtureTransfersIdAndClearsFirst()
    {
        // LiveFeedPatcherTests.swift:24-34
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);
        feed.RunnerOnFirst = 99;
        feed.RunnerOnSecond = null;

        var patched = LiveFeedPatcher.Apply(LiveFeedPatcherFixtures.RunnerMoveFirstToSecondPatch, feed);

        Assert.Null(patched.RunnerOnFirst);
        Assert.Equal(99, patched.RunnerOnSecond);
    }

    [Fact]
    public void DecorativeFixtureLeavesStateUntouched()
    {
        // LiveFeedPatcherTests.swift:36-41
        var feed = LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);
        var patched = LiveFeedPatcher.Apply(LiveFeedPatcherFixtures.DecorativePatch, feed);

        Assert.Equal(feed, patched);
    }
}
