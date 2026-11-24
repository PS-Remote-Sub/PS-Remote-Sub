using System.Text.RegularExpressions;

namespace PSRemoteSub.Core.Tv;

/// <summary>Result of <c>adb connect</c>.</summary>
public enum AdbConnect { Connected, Unauthorized, Failed }

/// <summary>Result of <c>adb install</c>.</summary>
public enum AdbInstallOutcome { Success, SignatureMismatch, Failed }

/// <summary>Pure parsers for the text adb prints (formats measured on a real Android 12 TV).</summary>
public static partial class AdbParsers
{
    private const string AdbTcpService = "_adb._tcp";

    [GeneratedRegex(@"^\s*versionCode=(\d+)", RegexOptions.Multiline)]
    private static partial Regex VersionCodeLine();

    /// <summary>
    /// <c>adb mdns services</c>: tab-separated <c>name, service type, ip:port</c> after a header line. Only
    /// <c>_adb._tcp</c> endpoints are usable with <c>adb connect</c>, so the TLS connect/pairing entries are dropped.
    /// </summary>
    public static IReadOnlyList<(string Name, string Endpoint)> MdnsServices(string stdout)
    {
        var result = new List<(string, string)>();
        foreach (var line in stdout.Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length >= 3 && parts[1].Trim() == AdbTcpService && parts[2].Trim().Length > 0)
                result.Add((parts[0].Trim(), parts[2].Trim()));
        }
        return result;
    }

    /// <summary>
    /// <c>adb connect</c>: "failed to authenticate" means the TV is waiting for the user's approval;
    /// "connected to" / "already connected to" is success; anything else ("failed to connect", "cannot connect") failed.
    /// </summary>
    public static AdbConnect Connect(string stdout)
    {
        if (stdout.Contains("failed to authenticate", StringComparison.OrdinalIgnoreCase))
            return AdbConnect.Unauthorized;
        return stdout.Contains("connected to", StringComparison.OrdinalIgnoreCase) ? AdbConnect.Connected : AdbConnect.Failed;
    }

    /// <summary><c>adb get-state</c>: "device", "unauthorized", "offline"...; null when empty.</summary>
    public static string? DeviceState(string stdout)
    {
        var state = stdout.Trim();
        return state.Length == 0 ? null : state;
    }

    /// <summary>The first <c>versionCode=N</c> line of <c>dumpsys package</c>; null when the package is not installed.</summary>
    public static int? VersionCode(string dumpsysStdout)
    {
        var m = VersionCodeLine().Match(dumpsysStdout);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary><c>adb install</c> output (stdout and stderr together): "Success", or a Failure [INSTALL_FAILED_...] line.</summary>
    public static AdbInstallOutcome Install(string output)
    {
        if (output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.Ordinal) ||
            output.Contains("INSTALL_PARSE_FAILED_INCONSISTENT_CERTIFICATES", StringComparison.Ordinal))
            return AdbInstallOutcome.SignatureMismatch;
        return output.Split('\n').Any(l => l.Trim() == "Success") ? AdbInstallOutcome.Success : AdbInstallOutcome.Failed;
    }
}
