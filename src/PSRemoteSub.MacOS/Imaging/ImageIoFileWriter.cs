using System.Runtime.InteropServices;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Imaging;

/// <summary>ImageIO PNG writer (and loader, used by tests and fixtures).</summary>
public sealed class ImageIoFileWriter : IImageFileWriter
{
    public Task SavePngAsync(Frame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Task.Run(() =>
        {
            int rc;
            unsafe
            {
                fixed (byte* p = frame.Bgra)
                    rc = Native.psrs_png_save(p, frame.Width, frame.Height, path);
            }
            if (rc != 0)
                throw new IOException($"Could not write PNG '{path}' (code {rc}).");
        });
    }

    internal static Frame LoadPng(string path)
    {
        int rc = Native.psrs_png_load(path, out nint buf, out int w, out int h);
        if (rc != 0)
            throw new IOException($"Could not read image '{path}' (code {rc}).");
        try
        {
            var bytes = new byte[checked(w * h * 4)];
            Marshal.Copy(buf, bytes, 0, bytes.Length);
            return new Frame(w, h, bytes);
        }
        finally { Native.psrs_free(buf); }
    }
}
