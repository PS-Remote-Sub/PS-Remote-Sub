using System.Globalization;
using System.Text.RegularExpressions;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>Pairs a capture card's video device with its audio input from the strings AVFoundation reports.</summary>
internal static partial class AvDevicePairing
{
    internal sealed record Video(string Id, string Name, string ModelId);

    internal sealed record Audio(string Id, string Name, string ModelId);

    /// <summary>
    /// The audio input of <paramref name="video"/>: first by USB vendor and product (video model id
    /// <c>UVC Camera VendorID_13407 ProductID_8496</c>, video uniqueID <c>0x&lt;location&gt;&lt;vid&gt;&lt;pid&gt;</c>, audio
    /// <c>...:345F:2130:...</c>), then by equal non-empty model id, then by equal non-empty name. Null if none.
    /// </summary>
    public static string? PairAudio(Video video, IEnumerable<Audio> audios)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audios);
        var list = audios as IReadOnlyList<Audio> ?? audios.ToList();

        if (UsbIdOfVideo(video) is { } usb)
        {
            foreach (var a in list)
            {
                if (UsbIdOfAudio(a) == usb)
                    return a.Id;
            }
        }
        if (video.ModelId.Length > 0 && list.FirstOrDefault(a => a.ModelId == video.ModelId) is { } byModel)
            return byModel.Id;
        if (video.Name.Length > 0 && list.FirstOrDefault(a => a.Name == video.Name) is { } byName)
            return byName.Id;
        return null;
    }

    private static (int Vid, int Pid)? UsbIdOfVideo(Video v)
    {
        var m = UvcModel().Match(v.ModelId);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int vid)
            && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            return (vid, pid);
        var u = VideoUniqueId().Match(v.Id);
        return u.Success ? (Hex(u.Groups[1].Value), Hex(u.Groups[2].Value)) : null;
    }

    private static (int Vid, int Pid)? UsbIdOfAudio(Audio a)
    {
        foreach (string text in new[] { a.ModelId, a.Id })
        {
            var m = AudioModel().Match(text);
            if (m.Success)
                return (Hex(m.Groups[1].Value), Hex(m.Groups[2].Value));
        }
        return null;
    }

    private static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"VendorID_(\d+)\s+ProductID_(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UvcModel();

    [GeneratedRegex(@"^0x[0-9a-f]{1,8}([0-9a-f]{4})([0-9a-f]{4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VideoUniqueId();

    [GeneratedRegex(@":([0-9a-f]{4}):([0-9a-f]{4})(?::|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AudioModel();
}
