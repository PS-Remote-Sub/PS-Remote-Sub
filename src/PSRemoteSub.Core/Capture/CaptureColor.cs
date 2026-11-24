namespace PSRemoteSub.Core.Capture;

/// <summary>
/// Names a capture mode's YUV matrix and nominal range (Media Foundation's <c>MF_MT_YUV_MATRIX</c> and
/// <c>MF_MT_VIDEO_NOMINAL_RANGE</c> values) for the log, so a wrong-colour report (crushed blacks, a washed-out picture)
/// can be traced to what the card declared.
/// </summary>
public static class CaptureColor
{
    /// <summary><c>matrix BT.709, range limited 16-235</c>; "unset" for an attribute the card does not declare.</summary>
    public static string Describe(uint? matrix, uint? range) => $"matrix {Matrix(matrix)}, range {Range(range)}";

    private static string Matrix(uint? value) => value switch
    {
        null => "unset",
        0 => "unknown",
        1 => "BT.709",
        2 => "BT.601",
        3 => "SMPTE 240M",
        4 => "BT.2020",
        5 => "BT.2020 (12-bit)",
        _ => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Range(uint? value) => value switch
    {
        null => "unset",
        0 => "unknown",
        1 => "full 0-255",
        2 => "limited 16-235",
        3 => "48-208",
        4 => "64-127",
        _ => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
