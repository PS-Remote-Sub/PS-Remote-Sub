using PSRemoteSub.Core.Imaging;
using PSRemoteSub.MacOS.Imaging;

namespace PSRemoteSub.MacOS.Tests;

public class ImageIoFileWriterTests
{
    [MacFact]
    public async Task Png_SaveThenLoad_SamePixels()
    {
        var bytes = new byte[4 * 3 * 4];
        for (int i = 0; i < 12; i++)
        {
            bytes[i * 4] = (byte)(i * 20);
            bytes[i * 4 + 1] = (byte)(255 - i * 10);
            bytes[i * 4 + 2] = (byte)(i * 5);
            bytes[i * 4 + 3] = 255;
        }
        var frame = new Frame(4, 3, bytes);
        string path = Path.Combine(Path.GetTempPath(), $"psrs-{Guid.NewGuid():N}.png");
        try
        {
            await new ImageIoFileWriter().SavePngAsync(frame, path);
            var loaded = ImageIoFileWriter.LoadPng(path);
            Assert.Equal(4, loaded.Width);
            Assert.Equal(3, loaded.Height);
            Assert.Equal(bytes, loaded.Bgra);
        }
        finally { File.Delete(path); }
    }

    [MacFact]
    public async Task Png_Transparency_RoundTripsWithoutBlendingGarbage()
    {
        // 4x3: all transparent except one half-alpha premultiplied pixel.
        var bytes = new byte[4 * 3 * 4];
        bytes[0] = 50; bytes[1] = 25; bytes[2] = 10; bytes[3] = 128;
        var frame = new Frame(4, 3, bytes);
        string path = Path.Combine(Path.GetTempPath(), $"psrs-{Guid.NewGuid():N}.png");
        try
        {
            await new ImageIoFileWriter().SavePngAsync(frame, path);
            for (int i = 0; i < 20; i++)
            {
                // Churn the heap so a malloc'd buffer would be dirty.
                var junk = new byte[4 * 3 * 4]; Array.Fill(junk, (byte)0xAB); GC.KeepAlive(junk);
                var loaded = ImageIoFileWriter.LoadPng(path);
                Assert.All(loaded.Bgra.Skip(4), b => Assert.Equal(0, b));
                Assert.Equal(128, loaded.Bgra[3]);
            }
        }
        finally { File.Delete(path); }
    }
}
