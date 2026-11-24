using System.Globalization;
using System.Text;

namespace PSRemoteSub.Core.Diagnostics;

/// <summary>
/// Appends <c>yyyy-MM-dd HH:mm:ss.fff [level] message</c> lines (local time) to a file. When the file would grow past
/// <paramref name="maxBytes"/> it is renamed to <c>&lt;name&gt;.1&lt;ext&gt;</c> (replacing the previous one) and a new
/// file is started. Thread-safe. <see cref="Write"/> never throws: a log that cannot be written is silently skipped.
/// </summary>
public sealed class FileLog(string path, long maxBytes = FileLog.DefaultMaxBytes, TimeProvider? time = null)
{
    public const long DefaultMaxBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Path { get; } = path;

    /// <summary>The file the log rolls into: <c>log.txt</c> → <c>log.1.txt</c>.</summary>
    public string RolledPath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(path) ?? "",
        System.IO.Path.GetFileNameWithoutExtension(path) + ".1" + System.IO.Path.GetExtension(path));

    public void Write(string level, string message)
    {
        try
        {
            var stamp = _time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{stamp} [{level}] {OneLine(message)}{Environment.NewLine}";
            var bytes = Utf8NoBom.GetBytes(line);
            lock (_gate)
            {
                if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir)
                    Directory.CreateDirectory(dir);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 0 && info.Length + bytes.Length > maxBytes)
                    File.Move(Path, RolledPath, overwrite: true);
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down (disk full, file locked, folder deleted...).
        }
    }

    private static string OneLine(string? message) =>
        (message ?? "").Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
