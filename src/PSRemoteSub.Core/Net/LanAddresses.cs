using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PSRemoteSub.Core.Net;

public static class LanAddresses
{
    private static readonly string[] VirtualKeywords =
        ["vEthernet", "Hyper-V", "VMware", "VirtualBox", "WSL", "Loopback"];

    /// <summary>Up Ethernet/Wi-Fi adapters only, non-virtual; those with an IPv4 gateway first.</summary>
    public static IReadOnlyList<IPAddress> Rank(IEnumerable<NicInfo> nics) =>
    [
        .. UsableNics(nics)
            .SelectMany(n => n.IPv4)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct(),
    ];

    /// <summary>Address and mask of every usable adapter (same filter as <see cref="Rank"/>); addresses without a known mask are skipped.</summary>
    public static IReadOnlyList<(IPAddress Ip, IPAddress Mask)> GetLocalNets(IEnumerable<NicInfo> nics)
    {
        var nets = new List<(IPAddress, IPAddress)>();
        foreach (var n in UsableNics(nics))
        {
            if (n.IPv4Masks is not { } masks)
                continue;
            for (int i = 0; i < n.IPv4.Count && i < masks.Count; i++)
            {
                var ip = n.IPv4[i];
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    nets.Add((ip, masks[i]));
            }
        }
        return nets;
    }

    public static IReadOnlyList<(IPAddress Ip, IPAddress Mask)> GetLocalNets() => GetLocalNets(ReadNics());

    private static IEnumerable<NicInfo> UsableNics(IEnumerable<NicInfo> nics) =>
        nics
            .Where(n => n.IsUp
                && n.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                && !IsVirtual(n))
            .OrderByDescending(n => n.HasIPv4Gateway); // stable: keeps input order within a group

    /// <summary>The directed-broadcast address of <paramref name="ip"/> on the subnet given by <paramref name="mask"/>.</summary>
    public static IPAddress BroadcastOf(IPAddress ip, IPAddress mask)
    {
        var a = ip.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = new byte[a.Length];
        for (int i = 0; i < b.Length; i++)
            b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    /// <summary>
    /// Subnet broadcast address of every usable adapter (same filter as <see cref="Rank"/>; addresses without a known
    /// mask are skipped), plus the limited broadcast <c>255.255.255.255</c>. Distinct.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetBroadcastTargets(IEnumerable<NicInfo> nics)
    {
        var targets = new List<IPAddress>();
        foreach (var n in UsableNics(nics))
        {
            if (n.IPv4Masks is not { } masks)
                continue;
            for (int i = 0; i < n.IPv4.Count && i < masks.Count; i++)
            {
                var ip = n.IPv4[i];
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    targets.Add(BroadcastOf(ip, masks[i]));
            }
        }
        targets.Add(IPAddress.Broadcast);
        return [.. targets.Distinct()];
    }

    public static IReadOnlyList<IPAddress> GetBroadcastTargets() => GetBroadcastTargets(ReadNics());

    public static IReadOnlyList<IPAddress> GetCandidates() => Rank(ReadNics());

    private static List<NicInfo> ReadNics()
    {
        var infos = new List<NicInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = ni.GetIPProperties();
            var v4 = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
            bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                && !g.Address.Equals(IPAddress.Any));
            infos.Add(new NicInfo(ni.Name, ni.Description, ni.OperationalStatus == OperationalStatus.Up, gw,
                [.. v4.Select(u => u.Address)], ni.NetworkInterfaceType, [.. v4.Select(u => u.IPv4Mask)]));
        }
        return infos;
    }

    // macOS: tunnels (utun), AirDrop/Wi-Fi Direct (awdl, llw), VM/Internet-sharing bridges (bridge, vmenet, ap), anpi.
    private static readonly string[] VirtualNamePrefixes = ["utun", "awdl", "llw", "anpi", "bridge", "vmenet"];

    private static bool IsVirtual(NicInfo n) =>
        VirtualKeywords.Any(k =>
            n.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
            n.Description.Contains(k, StringComparison.OrdinalIgnoreCase))
        || VirtualNamePrefixes.Any(p => n.Name.StartsWith(p, StringComparison.Ordinal))
        || IsApInterface(n.Name);

    /// <summary>macOS Wi-Fi access-point interfaces: <c>ap1</c>, <c>ap2</c>…</summary>
    private static bool IsApInterface(string name) =>
        name.Length > 2 && name.StartsWith("ap", StringComparison.Ordinal) && name.Skip(2).All(char.IsAsciiDigit);
}
