using System.Globalization;
using System.Resources;

namespace PSRemoteSub.App;

/// <summary>UI copy from <c>Strings.resx</c> (neutral resources are Thai).</summary>
internal static class Loc
{
    private static readonly ResourceManager Resources = new("PSRemoteSub.App.Strings", typeof(Loc).Assembly);

    /// <summary>The string for <paramref name="key"/>, or the key itself when it is missing.</summary>
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
