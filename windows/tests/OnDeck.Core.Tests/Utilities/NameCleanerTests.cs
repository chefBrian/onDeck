using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class NameCleanerTests
{
    [Theory]
    [InlineData("Shohei Ohtani-P", "Shohei Ohtani")]
    [InlineData("Shohei Ohtani-H", "Shohei Ohtani")]
    [InlineData("Shohei Ohtani-DH", "Shohei Ohtani")]
    public void StripPositionSuffix_RemovesTrailingPositionCode(string input, string expected)
    {
        Assert.Equal(expected, NameCleaner.StripPositionSuffix(input));
    }

    [Theory]
    [InlineData("Mookie Betts")]
    [InlineData("Jean-Pierre Ramirez")]     // interior hyphen is not a suffix
    [InlineData("Shohei Ohtani-SP")]        // only P/H/DH are stripped
    [InlineData("Shohei Ohtani-P ")]        // anchored at end, trailing space defeats it
    public void StripPositionSuffix_LeavesOtherNamesUntouched(string input)
    {
        Assert.Equal(input, NameCleaner.StripPositionSuffix(input));
    }

    [Fact]
    public void StripPositionSuffix_RemovesOnlyOneSuffix()
    {
        Assert.Equal("Player-P", NameCleaner.StripPositionSuffix("Player-P-H"));
    }

    [Theory]
    [InlineData("T.J. Rumfield", "TJ Rumfield")]
    [InlineData("A.J. Puk", "AJ Puk")]
    [InlineData("Mookie Betts", "Mookie Betts")]
    public void StripPeriods_RemovesAllPeriods(string input, string expected)
    {
        Assert.Equal(expected, NameCleaner.StripPeriods(input));
    }

    [Theory]
    [InlineData("T.J. Rumfield-P", "TJ Rumfield")]
    [InlineData("A.J. Puk-DH", "AJ Puk")]
    [InlineData("Shohei Ohtani", "Shohei Ohtani")]
    public void Clean_StripsSuffixThenPeriods(string input, string expected)
    {
        Assert.Equal(expected, NameCleaner.Clean(input));
    }

    [Fact]
    public void Clean_HandlesEmptyString()
    {
        Assert.Equal("", NameCleaner.Clean(""));
    }
}
