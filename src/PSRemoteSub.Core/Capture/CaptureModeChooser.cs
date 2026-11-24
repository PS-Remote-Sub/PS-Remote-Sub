using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Capture;

/// <summary>Pure rules for picking which capture mode to open.</summary>
public static class CaptureModeChooser
{
    private const int MaxWidth = 3840;
    private const int MaxHeight = 2160;
    private const double MaxFps = 60.5;
    private const double RateTolerance = 0.5;

    private static bool IsUsable(CaptureMode m) =>
        m.Width <= MaxWidth && m.Height <= MaxHeight && m.Fps <= MaxFps && m.Subtype != CaptureSubtype.Other;

    private static int SubtypeRank(CaptureSubtype s) => s switch
    {
        CaptureSubtype.Nv12 => 0,
        CaptureSubtype.Yuy2 => 1,
        CaptureSubtype.Mjpg => 2,
        _ => 3,
    };

    /// <summary>Picks the mode to open: the wanted one when offered, otherwise the best automatic one; null when nothing is usable.</summary>
    public static CaptureMode? Choose(IReadOnlyList<CaptureMode> offered, CaptureModeSetting? wanted)
    {
        var usable = offered.Where(IsUsable).ToList();
        if (wanted is not null)
        {
            var matches = usable
                .Where(m => m.Width == wanted.Width && m.Height == wanted.Height
                            && Math.Abs(m.Fps - wanted.Fps) < RateTolerance)
                .ToList();
            if (matches.Count > 0)
                return Best(matches);
        }
        return Auto(usable);
    }

    private static CaptureMode? Auto(List<CaptureMode> usable)
    {
        if (usable.Count == 0)
            return null;
        var fast = usable.Where(m => m.Fps >= 59).ToList();
        if (fast.Count == 0)
        {
            var top = usable.Max(m => Math.Round(m.Fps));
            fast = usable.Where(m => Math.Round(m.Fps) == top).ToList();
        }
        return Best(fast);
    }

    private static CaptureMode Best(List<CaptureMode> modes) => modes
        .OrderByDescending(m => (long)m.Width * m.Height)
        .ThenByDescending(m => m.Fps)
        .ThenBy(m => SubtypeRank(m.Subtype))
        .First();

    /// <summary>The compressed (MJPG) twin of a mode that failed uncompressed; null when there is none or it already was MJPG.</summary>
    public static CaptureMode? Fallback(IReadOnlyList<CaptureMode> offered, CaptureMode failed)
    {
        if (failed.Subtype == CaptureSubtype.Mjpg)
            return null;
        return offered.FirstOrDefault(m => m.Subtype == CaptureSubtype.Mjpg
                                           && m.Width == failed.Width && m.Height == failed.Height
                                           && Math.Abs(m.Fps - failed.Fps) < RateTolerance);
    }

    /// <summary>Distinct usable size/rate choices for the settings list, largest and fastest first.</summary>
    public static IReadOnlyList<CaptureModeSetting> Choices(IReadOnlyList<CaptureMode> offered) => offered
        .Where(IsUsable)
        .GroupBy(m => (m.Width, m.Height, Fps: Math.Round(m.Fps, 2)))
        .Select(g => new CaptureModeSetting(g.Key.Width, g.Key.Height, g.Key.Fps))
        .OrderByDescending(c => (long)c.Width * c.Height)
        .ThenByDescending(c => c.Fps)
        .ToList();
}
