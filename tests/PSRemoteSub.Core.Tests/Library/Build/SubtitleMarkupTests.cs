using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public class SubtitleMarkupTests
{
    [Theory]
    [InlineData("~sl:1.64~What's going on?", new[] { "What's going on?" })]
    [InlineData("~sl:0:1.95~In Spanish: Hey Jorge, you here?~sl:0.15~In Spanish: Your mom says hi.",
                new[] { "In Spanish: Hey Jorge, you here?", "In Spanish: Your mom says hi." })]
    [InlineData("~sl:0:2.72~From what I can tell,~lr:0.1~they've each got a thing.",
                new[] { "From what I can tell,", "they've each got a thing." })]
    [InlineData("~COLOR_PURPLE~Mattia, good to see you, man!", new[] { "Mattia, good to see you, man!" })]
    [InlineData("~Tone~ [weak grunting]", new[] { "[weak grunting]" })]
    [InlineData("~t~Oh yeah?", new[] { "Oh yeah?" })]
    [InlineData("Hold ~INPUT_REVEAL_HUD~ to ask ~1~ to help.", new[] { "Hold ~INPUT_REVEAL_HUD~ to ask ~1~ to help." })]
    [InlineData("~sl:0~   ", new string[0])]
    public void Pieces_split_on_timing_and_drop_display_markup(string text, string[] expected) =>
        Assert.Equal(expected, SubtitleMarkup.Pieces(text));

    [Fact]
    public void KeptTokens_lists_button_and_placeholder_tokens_only() =>
        Assert.Equal(new[] { "~INPUT_SHOW_SWITCH_CHARACTER|PromptStyle=1~", "~1b~" },
            SubtitleMarkup.KeptTokens("~sl:0~Hold ~INPUT_SHOW_SWITCH_CHARACTER|PromptStyle=1~ near ~1b~ ~COLOR_RED~"));
}
