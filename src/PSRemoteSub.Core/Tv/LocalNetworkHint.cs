using System.Net;
using System.Net.Sockets;
using PSRemoteSub.Core.Net;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Tv;

/// <summary>
/// Spots the failures macOS produces when Local Network access is denied: LAN traffic fails with "no route to host"
/// or "permission denied" while the Internet keeps working. Pure; the caller decides whether the OS can show it.
/// </summary>
public static class LocalNetworkHint
{
    /// <summary>
    /// True when <paramref name="error"/> is HostUnreachable, NetworkUnreachable or AccessDenied and
    /// <paramref name="target"/> is inside one of the local subnets (a LAN peer that should have been reachable).
    /// </summary>
    public static bool LooksBlocked(SocketError error, IPAddress target, IReadOnlyList<(IPAddress Ip, IPAddress Mask)> localNets)
    {
        if (error is not (SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.AccessDenied))
            return false;
        if (target.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var t = target.GetAddressBytes();
        foreach (var (ip, mask) in localNets)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && SameSubnet(t, ip.GetAddressBytes(), mask.GetAddressBytes()))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The hint flag after an install attempt: only on macOS; on when adb reported "No route to host", kept on by a
    /// failure with another cause (unknown), cleared by a success.
    /// </summary>
    public static bool AfterInstall(PlatformKind kind, bool current, bool installOk, bool resultBlocked) =>
        kind == PlatformKind.MacOS && (resultBlocked || (current && !installOk));

    /// <summary>
    /// The hint flag after a beacon send pass that tried at least one send: only on macOS, on when any failure looks
    /// like a denied Local Network, otherwise cleared (the sends work again). <paramref name="localNets"/> is called at
    /// most once, and only on macOS with a failure to look at (the beacon passes every 2 s; reading the interfaces is
    /// not free).
    /// </summary>
    public static bool AfterBeaconPass(PlatformKind kind, BeaconPassResult pass,
        Func<IReadOnlyList<(IPAddress Ip, IPAddress Mask)>> localNets)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(localNets);
        if (kind != PlatformKind.MacOS || pass.Failures.Count == 0)
            return false;
        var nets = localNets();
        return pass.Failures.Any(f => LooksBlocked(f.Error, f.Target, nets));
    }

    /// <summary>Whether adb's output (stdout and stderr) reports <c>No route to host</c>, which is how a denied Local Network shows up there.</summary>
    public static bool AdbOutputLooksBlocked(string output) =>
        output.Contains("No route to host", StringComparison.OrdinalIgnoreCase);

    private static bool SameSubnet(byte[] a, byte[] b, byte[] mask)
    {
        if (a.Length != b.Length || b.Length != mask.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if ((a[i] & mask[i]) != (b[i] & mask[i]))
                return false;
        }
        return true;
    }
}
