using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Translation.Ai;
using Xunit;

namespace PSRemoteSub.Core.Tests.Translation.Ai;

public class AiPromptTests
{
    [Fact]
    public void PromptBuilder_NoContextNoGame()
    {
        var r = new AiTranslationRequest("Where is the key?", [], "English", "Thai", null);
        var (sys, user) = AiPromptBuilder.Build(r);
        Assert.Contains("from English to Thai", sys);
        Assert.DoesNotContain("The game is", sys);
        Assert.Equal("[translate] Where is the key?", user);
    }

    [Fact]
    public void PromptBuilder_ContextAndGame()
    {
        var r = new AiTranslationRequest("Go!", [new("Hi", "สวัสดี"), new("Run!", null)], "English", "Thai", "Yakuza");
        var (sys, user) = AiPromptBuilder.Build(r);
        Assert.Equal(["[previous] Hi => สวัสดี", "[previous] Run!", "[translate] Go!"], user.Split('\n'));
        Assert.Contains("The game is \"Yakuza\".", sys);
    }

    [Fact]
    public void Prompt_NoBrief_Unchanged()
    {
        var (sys, user) = AiPromptBuilder.Build(new AiTranslationRequest("Go!", [], "English", "Thai", "Yakuza"));
        const string expected =
            "You translate video game subtitles from English to Thai.\n" +
            "The text comes from OCR and may contain misread or missing characters; infer the intended words from context.\n" +
            "Write natural, conversational Thai that a player can read quickly. Keep it short.\n" +
            "Keep names of characters, places and items consistent with earlier lines; transliterate them rather than translating their meaning.\n" +
            "A subtitle may have several lines (one per speaker, or one sentence wrapped); translate every line and keep the same line breaks.\n" +
            "The game is \"Yakuza\".\n" +
            "Reply with only the translation of the subtitle after [translate], every line of it, with no quotes, notes or explanations.";
        Assert.Equal(expected, sys);
        Assert.Equal("[translate] Go!", user);
    }

    [Fact]
    public void Prompt_IncludesBrief()
    {
        var r = new AiTranslationRequest("Go!", [], "English", "Thai", "Yakuza", Brief: new GameBrief("STATIC-PART", "PERLINE-PART"));
        var (sys, _) = AiPromptBuilder.Build(r);
        var notes = "Game notes (follow them; Thai is the target style):\nSTATIC-PART\nPERLINE-PART";
        Assert.Contains(notes, sys);
        Assert.True(sys.IndexOf(notes, StringComparison.Ordinal) < sys.IndexOf("Reply with only", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptBuilder_BlankGameOmitted()
    {
        var (sys, _) = AiPromptBuilder.Build(new AiTranslationRequest("x", [], "English", "Thai", "  "));
        Assert.DoesNotContain("The game is", sys);
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("ja", "Japanese")]
    [InlineData("zh", "Simplified Chinese")]
    [InlineData("ko", "Korean")]
    public void LanguageNames_Source(string code, string name)
    {
        var s = new SourceLanguage(code, "", "", "");
        Assert.Equal(name, LanguageNames.ForSource(s));
    }

    [Theory]
    [InlineData("th", "Thai")]
    [InlineData("zh-CN", "Simplified Chinese")]
    [InlineData("en", "English")]
    [InlineData("vi", "vi")]
    public void LanguageNames_Target(string code, string name) => Assert.Equal(name, LanguageNames.ForTarget(code));
}

public class AiResponseCleanerTests
{
    [Theory]
    [InlineData("  \"ไปกันเถอะ\"  ", "ไปกันเถอะ")]
    [InlineData("```\nไปกันเถอะ\n```", "ไปกันเถอะ")]
    [InlineData("Translation: ไปกันเถอะ", "ไปกันเถอะ")]
    [InlineData("คำแปล: ไปกันเถอะ", "ไปกันเถอะ")]
    [InlineData("「ไป」", "ไป")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("ไป \"เร็ว\" นะ", "ไป \"เร็ว\" นะ")]
    public void Clean(string? raw, string? expected) => Assert.Equal(expected, AiResponseCleaner.Clean(raw));

    [Theory]
    [InlineData("<think>\nThe user wants Thai.\nOK.\n</think>\n\nไปกันเถอะ", "ไปกันเถอะ")]
    [InlineData("<THINK>hmm</Think>ไป", "ไป")]
    [InlineData("  <think></think>  \"ไป\"", "ไป")]
    public void Clean_StripsThinkBlock(string raw, string expected) => Assert.Equal(expected, AiResponseCleaner.Clean(raw));

    [Theory]
    [InlineData("<think>\nThe user wants Thai. Let me")]
    [InlineData("<think>a</think><think>b")]
    public void Clean_UnclosedThink_IsNull(string raw) => Assert.Null(AiResponseCleaner.Clean(raw));

    [Theory]
    [InlineData("`ไป`", "ไป")]
    [InlineData("\"Translation: ไป\"", "ไป")]
    [InlineData("Translation: \"ไป\"", "ไป")]
    [InlineData("คำแปล: 「ไป」", "ไป")]
    [InlineData("ไป `เร็ว` นะ", "ไป `เร็ว` นะ")]
    public void Clean_RepeatsPrefixAndQuotePasses(string raw, string expected) =>
        Assert.Equal(expected, AiResponseCleaner.Clean(raw));

    [Theory]
    [InlineData("ไปกันเถอะ", "Let's go", true)]
    [InlineData("", "", true)] // 0 <= 6*0 + 40
    public void IsPlausible_ShortAnswer(string answer, string source, bool expected) =>
        Assert.Equal(expected, AiResponseCleaner.IsPlausible(answer, source));

    [Fact]
    public void IsPlausible_Boundary()
    {
        const string source = "Hi"; // 6*2 + 40 = 52
        Assert.True(AiResponseCleaner.IsPlausible(new string('ก', 52), source));
        Assert.False(AiResponseCleaner.IsPlausible(new string('ก', 53), source));
    }

    // A real answer from 21 Nov: the model explained itself between the Thai lines.
    [Fact]
    public void HasCommentary_EnglishSentenceBetweenThaiLines()
    {
        const string source = "You're... you're in bad shape, Jay, we gotta go.\nLu, Lu, do not take me to the hospital, you hear me?";
        const string answer = "ลู ๆ อย่าพาฉันไปโรงพยาบาลนะ ได้ยินมั้ย\n"
            + "Wait — the line marked [translate] has two speakers, and it's a single block. Here's the translation with line breaks kept:\n"
            + "นายอาการหนักมากเลยเจย์ ต้องไปแล้ว\nลู ๆ อย่าพาฉันไปโรงพยาบาลนะ ได้ยินมั้ย";
        Assert.True(AiResponseCleaner.HasCommentary(answer, source));
    }

    [Theory]
    [InlineData("ไปกันเถอะ", "Let's go")]
    [InlineData("ไปที่ Vice City กัน", "Let's go to Vice City")] // names stay in English
    [InlineData("ไปกันเถอะ\nOK", "Let's go.\nOK")] // a short English line
    [InlineData("ป้ายเขียนว่า\nWelcome to the Leonida Keys", "The sign says\nWelcome to the Leonida Keys")] // kept from the source
    [InlineData("Let me know when you get here okay", "Let me know when you get here okay")] // all English: other guards decide
    public void HasCommentary_NormalAnswers_AreFine(string answer, string source) =>
        Assert.False(AiResponseCleaner.HasCommentary(answer, source));

    // More answers saved on 21 Nov, all for two-line subtitles without names: the model's reasoning came first.
    [Theory]
    [InlineData("Officer speaker unclear; the second line is a radio-style order from an officer, so formal register applies.\n"
        + "ให้ตายสิ เราต้องเพิ่มคนเข้ามา\nเจ้าหน้าที่ รายงานสถานะด้วย!",
        "Goddamn, we need more of our guys in here.\nOfficer on scene, confirm status!")]
    [InlineData("ว่าไงครับ ผมเป็นคนพูด, but this is a dialogue.\nOutput only Thai translation with line breaks.\n"
        + "คุยกับเราหน่อยสิ จ่า\nเร็วเข้า เราต้องหลุดจากกุญแจมือนี่",
        "Talk to us, Sergeant.\nC'mon, c'mon, we gotta get out of these cuffs.")]
    public void HasCommentary_ReasoningBeforeTheTranslation(string answer, string source) =>
        Assert.True(AiResponseCleaner.HasCommentary(answer, source));

    [Theory]
    [InlineData("[translate] ไปกันเถอะ", "Let's go")]
    [InlineData("ไปกันเถอะ\nNote: I kept the slang informal here", "Let's go")]
    public void HasCommentary_MarkersAndNotes(string answer, string source) =>
        Assert.True(AiResponseCleaner.HasCommentary(answer, source));

    [Fact]
    public void PromptBuilder_Names()
    {
        var r = new AiTranslationRequest("Lucia\nJason", [], "English", "Thai", null, AiRequestKind.Names);
        var (sys, user) = AiPromptBuilder.Build(r);
        Assert.Contains("Transliterate these character names into Thai script, one per line, in the same order.", sys);
        Assert.Equal("Lucia\nJason", user);
    }

    // With only the first line after the marker, models were unsure whether the next lines were part of it and wrote
    // their reasoning into the answer. The marker now says how many lines follow.
    [Fact]
    public void PromptBuilder_Subtitle_KeepsLineBreaks_ContextNewlineAsSlash()
    {
        var r = new AiTranslationRequest("Where?\nOut.", [new("Hi\nYo", "สวัสดี\nโย่")], "English", "Thai", null);
        var (sys, user) = AiPromptBuilder.Build(r);
        Assert.Contains("translate every line and keep the same line breaks", sys);
        Assert.Equal(["[previous] Hi / Yo => สวัสดี / โย่", "[translate] 2 lines:", "Where?", "Out."], user.Split('\n'));
    }

    [Fact]
    public void PromptBuilder_Subtitle_ThreeLines_CarriageReturnsAndBlankLinesIgnored()
    {
        var r = new AiTranslationRequest("A\r\nB\n\nC", [], "English", "Thai", null);
        Assert.Equal(["[translate] 3 lines:", "A", "B", "C"], AiPromptBuilder.Build(r).User.Split('\n'));
    }
}
