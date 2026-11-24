using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Text;
using PSRemoteSub.Windows.Imaging;
using PSRemoteSub.Windows.Ocr;

namespace PSRemoteSub.Windows.Tests;

public class WindowsOcrEngineTests
{
    [OcrFact]
    public async Task Recognizes_TwoLines_TopToBottom()
    {
        var lines = await new WindowsOcrEngine().RecognizeAsync(
            await WinRtImageFileWriter.LoadPngAsync(Fixture("ocr/en-two-lines.png")), "en-US", default);
        Assert.Equal(["We have to leave", "before sunset."], lines.Select(l => l.Text));
        Assert.True(lines[0].Bounds.Y < lines[1].Bounds.Y);
    }

    [OcrFact]
    public async Task Recognizes_WhiteOnScene_RawFrame()
    {
        var lines = await new WindowsOcrEngine().RecognizeAsync(
            await WinRtImageFileWriter.LoadPngAsync(Fixture("ocr/en-white-on-scene.png")), "en-US", default);
        string joined = string.Join(" ", lines.Select(l => l.Text));
        Assert.Contains("Where is the key", joined);
    }

    [OcrFact]
    public void IsAvailable_enUS_True_xxXX_False()
    {
        var engine = new WindowsOcrEngine();
        Assert.True(engine.IsAvailable("en-US"));
        Assert.False(engine.IsAvailable("xx-XX"));
    }

    [OcrFact]
    public void Invalidate_ForgetsCachedResult_AndLooksAgain()
    {
        var engine = new WindowsOcrEngine();
        Assert.False(engine.IsAvailable("xx-XX"));
        Assert.True(engine.IsAvailable("en-US"));

        engine.Invalidate("xx-XX");
        engine.Invalidate("en-US");
        engine.Invalidate("never-seen"); // not cached: no-op

        Assert.False(engine.IsAvailable("xx-XX"));
        Assert.True(engine.IsAvailable("en-US"));
    }

    [OcrFact]
    public async Task UnavailableLanguage_Throws_InvalidOperation()
    {
        var frame = await WinRtImageFileWriter.LoadPngAsync(Fixture("ocr/en-two-lines.png"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WindowsOcrEngine().RecognizeAsync(frame, "xx-XX", default));
    }

    // Real PS Remote Play screenshots (GTA 6), cropped to what a zone's OCR box covers: see make-ocr-fixtures.ps1.

    [OcrFact]
    public async Task PhoneMoses_OneReadPerBubble_MosesIsTheSender()
    {
        var reads = await SplitFixtureAsync("ocr/phone-moses.png");

        // The photo at the top reads as nothing.
        Assert.Equal(4, reads.Count);
        Assert.Equal((null, "Jason was added to the group"), (reads[0].Name, reads[0].Text));
        Assert.Null(reads[1].Name);
        Assert.StartsWith("Okay. This is Jason, by the wa", reads[1].Text); // OCR reads "way." as "waV"
        Assert.EndsWith("He's cool, don't worry.", reads[1].Text);
        Assert.Equal(("Moses", "If you vouching for him, we all good."), (reads[2].Name, reads[2].Text));
        Assert.Equal((null, "Thanks."), (reads[3].Name, reads[3].Text));
    }

    [OcrFact]
    public async Task PhoneLinda_NewMessageIsNoName_OneReadPerBubble()
    {
        var reads = await SplitFixtureAsync("ocr/phone-linda.png");

        Assert.All(reads, r => Assert.Null(r.Name));
        // The centred "New message" line over the last bubble is its own part (or dropped), never part of a bubble.
        var bubbles = reads.Where(r => r.Text != "New message").ToList();
        Assert.All(bubbles, r => Assert.DoesNotContain("New message", r.Text));
        Assert.Equal(4, bubbles.Count);
        Assert.EndsWith("because of her. L.", bubbles[0].Text); // the cut-off top line may join this one
        Assert.Equal("MORE was a typo. I of course meant ANY trouble. L.", bubbles[1].Text);
        Assert.Equal("Yeah I don't need your opinion thanks.", bubbles[2].Text);
        Assert.StartsWith("Oh and if you're heading into Port Gellhorn", bubbles[3].Text);
        Assert.EndsWith("almost as low our night manager's. L.", bubbles[3].Text);
    }

    [OcrFact]
    public async Task DialogueTwoSpeakers_InBubblesMode_OneReadPerSpeaker()
    {
        var reads = await SplitFixtureAsync("ocr/dialogue-two-speakers.png");

        Assert.Equal(
            [("Cop", "Talk to us, Sergeant... Sergeant?"), ("Jason", "C'mon, c'mon, we gotta get out of these cuffs.")],
            reads.Select(r => (r.Name, r.Text)));
    }

    /// <summary>As a Bubbles zone reads it: the Normal filter, real OCR, then <see cref="BubbleSplitter"/>.</summary>
    private static async Task<IReadOnlyList<SpeakerRead>> SplitFixtureAsync(string rel)
    {
        var engine = new WindowsOcrEngine();
        var processed = Preprocessor.Apply(
            await WinRtImageFileWriter.LoadPngAsync(Fixture(rel)), PreprocessFilter.Normal, engine.MaxImageDimension);
        var lines = await engine.RecognizeAsync(processed, "en-US", default);
        return BubbleSplitter.Split(lines, SourceLanguages.Get("en"));
    }

    private static string Fixture(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PSRemoteSub.sln")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("PSRemoteSub.sln not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "tests", "fixtures", rel);
    }
}
