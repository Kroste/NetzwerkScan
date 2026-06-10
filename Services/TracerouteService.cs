using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetScanner.Services;

/// <summary>Ein einzelner Hop auf dem Weg zum Ziel.</summary>
public sealed record TraceHop(int Ttl, IPAddress? Address, long RoundtripMs, IPStatus Status)
{
    public bool Reached => Status == IPStatus.Success;
    public bool TimedOut => Status == IPStatus.TimedOut || Address is null || Address.Equals(IPAddress.Any);
    public string AddressDisplay => TimedOut ? "* * *" : Address!.ToString();
    public string RttDisplay => TimedOut ? "—" : $"{RoundtripMs} ms";
}

/// <summary>
/// Traceroute ohne externes Tool: ICMP-Echo mit schrittweise erhoehter TTL. Jeder Router
/// auf dem Pfad dekrementiert die TTL und antwortet bei 0 mit "Time Exceeded" — daraus
/// faellt seine IP. Cross-platform (kein tracert.exe-Parsing).
///
/// Hinweis Linux: Der unprivilegierte ICMP-Pfad liefert die Hop-Adresse bei TtlExpired
/// nicht immer zurueck (Kernel-Limitierung des Datagram-ICMP-Sockets). Unter Windows ist
/// die Hop-IP zuverlaessig. Auf Bazzite ggf. als root oder per System-traceroute gegenpruefen.
/// </summary>
public sealed class TracerouteService
{
    public async Task<IReadOnlyList<TraceHop>> TraceAsync(
        string target,
        int maxHops = 30,
        int timeoutMs = 1500,
        IProgress<TraceHop>? progress = null,
        CancellationToken ct = default)
    {
        var dest = await ResolveAsync(target, ct);

        var hops = new List<TraceHop>(maxHops);
        var buffer = new byte[32];                // beliebiger Payload
        using var ping = new Ping();

        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            var options = new PingOptions(ttl, dontFragment: true);

            TraceHop hop;
            try
            {
                var reply = await ping.SendPingAsync(
                    dest, TimeSpan.FromMilliseconds(timeoutMs), buffer, options, ct);

                // Bei TtlExpired ist reply.Address der antwortende Router; bei Success das Ziel.
                hop = new TraceHop(ttl, reply.Address, reply.RoundtripTime, reply.Status);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                hop = new TraceHop(ttl, null, -1, IPStatus.Unknown);
            }

            hops.Add(hop);
            progress?.Report(hop);

            if (hop.Reached) break;               // Ziel erreicht -> fertig
        }

        return hops;
    }

    /// <summary>Zielname/-IP zu einer IPv4-Adresse aufloesen.</summary>
    private static async Task<IPAddress> ResolveAsync(string target, CancellationToken ct)
    {
        target = target.Trim();
        if (IPAddress.TryParse(target, out var ip))
            return ip;

        var addrs = await Dns.GetHostAddressesAsync(target, ct);
        return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addrs.FirstOrDefault()
               ?? throw new InvalidOperationException($"Konnte '{target}' nicht aufloesen.");
    }

    /// <summary>Default-Gateway des aktiven Interfaces (oder null, wenn keins gefunden).</summary>
    public static IPAddress? DefaultGateway()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a is not null
                                 && a.AddressFamily == AddressFamily.InterNetwork
                                 && !a.Equals(IPAddress.Any));
    }
}
