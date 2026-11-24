using PSRemoteSub.MacOS.Imaging;
using PSRemoteSub.MacOS.Ocr;

namespace PSRemoteSub.MacOS.Tests;

public class VisionOcrEngineTests
{
    [MacFact]
    public async Task Recognizes_TwoLines_TopToBottom()
    {
        var lines = await new VisionOcrEngine().RecognizeAsync(
            ImageIoFileWriter.LoadPng(Fixture("ocr/en-two-lines.png")), "en-US", default);
        Assert.Equal(["We have to leave", "before sunset."], lines.Select(l => l.Text));
        Assert.True(lines[0].Bounds.Y < lines[1].Bounds.Y);
    }

    [MacFact]
    public async Task Recognizes_WhiteOnScene()
    {
        var lines = await new VisionOcrEngine().RecognizeAsync(
            ImageIoFileWriter.LoadPng(Fixture("ocr/en-white-on-scene.png")), "en-US", default);
        Assert.Contains("Where is the key", string.Join(" ", lines.Select(l => l.Text)));
    }

    [MacFact]
    public void IsAvailable_enUS_True_xxXX_False()
    {
        var engine = new VisionOcrEngine();
        Assert.True(engine.IsAvailable("en-US"));
        Assert.False(engine.IsAvailable("xx-XX"));
    }

    [MacFact]
    public async Task Words_HaveBoundsInsideLine()
    {
        var frame = ImageIoFileWriter.LoadPng(Fixture("ocr/en-two-lines.png"));
        var lines = await new VisionOcrEngine().RecognizeAsync(frame, "en-US", default);
        foreach (var line in lines)
        {
            Assert.NotNull(line.Words);
            Assert.NotEmpty(line.Words);
            foreach (var w in line.Words)
            {
                Assert.True(w.Bounds.X >= line.Bounds.X - 2 && w.Bounds.Y >= line.Bounds.Y - 2, $"{w.Text}: {w.Bounds} vs {line.Bounds}");
                Assert.True(w.Bounds.Right <= line.Bounds.Right + 2 && w.Bounds.Bottom <= line.Bounds.Bottom + 2, $"{w.Text}: {w.Bounds} vs {line.Bounds}");
            }
        }
    }

    [MacFact]
    public async Task Recognize_UnavailableLanguage_Throws()
    {
        var frame = ImageIoFileWriter.LoadPng(Fixture("ocr/en-two-lines.png"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VisionOcrEngine().RecognizeAsync(frame, "xx-XX", default));
    }

    [MacFact]
    public async Task Recognize_CancelledToken_Throws()
    {
        var frame = ImageIoFileWriter.LoadPng(Fixture("ocr/en-two-lines.png"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VisionOcrEngine().RecognizeAsync(frame, "en-US", cts.Token));
    }

    internal static string Fixture(string rel) => Path.Combine(AppContext.BaseDirectory, "fixtures", rel);
}
