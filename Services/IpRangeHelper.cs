using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetScanner.Services;

/// <summary>
/// Hilfsfunktionen rund um IP-Bereiche und die ARP-Tabelle.
/// Bewusst ohne Raw-Sockets, damit keine erhoehten Rechte noetig sind.
/// </summary>
public static class IpRangeHelper
{
    /// <summary>
    /// Expandiert eine CIDR-Notation (z. B. "192.168.10.0/24") in alle Host-Adressen
    /// (ohne Netz- und Broadcast-Adresse). Nur IPv4.
    /// </summary>
    public static IReadOnlyList<IPAddress> ExpandCidr(string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp)
            || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)
            throw new FormatException($"Ungueltige CIDR-Angabe: '{cidr}'");

        uint baseAddr = ToUInt(baseIp);
        uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        uint network = baseAddr & mask;
        uint broadcast = network | ~mask;

        var list = new List<IPAddress>();
        // /31 und /32 haben keine "normalen" Hosts -> Sonderfall: alle Adressen nehmen.
        if (prefix >= 31)
        {
            for (uint a = network; a <= broadcast; a++) list.Add(FromUInt(a));
            return list;
        }
        for (uint a = network + 1; a < broadcast; a++) list.Add(FromUInt(a));
        return list;
    }

    /// <summary>Prueft eine CIDR-Angabe (IPv4) auf Gueltigkeit, ohne zu expandieren oder
    /// zu werfen — fuer die Eingabe-Validierung vor dem Scan-Start.</summary>
    public static bool IsValidCidr(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr)) return false;
        var parts = cidr.Trim().Split('/', 2);
        return parts.Length == 2
            && IPAddress.TryParse(parts[0], out var ip)
            && ip.AddressFamily == AddressFamily.InterNetwork
            && int.TryParse(parts[1], out var prefix)
            && prefix is >= 0 and <= 32;
    }

    /// <summary>Ermittelt die lokalen IPv4-Subnetze als CIDR (eines pro aktivem Interface).</summary>
    public static IReadOnlyList<string> LocalSubnets()
    {
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                int prefix = ua.PrefixLength is > 0 and <= 32 ? ua.PrefixLength : 24;
                uint addr = ToUInt(ua.Address);
                uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
                var network = FromUInt(addr & mask);
                result.Add($"{network}/{prefix}");
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>
    /// Liest die ARP-/Neighbor-Tabelle des Systems und liefert IP -> MAC.
    /// Linux: 'ip neigh' (Fallback /proc/net/arp), Windows: 'arp -a'.
    /// Wird nach dem Ping-Sweep aufgerufen, dann sind die Eintraege gefuellt.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadArpTableAsync(
        CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                ParseWindowsArp(await RunAsync("arp", "-a", ct), map);
            else
                ParseLinuxNeigh(await RunAsync("ip", "neigh", ct), map);
        }
        catch
        {
            // ARP ist optional (nur fuer MAC/Vendor); Fehler hier sind nicht fatal.
        }
        return map;
    }

    private static void ParseWindowsArp(string output, Dictionary<string, string> map)
    {
        // Zeilenformat:  192.168.10.5          aa-bb-cc-dd-ee-ff     dynamisch
        foreach (var line in output.Split('\n'))
        {
            var t = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 2 && IPAddress.TryParse(t[0], out _) && t[1].Contains('-'))
                map[t[0]] = NormalizeMac(t[1]);
        }
    }

    private static void ParseLinuxNeigh(string output, Dictionary<string, string> map)
    {
        // Zeilenformat:  192.168.10.5 dev eno1 lladdr aa:bb:cc:dd:ee:ff REACHABLE
        foreach (var line in output.Split('\n'))
        {
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int i = Array.IndexOf(t, "lladdr");
            if (i >= 0 && i + 1 < t.Length && IPAddress.TryParse(t[0], out _))
                map[t[0]] = NormalizeMac(t[i + 1]);
        }
    }

    private static async Task<string> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return output;
    }

    public static string NormalizeMac(string mac) =>
        mac.Replace('-', ':').ToUpperInvariant();

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt(uint v) =>
        new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
}
