using System.Net;
using System.Net.NetworkInformation;

namespace PSRemoteSub.Core.Net;

/// <summary>One network adapter. <c>IPv4Masks</c> runs parallel to <c>IPv4</c> (same index = same address); null when unknown.</summary>
public sealed record NicInfo(
    string Name, string Description, bool IsUp, bool HasIPv4Gateway,
    IReadOnlyList<IPAddress> IPv4, NetworkInterfaceType Type,
    IReadOnlyList<IPAddress>? IPv4Masks = null);
