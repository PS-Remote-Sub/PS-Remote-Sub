using Avalonia;
using PSRemoteSub.Core.Platform;
#if WINDOWS
using PSRemoteSub.Windows;
#elif MACOS
using PSRemoteSub.MacOS;
#endif

namespace PSRemoteSub.App;

/// <summary>Picks the OS implementation of <see cref="IPlatformServices"/> at compile time. The only place in the App that does.</summary>
internal static class PlatformFactory
{
    public static IPlatformServices Create() =>
#if WINDOWS
        new WindowsPlatformServices();
#elif MACOS
        new MacPlatformServices();
#else
#error Unsupported target framework: define WINDOWS or MACOS.
#endif

    /// <summary>OS-specific Avalonia options. On Mac the app is a menu bar app: no Dock icon.</summary>
    public static AppBuilder ConfigureAppBuilder(AppBuilder builder) =>
#if MACOS
        builder.With(new MacOSPlatformOptions { ShowInDock = false });
#else
        builder;
#endif
}
