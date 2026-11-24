using System.Net;
using System.Net.NetworkInformation;
using PSRemoteSub.Core.Net;

namespace PSRemoteSub.Core.Tests.Net;

public class LanAddressesTests
{
    private static NicInfo Nic(string name, string ip, bool up, bool gw, NetworkInterfaceType t, string? desc = null) =>
        new(name, desc ?? name, up, gw, [IPAddress.Parse(ip)], t);

    [Fact]
    public void Rank_PrefersUpEthernetOrWifiWithGateway_SkipsVirtualAndLoopback()
    {
        var nics = new[]
        {
            Nic("Ethernet 2", "10.0.0.5", true, false, NetworkInterfaceType.Ethernet),
            Nic("vEthernet (WSL)", "172.20.0.1", true, true, NetworkInterfaceType.Ethernet),
            Nic("Loopback Pseudo-Interface 1", "127.0.0.1", true, false, NetworkInterfaceType.Loopback),
            Nic("Wi-Fi", "192.168.1.20", true, true, NetworkInterfaceType.Wireless80211),
            Nic("Ethernet 3", "192.168.9.9", false, true, NetworkInterfaceType.Ethernet),
        };
        Assert.Equal(
            [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.5")],
            LanAddresses.Rank(nics));
    }

    [Fact]
    public void BroadcastOf_24Bit() =>
        Assert.Equal(IPAddress.Parse("192.168.0.255"),
            LanAddresses.BroadcastOf(IPAddress.Parse("192.168.0.42"), IPAddress.Parse("255.255.255.0")));

    [Fact]
    public void BroadcastTargets_SkipVirtualNics()
    {
        var mask = new[] { IPAddress.Parse("255.255.255.0") };
        NicInfo N(string name, string ip) =>
            new(name, name, true, true, [IPAddress.Parse(ip)], NetworkInterfaceType.Ethernet, mask);
        var nics = new[]
        {
            N("VMware Network Adapter VMnet1", "192.168.100.1"),
            N("vEthernet (WSL)", "172.20.0.1"),
            N("Ethernet", "192.168.0.42"),
        };
        Assert.Equal(
            [IPAddress.Parse("192.168.0.255"), IPAddress.Broadcast],
            LanAddresses.GetBroadcastTargets(nics));
    }

    [Fact]
    public void Rank_MacNames_en0First_ExcludesUtunAwdlBridge()
    {
        var nics = new[]
        {
            Nic("utun3", "100.64.0.2", true, false, NetworkInterfaceType.Ethernet),
            Nic("awdl0", "169.254.10.1", true, false, NetworkInterfaceType.Ethernet),
            Nic("bridge100", "192.168.64.1", true, false, NetworkInterfaceType.Ethernet),
            Nic("llw0", "169.254.20.1", true, false, NetworkInterfaceType.Ethernet),
            Nic("anpi0", "169.254.30.1", true, false, NetworkInterfaceType.Ethernet),
            Nic("ap1", "169.254.40.1", true, false, NetworkInterfaceType.Ethernet),
            Nic("vmenet0", "192.168.65.1", true, false, NetworkInterfaceType.Ethernet),
            Nic("en5", "10.0.0.7", true, false, NetworkInterfaceType.Ethernet),
            Nic("en0", "192.168.1.30", true, true, NetworkInterfaceType.Wireless80211),
        };
        Assert.Equal(
            [IPAddress.Parse("192.168.1.30"), IPAddress.Parse("10.0.0.7")],
            LanAddresses.Rank(nics));
    }

    [Fact]
    public void LocalNets_ListsUsableNicsWithMasks()
    {
        var mask = new[] { IPAddress.Parse("255.255.255.0") };
        NicInfo N(string name, string ip) =>
            new(name, name, true, true, [IPAddress.Parse(ip)], NetworkInterfaceType.Ethernet, mask);
        var nets = LanAddresses.GetLocalNets([N("utun3", "100.64.0.2"), N("en0", "192.168.0.42")]);
        Assert.Equal([(IPAddress.Parse("192.168.0.42"), IPAddress.Parse("255.255.255.0"))], nets);
    }
}
