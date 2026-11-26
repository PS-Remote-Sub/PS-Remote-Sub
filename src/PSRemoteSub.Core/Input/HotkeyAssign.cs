namespace PSRemoteSub.Core.Input;

public static class HotkeyAssign
{
    /// <summary>The other action already using <paramref name="key"/> (compared as parsed hotkeys), or null.</summary>
    public static HotkeyAction? ConflictWith(IReadOnlyDictionary<HotkeyAction, string> keys, HotkeyAction action, string key)
    {
        if (!TryParse(key, out var wanted)) return null;
        foreach (var (other, text) in keys)
            if (other != action && TryParse(text, out var spec) && spec == wanted)
                return other;
        return null;
    }

    public static void Swap(IDictionary<HotkeyAction, string> keys, HotkeyAction a, HotkeyAction b) =>
        (keys[a], keys[b]) = (keys[b], keys[a]);

    private static bool TryParse(string text, out HotkeySpec spec)
    {
        try { spec = HotkeySpec.Parse(text); return true; }
        catch (FormatException) { spec = null!; return false; }
    }
}
