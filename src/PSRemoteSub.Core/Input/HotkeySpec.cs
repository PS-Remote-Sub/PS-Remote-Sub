using System.Text;

namespace PSRemoteSub.Core.Input;

public sealed record HotkeySpec(bool Ctrl, bool Alt, bool Shift, char Key)
{
    public static HotkeySpec Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Hotkey is empty.");
        bool ctrl = false, alt = false, shift = false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default: throw new FormatException($"Unknown modifier '{parts[i]}'.");
            }
        }
        if (!(ctrl || alt || shift)) throw new FormatException("Hotkey needs at least one modifier.");
        var keyText = parts[^1];
        if (keyText.Length != 1) throw new FormatException($"Invalid key '{keyText}'.");
        char key = char.ToUpperInvariant(keyText[0]);
        if (!(key is >= 'A' and <= 'Z' || key is >= '0' and <= '9'))
            throw new FormatException($"Invalid key '{keyText}'.");
        return new HotkeySpec(ctrl, alt, shift, key);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Ctrl) sb.Append("Ctrl+");
        if (Alt) sb.Append("Alt+");
        if (Shift) sb.Append("Shift+");
        return sb.Append(Key).ToString();
    }
}
