using Avalonia;
using Avalonia.Styling;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>Applies the saved <see cref="AppTheme"/> to the whole app (every window follows the app's variant).</summary>
internal static class ThemeApplier
{
    public static void Apply(Application app, AppTheme theme) =>
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };

    /// <summary>The theme toggle (main window, setup wizard): flips light/dark into the settings and applies it; the caller saves.</summary>
    public static void Toggle(Application app, AppSettings settings)
    {
        settings.Theme = IsLight(app) ? AppTheme.Dark : AppTheme.Light;
        Apply(app, settings.Theme);
    }

    /// <summary>Whether the app is showing its light variant right now (System resolves to the OS setting).</summary>
    public static bool IsLight(Application app) => app.ActualThemeVariant == ThemeVariant.Light;
}
