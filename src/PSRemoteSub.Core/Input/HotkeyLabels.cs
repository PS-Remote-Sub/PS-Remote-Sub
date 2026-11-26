using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Input;

/// <summary>How a hotkey is shown to the user in status and help texts. Stored and edited text stays <c>Ctrl+Alt+Shift+S</c>.</summary>
public static class HotkeyLabels
{
    /// <summary>Windows: <c>Ctrl+Alt+Shift+S</c>. Mac: <c>⌃⌥⇧S</c> (control, option, shift, then the key).</summary>
    public static string Display(HotkeySpec spec, PlatformKind kind)
    {
        if (kind != PlatformKind.MacOS) return spec.ToString();
        return (spec.Ctrl ? "⌃" : "") + (spec.Alt ? "⌥" : "") + (spec.Shift ? "⇧" : "") + spec.Key;
    }

    /// <summary>
    /// A stored hotkey setting as shown on this OS: <see cref="Display"/> when it parses, otherwise the setting's own text
    /// (so a status line still names the key the user typed rather than nothing).
    /// </summary>
    /// <summary>
    /// The keys of a hotkey setting as separate key caps (modifiers first, then the key): <c>Ctrl</c> <c>Alt</c>
    /// <c>Shift</c> <c>P</c> on Windows, <c>⌃</c> <c>⌥</c> <c>⇧</c> <c>P</c> on Mac. Unparsable: one cap with the text;
    /// empty: none.
    /// </summary>
    public static IReadOnlyList<string> KeyCaps(string? setting, PlatformKind kind)
    {
        if (string.IsNullOrWhiteSpace(setting)) return [];
        HotkeySpec spec;
        try
        {
            spec = HotkeySpec.Parse(setting);
        }
        catch (FormatException)
        {
            return [setting];
        }
        bool mac = kind == PlatformKind.MacOS;
        var caps = new List<string>(4);
        if (spec.Ctrl) caps.Add(mac ? "⌃" : "Ctrl");
        if (spec.Alt) caps.Add(mac ? "⌥" : "Alt");
        if (spec.Shift) caps.Add(mac ? "⇧" : "Shift");
        caps.Add(spec.Key.ToString());
        return caps;
    }

    public static string DisplaySetting(string? setting, PlatformKind kind)
    {
        try
        {
            return Display(HotkeySpec.Parse(setting ?? ""), kind);
        }
        catch (FormatException)
        {
            return setting ?? "";
        }
    }
}
