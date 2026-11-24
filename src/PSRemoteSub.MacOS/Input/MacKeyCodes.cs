namespace PSRemoteSub.MacOS.Input;

/// <summary>ANSI virtual key codes (<c>kVK_ANSI_*</c> in Carbon's <c>Events.h</c>) for the keys a hotkey may use.</summary>
internal static class MacKeyCodes
{
    // Index: 'A'..'Z'.
    private static readonly ushort[] Letters =
    [
        0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E,
        0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06,
    ];

    // Index: '0'..'9'.
    private static readonly ushort[] Digits = [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19];

    public static ushort ForKey(char key) => key switch
    {
        >= 'A' and <= 'Z' => Letters[key - 'A'],
        >= '0' and <= '9' => Digits[key - '0'],
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Only A-Z and 0-9 are supported."),
    };
}
