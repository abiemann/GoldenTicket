using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GoldenTicket.ConnectivitySpike.Networking;

/// <summary>One usable private network address the host could bind to.</summary>
public sealed record LanInterface(string Name, string Description, IPAddress Address)
{
    public override string ToString() => $"{Address}  ({Name} - {Description})";
}

/// <summary>
/// Finds the private LAN addresses this machine could serve on. DESIGN 18.5: bind only the selected
/// private interface, and never expose the host on a public network interface.
/// </summary>
public static class LanInterfaces
{
    /// <summary>
    /// Up, non-loopback interfaces with an IPv4 address in a private range. Link-local (169.254/16)
    /// is excluded: it means DHCP failed, and the phone will not reliably reach it.
    /// </summary>
    public static IReadOnlyList<LanInterface> Discover()
    {
        var found = new List<LanInterface>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!IsPrivate(unicast.Address)) continue;

                found.Add(new LanInterface(adapter.Name, adapter.Description, unicast.Address));
            }
        }

        return found;
    }

    /// <summary>
    /// RFC 1918 ranges only. A public address is refused outright rather than warned about, because
    /// DESIGN 18.5 forbids exposing the host beyond the private network.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            172 => octets[1] >= 16 && octets[1] <= 31,
            192 => octets[1] == 168,
            _ => false,
        };
    }
}
