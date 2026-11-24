using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Tests.Imaging;

public class FrameTests
{
    [Fact] public void Frame_RejectsWrongBufferLength() =>
        Assert.Throws<ArgumentException>(() => new Frame(2, 2, new byte[15]));

    [Fact] public void Frame_Crop_CopiesRows()
    {
        var data = new byte[4 * 2 * 4];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
        var crop = new Frame(4, 2, data).Crop(new PixelRect(1, 0, 2, 2));
        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(new byte[] { 4,5,6,7, 8,9,10,11, 20,21,22,23, 24,25,26,27 }, crop.Bgra);
    }
}
