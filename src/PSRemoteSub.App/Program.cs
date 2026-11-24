using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;
using Velopack;

namespace PSRemoteSub.App;

internal static class Program
{
    private const string SingleInstanceMutexName = "PSRemoteSub.SingleInstance";

    /// <summary>The OS services for this run; created once in <see cref="Main"/>.</summary>
    internal static IPlatformServices Platform { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        Platform = PlatformFactory.Create();
        AppPaths.Initialize(Platform.DataDir);

        // Velopack install/update/uninstall hooks run first and may relaunch or exit the process.
        VelopackApp.Build().Run();

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
            return 0; // Another instance is already running: exit silently.

        // Local diagnostic log for bug reports (never uploaded). Only this, the running instance, writes to it.
        Log.Configure(AppPaths.LogFile);
        Log.Info($"PS Remote Sub {AppHost.Version} starting ({RuntimeInformation.OSDescription}, .NET {Environment.Version})");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception" + (e.IsTerminating ? " (terminating)" : ""), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Warn("Unobserved task exception", e.Exception);

        int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        Log.Info($"Exited with code {code}");
        return code;
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        PlatformFactory.ConfigureAppBuilder(AppBuilder.Configure<App>())
            .UsePlatformDetect()
            .LogToTrace();
}
