namespace PSRemoteSub.App;

/// <summary>The data folder (<c>%AppData%\PSRemoteSub\</c> on Windows, <c>~/Library/Application Support/PSRemoteSub/</c> on macOS) and the files in it.</summary>
internal static class AppPaths
{
    public static string DataDir { get; private set; } = "";

    public static string SettingsFile { get; private set; } = "";

    public static string CapturesDir { get; private set; } = "";

    /// <summary>Downloaded translation libraries, one folder per game.</summary>
    public static string LibraryDir { get; private set; } = "";

    /// <summary>The TV app APK downloaded from the <c>PS-Remote-Sub-TV</c> GitHub release.</summary>
    public static string TvApkDir { get; private set; } = "";

    /// <summary>The translation libraries shipped next to the exe (read-only baseline).</summary>
    public static string BundledLibraryDir { get; } = Path.Combine(AppContext.BaseDirectory, "library");

    /// <summary>Diagnostic log (rolls to <c>log.1.txt</c> at 1 MB). Holds no subtitle text.</summary>
    public static string LogFile { get; private set; } = "";

    /// <summary>Sets every path from the platform's data folder. Called once, first thing in <c>Program.Main</c>.</summary>
    public static void Initialize(string dataDir)
    {
        DataDir = dataDir;
        SettingsFile = Path.Combine(dataDir, "settings.json");
        CapturesDir = Path.Combine(dataDir, "captures");
        LibraryDir = Path.Combine(dataDir, "library");
        TvApkDir = Path.Combine(dataDir, "tv");
        LogFile = Path.Combine(dataDir, "log.txt");
    }
}
