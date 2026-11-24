using System.Globalization;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Tv;

/// <summary>Locates the adb and TV app APK that ship in the <c>tv</c> folder next to the PC app.</summary>
public static class TvBundle
{
    /// <summary>Path of the bundled adb: <c>tv/adb/win/adb.exe</c> on Windows, <c>tv/adb/mac/adb</c> on macOS.</summary>
    public static string AdbPath(string baseDir, PlatformKind kind) => kind == PlatformKind.MacOS
        ? Path.Combine(baseDir, "tv", "adb", "mac", "adb")
        : Path.Combine(baseDir, "tv", "adb", "win", "adb.exe");

    /// <summary>Path of the bundled TV app APK.</summary>
    public static string ApkPath(string baseDir) => Path.Combine(baseDir, "tv", "psrs-tv-overlay.apk");

    /// <summary>The bundled APK's versionCode from <c>tv/apk-version.txt</c>; null if the file is missing or not an integer.</summary>
    public static int? BundledVersion(string baseDir)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(baseDir, "tv", "apk-version.txt")).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
