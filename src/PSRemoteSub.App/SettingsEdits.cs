using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>
/// Settings writes shared by the settings pages and the setup wizard, so both go through the same path. None of them
/// saves: the pages debounce the save, the wizard saves at once. UI thread only.
/// </summary>
internal static class SettingsEdits
{
    /// <summary>Applies <paramref name="edit"/> to the active game and pushes it to the running app (the Games page's path).</summary>
    public static void EditActiveGame(AppHost host, Action<GameProfile> edit)
    {
        edit(host.Settings.GetActive());
        host.ApplySettings();
    }

    /// <summary>
    /// Sets <c>Translation.Mode</c> and rebuilds the translator (<see cref="AppHost.ApplyTranslationSettings"/>, which
    /// also saves). Returns false, doing nothing, when it is already <paramref name="mode"/>.
    /// </summary>
    public static bool SetTranslationMode(AppHost host, TranslationMode mode)
    {
        if (host.Settings.Translation.Mode == mode)
            return false;
        host.Settings.Translation.Mode = mode;
        host.ApplyTranslationSettings();
        return true;
    }
}
