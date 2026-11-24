using PSRemoteSub.Core.Pipeline;

namespace PSRemoteSub.Core.Tests.Pipeline;

public class SpeakerPaletteTests
{
    [Fact]
    public void For_SameNameSameColour_IgnoresCase() =>
        Assert.Equal(SpeakerPalette.For("Lucia"), SpeakerPalette.For("LUCIA"));

    [Fact]
    public void Colours_AreNeverWhiteAndAreHex() =>
        Assert.All(SpeakerPalette.Colors, c =>
        {
            Assert.Matches("^#[0-9A-F]{6}$", c);
            Assert.NotEqual("#FFFFFF", c);
        });

    [Fact]
    public void For_IsOneOfThePalette() => Assert.Contains(SpeakerPalette.For("Jason"), SpeakerPalette.Colors);

    [Fact]
    public void After_GivesTheNextColour() =>
        Assert.NotEqual(SpeakerPalette.For("Lucia"), SpeakerPalette.After(SpeakerPalette.For("Lucia")));

    [Theory]
    [InlineData(null)]
    [InlineData("#FFFFFF")]
    [InlineData("#F9FBFF")] // what was learned for real names: white with a faint tint
    [InlineData("#DAF6FD")]
    [InlineData("#EFFBFE")]
    [InlineData("#C8C8C8")] // grey
    public void IsMissing_WhiteGreyAndNearWhite(string? color) => Assert.True(SpeakerPalette.IsMissing(color));

    [Theory]
    [InlineData("#E070C0")]
    [InlineData("#F7F3A3")] // pale yellow still reads as yellow
    [InlineData("#7FD7FF")]
    public void IsMissing_RealColours_False(string color) => Assert.False(SpeakerPalette.IsMissing(color));
}
