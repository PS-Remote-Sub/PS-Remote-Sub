using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Tests.Imaging;

public class PreprocessorTests
{
    private static Frame Solid(int w, int h, byte r, byte g, byte b)
    {
        var data = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            data[i * 4] = b; data[i * 4 + 1] = g; data[i * 4 + 2] = r; data[i * 4 + 3] = 255;
        }
        return new Frame(w, h, data);
    }

    [Fact] public void Normal_IsGray()
    {
        var o = Preprocessor.Apply(Solid(1, 1, 200, 100, 50), PreprocessFilter.Normal, 10000);
        for (int c = 0; c < 3; c++) Assert.InRange(o.Bgra[c], 123, 125);
        Assert.Equal(255, o.Bgra[3]);
    }

    [Fact] public void WhiteText_NearWhiteBecomesBlack_ColoredWhite()
    {
        var a = Preprocessor.Apply(Solid(1, 200, 230, 230, 230), PreprocessFilter.WhiteText, 10000);
        Assert.Equal(0, a.Bgra[0]);
        var b = Preprocessor.Apply(Solid(1, 200, 250, 200, 200), PreprocessFilter.WhiteText, 10000);
        Assert.Equal(255, b.Bgra[0]);
    }

    [Fact] public void DarkText_Threshold110()
    {
        Assert.Equal(0, Preprocessor.Apply(Solid(1, 200, 110, 110, 110), PreprocessFilter.DarkText, 10000).Bgra[0]);
        Assert.Equal(255, Preprocessor.Apply(Solid(1, 200, 111, 111, 111), PreprocessFilter.DarkText, 10000).Bgra[0]);
    }

    [Fact] public void Upscales_WhenShort()
    {
        var o = Preprocessor.Apply(Solid(100, 40, 10, 20, 30), PreprocessFilter.Normal, 10000);
        Assert.Equal(200, o.Width);
        Assert.Equal(80, o.Height);
    }

    [Fact] public void NoUpscale_WhenTall()
    {
        var o = Preprocessor.Apply(Solid(100, 200, 10, 20, 30), PreprocessFilter.Normal, 10000);
        Assert.Equal(100, o.Width);
        Assert.Equal(200, o.Height);
    }

    [Fact] public void NoUpscale_WhenExceedsMax()
    {
        var o = Preprocessor.Apply(Solid(6000, 50, 10, 20, 30), PreprocessFilter.Normal, 10000);
        Assert.Equal(6000, o.Width);
        Assert.Equal(50, o.Height);
    }
}
