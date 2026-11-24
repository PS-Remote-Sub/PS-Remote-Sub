using System.Net;
using System.Net.Sockets;
using PSRemoteSub.Core.Net;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public class LocalNetworkHintTests
{
    private static readonly IReadOnlyList<(IPAddress Ip, IPAddress Mask)> Nets =
        [(IPAddress.Parse("192.168.1.30"), IPAddress.Parse("255.255.255.0"))];

    [Theory]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.AccessDenied)]
    public void LooksBlocked_BlockingErrorInSubnet_True(SocketError error) =>
        Assert.True(LocalNetworkHint.LooksBlocked(error, IPAddress.Parse("192.168.1.255"), Nets));

    [Fact]
    public void LooksBlocked_HostUnreachableInSubnet_True() =>
        Assert.True(LocalNetworkHint.LooksBlocked(SocketError.HostUnreachable, IPAddress.Parse("192.168.1.50"), Nets));

    [Fact]
    public void LooksBlocked_OutsideSubnet_False() =>
        Assert.False(LocalNetworkHint.LooksBlocked(SocketError.HostUnreachable, IPAddress.Parse("10.1.2.3"), Nets));

    [Fact]
    public void LooksBlocked_ConnectionRefused_False() =>
        Assert.False(LocalNetworkHint.LooksBlocked(SocketError.ConnectionRefused, IPAddress.Parse("192.168.1.50"), Nets));

    [Theory]
    [InlineData("failed to connect to '192.168.1.50:5555': No route to host", true)]
    [InlineData("cannot connect to 192.168.1.50:5555: no route to host (10065)", true)]
    [InlineData("failed to connect to '192.168.1.50:5555': Connection refused", false)]
    [InlineData("", false)]
    public void AdbOutputLooksBlocked_MatchesNoRouteToHost(string output, bool expected) =>
        Assert.Equal(expected, LocalNetworkHint.AdbOutputLooksBlocked(output));

    [Theory]
    // kind, current, installOk, resultBlocked, expected
    [InlineData(PlatformKind.MacOS, false, false, true, true)]   // adb said "No route to host"
    [InlineData(PlatformKind.MacOS, true, false, false, true)]   // other failure keeps it
    [InlineData(PlatformKind.MacOS, true, true, false, false)]   // success clears it
    [InlineData(PlatformKind.MacOS, false, false, false, false)] // plain failure does not raise it
    [InlineData(PlatformKind.Windows, true, false, true, false)] // never off macOS
    public void AfterInstall_Decides(PlatformKind kind, bool current, bool ok, bool blocked, bool expected) =>
        Assert.Equal(expected, LocalNetworkHint.AfterInstall(kind, current, ok, blocked));

    private static BeaconPassResult Pass(params (string Ip, SocketError Error)[] failures) =>
        new(3, [.. failures.Select(f => (IPAddress.Parse(f.Ip), f.Error))]);

    [Fact]
    public void AfterBeaconPass_BlockedFailureOnMac_True() =>
        Assert.True(LocalNetworkHint.AfterBeaconPass(PlatformKind.MacOS,
            Pass(("192.168.1.255", SocketError.HostUnreachable)), () => Nets));

    [Fact]
    public void AfterBeaconPass_CleanPass_Clears() =>
        Assert.False(LocalNetworkHint.AfterBeaconPass(PlatformKind.MacOS, Pass(), () => Nets));

    [Fact]
    public void AfterBeaconPass_UnrelatedFailure_Clears() =>
        Assert.False(LocalNetworkHint.AfterBeaconPass(PlatformKind.MacOS,
            Pass(("192.168.1.255", SocketError.ConnectionRefused)), () => Nets));

    [Fact]
    public void AfterBeaconPass_Windows_AlwaysFalse() =>
        Assert.False(LocalNetworkHint.AfterBeaconPass(PlatformKind.Windows,
            Pass(("192.168.1.255", SocketError.HostUnreachable)), () => Nets));

    // The local subnets are read from the network interfaces; the beacon passes every 2 s, so they are read only
    // when the answer can depend on them: on macOS and with a failure to look at.
    [Fact]
    public void AfterBeaconPass_Windows_DoesNotReadLocalNets()
    {
        int reads = 0;
        LocalNetworkHint.AfterBeaconPass(PlatformKind.Windows,
            Pass(("192.168.1.255", SocketError.HostUnreachable)), () => { reads++; return Nets; });
        Assert.Equal(0, reads);
    }

    [Fact]
    public void AfterBeaconPass_CleanPassOnMac_DoesNotReadLocalNets()
    {
        int reads = 0;
        LocalNetworkHint.AfterBeaconPass(PlatformKind.MacOS, Pass(), () => { reads++; return Nets; });
        Assert.Equal(0, reads);
    }

    [Fact]
    public void AfterBeaconPass_FailuresOnMac_ReadLocalNetsOnce()
    {
        int reads = 0;
        Assert.True(LocalNetworkHint.AfterBeaconPass(PlatformKind.MacOS,
            Pass(("10.0.0.1", SocketError.HostUnreachable), ("192.168.1.255", SocketError.HostUnreachable)),
            () => { reads++; return Nets; }));
        Assert.Equal(1, reads);
    }
}
