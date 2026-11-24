using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>Maps AVFoundation's CoreMedia pixel formats to the app's <see cref="CaptureMode"/>.</summary>
internal static class AvModeMapping
{
    /// <summary>A mode from a size, its maximum frame rate and the format's FourCC (big-endian, e.g. '420v').</summary>
    public static CaptureMode ToMode(int width, int height, double fps, uint fourcc) =>
        new(width, height, fps, SubtypeOf(fourcc));

    private static CaptureSubtype SubtypeOf(uint fourcc) => fourcc switch
    {
        0x34323076 or 0x34323066 /* 420v, 420f */ => CaptureSubtype.Nv12,
        0x79757673 or 0x32767579 /* yuvs, 2vuy */ => CaptureSubtype.Yuy2,
        0x646D6231 /* dmb1 */ => CaptureSubtype.Mjpg,
        _ => CaptureSubtype.Other,
    };
}
