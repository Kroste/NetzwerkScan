using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetScanner.Services;

/// <summary>Gemeinsame Netzwerk-Helfer für die Multicast-Discovery-Dienste.</summary>
internal static class NetInterfaces
{
    /// <summary>Alle aktiven, nicht-Loopback IPv4-Unicast-Adressen der lokalen Interfaces.</summary>
    public static IEnumerable<IPAddress> LocalUnicastV4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!ni.Supports(NetworkInterfaceComponent.IPv4)) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(ua.Address))
                    yield return ua.Address;
        }
    }
}

/// <summary>Aggregiertes mDNS-Resultat pro IP.</summary>
public sealed class MdnsRecord
{
    public string? Name { get; set; }
    public HashSet<string> Services { get; } = [];
}

/// <summary>Aggregiertes SSDP/UPnP-Resultat pro IP.</summary>
public sealed class SsdpRecord
{
    public string? Server { get; set; }
    public string? DeviceType { get; set; }
}
