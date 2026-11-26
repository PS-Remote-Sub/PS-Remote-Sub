namespace PSRemoteSub.Core.Settings;

/// <summary>The minimum-display choices the settings UI offers (seconds).</summary>
public static class MinDisplayChoices
{
    public static IReadOnlyList<double> Values { get; } = [0, 0.5, 1, 1.5, 2];

    /// <summary>The fixed choices, plus <paramref name="current"/> after them when it is not one of them.</summary>
    public static IReadOnlyList<double> For(double current) =>
        Values.Contains(current) ? Values : [.. Values, current];
}
