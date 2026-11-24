using System.Runtime.InteropServices.WindowsRuntime;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using Windows.Graphics.Imaging;

namespace PSRemoteSub.Windows.Imaging;

/// <summary>PNG encode/decode through Windows.Graphics.Imaging (WIC) using BGRA8 <see cref="SoftwareBitmap"/>s.</summary>
public sealed class WinRtImageFileWriter : IImageFileWriter
{
    public async Task SavePngAsync(Frame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(frame.Bgra.AsBuffer());

        await using var file = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var stream = file.AsRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }

    public static async Task<Frame> LoadPngAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stream = file.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, stream);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var bgra = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(bgra.AsBuffer());
        return new Frame(bitmap.PixelWidth, bitmap.PixelHeight, bgra);
    }
}
