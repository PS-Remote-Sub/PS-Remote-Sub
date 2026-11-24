using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Capture;

/// <summary>Fits a video frame inside a host rectangle without distorting it.</summary>
public static class Letterbox
{
    /// <summary>Largest rectangle with the frame's aspect ratio that fits inside <paramref name="host"/>, centred.</summary>
    public static PixelRect Fit(PixelRect host, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || host.Width <= 0 || host.Height <= 0)
            return host;

        int width, height;
        if ((long)host.Width * frameHeight <= (long)host.Height * frameWidth)
        {
            width = host.Width;
            height = (int)Math.Round((double)host.Width * frameHeight / frameWidth, MidpointRounding.AwayFromZero);
        }
        else
        {
            height = host.Height;
            width = (int)Math.Round((double)host.Height * frameWidth / frameHeight, MidpointRounding.AwayFromZero);
        }
        return new PixelRect(host.X + (host.Width - width) / 2, host.Y + (host.Height - height) / 2, width, height);
    }
}
