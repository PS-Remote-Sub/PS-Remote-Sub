namespace PSRemoteSub.Core.Diagnostics;

/// <summary>
/// Process-wide diagnostic log for bug reports. Does nothing until <see cref="Configure"/> is called (the App points
/// it at <c>%AppData%\PSRemoteSub\log.txt</c>). Thread-safe; never throws. Local file only: nothing is sent anywhere.
/// </summary>
/// <remarks>
/// Privacy: log event types, states and exception types/messages only. Never log subtitle text, translations or
/// anything else read from the screen.
/// </remarks>
public static class Log
{
    private static FileLog? _sink;

    /// <summary>The file being written, or null before <see cref="Configure"/>.</summary>
    public static string? FilePath => Volatile.Read(ref _sink)?.Path;

    public static void Configure(string path, long maxBytes = FileLog.DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Volatile.Write(ref _sink, new FileLog(path, maxBytes));
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", With(message, ex));

    public static void Error(string message, Exception? ex = null) => Write("ERROR", With(message, ex));

    /// <summary><c>TypeName: message</c>, for exceptions whose message is known not to carry user text.</summary>
    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static string With(string message, Exception? ex) => ex is null ? message : $"{message}: {Describe(ex)}";

    private static void Write(string level, string message)
    {
        try
        {
            Volatile.Read(ref _sink)?.Write(level, message);
        }
        catch (Exception)
        {
            // Never throws (FileLog already guards itself; this also covers a broken message argument).
        }
    }
}

/// <summary>
/// Rate limit for a failure that can repeat every tick: logs the first occurrence and every
/// <see cref="Every"/>-th after it, until <see cref="Reset"/> (the next success). Thread-safe.
/// </summary>
public sealed class LogThrottle(int every = 100)
{
    private int _count;

    public int Every { get; } = every > 0 ? every : throw new ArgumentOutOfRangeException(nameof(every));

    /// <summary>Counts one failure; true when this one should be logged. <paramref name="count"/> is the running total.</summary>
    public bool ShouldLog(out int count)
    {
        count = Interlocked.Increment(ref _count);
        return count == 1 || count % Every == 0;
    }

    public void Reset() => Interlocked.Exchange(ref _count, 0);
}
