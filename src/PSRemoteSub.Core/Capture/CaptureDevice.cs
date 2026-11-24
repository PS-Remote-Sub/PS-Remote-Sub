using System.Text.RegularExpressions;

namespace PSRemoteSub.Core.Capture;

/// <summary>A video capture device. <see cref="Id"/> is the WinRT device id, which is also its Media Foundation symbolic link.</summary>
/// <param name="Id">WinRT <c>DeviceInformation.Id</c> of the video capture device.</param>
/// <param name="Name">Friendly name.</param>
/// <param name="AudioDeviceId">Id of the paired audio capture device (see <see cref="CaptureDeviceRules.PairAudio"/>), or null.</param>
/// <param name="IsBuiltIn">Built into the PC (a laptop's camera): never picked unless it was chosen.</param>
public sealed record CaptureDevice(string Id, string Name, string? AudioDeviceId, bool IsBuiltIn = false);

/// <summary>Pure rules for pairing a capture device with its audio input and for choosing which device to open.</summary>
public static partial class CaptureDeviceRules
{
    /// <summary>
    /// The container Windows gives every device built into the PC. A built-in camera and the built-in microphone share
    /// it, so it says nothing about which audio belongs to a capture card.
    /// </summary>
    public static readonly Guid SystemContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    /// <summary>
    /// The first audio input in the video device's own container (a USB card's audio interface shares it), or null.
    /// Never pairs on no container, <see cref="Guid.Empty"/> or <see cref="SystemContainer"/>.
    /// </summary>
    public static string? PairAudio(Guid? videoContainer, IEnumerable<(string Id, Guid? Container)> audioInputs)
    {
        ArgumentNullException.ThrowIfNull(audioInputs);
        if (videoContainer is not { } container || container == Guid.Empty || container == SystemContainer)
            return null;
        foreach (var (id, audioContainer) in audioInputs)
        {
            if (audioContainer == container)
                return id;
        }
        return null;
    }

    /// <summary>
    /// The device to open. With a saved id: that device, or the same model on another USB port (its id changes with the
    /// port): the same vendor, product and interface first, then the same vendor and product (also when it was saved on
    /// the other OS), otherwise null: another device is never substituted, so an unplugged card shows as missing rather than the
    /// webcam. With no saved id: the first device that is not built in and has paired audio, then the first that is not
    /// built in, otherwise null.
    /// </summary>
    public static CaptureDevice? ResolveDevice(IReadOnlyList<CaptureDevice> devices, string? savedId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (!string.IsNullOrEmpty(savedId))
        {
            if (devices.FirstOrDefault(d => string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase)) is { } saved)
                return saved;
            if (ModelKey(savedId) is not { } model)
                return null;
            // The same interface first (a card with several video interfaces keeps the one chosen), then the same
            // vendor and product (an id saved on the other OS, where the interface is missing from one side).
            return devices.FirstOrDefault(d => ModelKey(d.Id) is { } key && string.Equals(key, model, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => ModelKey(d.Id) is { } key && SameVidPid(key, model));
        }
        return devices.FirstOrDefault(d => !d.IsBuiltIn && d.AudioDeviceId is not null)
            ?? devices.FirstOrDefault(d => !d.IsBuiltIn);
    }

    /// <summary>
    /// Whether two <see cref="ModelKey"/>s name the same USB vendor and product. The interface (<c>&amp;MI_xx</c>) is left out:
    /// a Mac uniqueID has none, so a card saved on Windows is found on a Mac and the other way round.
    /// </summary>
    private static bool SameVidPid(string a, string b) =>
        string.Equals(VidPid(a), VidPid(b), StringComparison.OrdinalIgnoreCase);

    private static string VidPid(string modelKey) =>
        modelKey.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) is var i and >= 0 ? modelKey[..i] : modelKey;

    /// <summary>
    /// The USB vendor, product and interface of a device id (<c>VID_345F&amp;PID_2130&amp;MI_00</c>, upper case), which
    /// stay the same on every port; null for an id that is not a USB device's. A Mac uniqueID (<c>0x&lt;location&gt;&lt;vid&gt;&lt;pid&gt;</c>) gives <c>VID_345F&amp;PID_2130</c>.
    /// </summary>
    public static string? ModelKey(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var match = UsbModel().Match(deviceId);
        if (match.Success)
            return match.Value.ToUpperInvariant();
        // Mac uniqueID: 0x, the USB location (changes with the port), then vendor and product as 4 hex digits each.
        var mac = MacUniqueId().Match(deviceId);
        return mac.Success ? $"VID_{mac.Groups[1].Value.ToUpperInvariant()}&PID_{mac.Groups[2].Value.ToUpperInvariant()}" : null;
    }

    [GeneratedRegex(@"^0x[0-9a-f]{1,8}([0-9a-f]{4})([0-9a-f]{4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MacUniqueId();

    [GeneratedRegex(@"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&MI_[0-9A-F]{2})?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbModel();
}
