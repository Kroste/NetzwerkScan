using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>
/// SSDP/UPnP-Discovery (Multicast 239.255.255.250:1900). Ein M-SEARCH "ssdp:discover"
/// veranlasst UPnP-Geräte (Router, Smart-TVs, Media-Server, viel IoT) zu einer
/// HTTP-artigen Unicast-Antwort mit SERVER-Kennung und Geräte-/Service-Typ (ST/USN).
/// </summary>
public sealed class SsdpDiscovery(ILogger<SsdpDiscovery> log)
{
    private static readonly IPAddress Group = IPAddress.Parse("239.255.255.250");
    private const int Port = 1900;

    public async Task DiscoverAsync(ConcurrentDictionary<string, SsdpRecord> sink, int listenMs, CancellationToken ct)
    {
        log.LogInformation("SSDP-Discovery startet (Listen {Ms} ms)", listenMs);
        var tasks = NetInterfaces.LocalUnicastV4().Select(local => RunOnInterfaceAsync(local, sink, listenMs, ct));
        await Task.WhenAll(tasks);
        log.LogInformation("SSDP-Discovery beendet: {Count} Host(s)", sink.Count);
    }

    private async Task RunOnInterfaceAsync(
        IPAddress local, ConcurrentDictionary<string, SsdpRecord> sink, int listenMs, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(local, 0));
            udp.Ttl = 2;

            string mSearch =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                "ST: ssdp:all\r\n\r\n";
            await udp.SendAsync(Encoding.ASCII.GetBytes(mSearch), new IPEndPoint(Group, Port), ct);

            using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listenCts.CancelAfter(listenMs);
            try
            {
                while (!listenCts.IsCancellationRequested)
                {
                    var r = await udp.ReceiveAsync(listenCts.Token);
                    ParseInto(Encoding.ASCII.GetString(r.Buffer), r.RemoteEndPoint.Address, sink);
                }
            }
            catch (OperationCanceledException) { /* Listen-Fenster zu Ende */ }
        }
        catch (Exception ex) { log.LogDebug(ex, "SSDP auf {Local} fehlgeschlagen", local); }
    }

    private void ParseInto(string resp, IPAddress sender, ConcurrentDictionary<string, SsdpRecord> sink)
    {
        string? server = null, st = null;
        foreach (var line in resp.Split('\n'))
        {
            if (line.StartsWith("SERVER:", StringComparison.OrdinalIgnoreCase)) server = line[7..].Trim();
            else if (line.StartsWith("ST:", StringComparison.OrdinalIgnoreCase)) st ??= line[3..].Trim();
            else if (line.StartsWith("USN:", StringComparison.OrdinalIgnoreCase) && st is null) st = line[4..].Trim();
        }

        var rec = sink.GetOrAdd(sender.ToString(), _ => new SsdpRecord());
        if (server is not null) rec.Server = server;
        var devType = DeviceTypeFrom(st);
        if (devType is not null) rec.DeviceType = devType;
        log.LogDebug("SSDP {Ip}: {Server} / {Type}", sender, server, rec.DeviceType);
    }

    /// <summary>UPnP-Geräte-/Service-Typ auf Klartext mappen.</summary>
    private static string? DeviceTypeFrom(string? st)
    {
        if (string.IsNullOrWhiteSpace(st)) return null;
        if (st.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase)
            || st.Contains("WANConnectionDevice", StringComparison.OrdinalIgnoreCase)
            || st.Contains("WANDevice", StringComparison.OrdinalIgnoreCase)) return "Router";
        if (st.Contains("MediaServer", StringComparison.OrdinalIgnoreCase)) return "Media-Server";
        if (st.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase)) return "Smart-TV/Renderer";
        if (st.Contains("dial-multiscreen", StringComparison.OrdinalIgnoreCase)) return "Smart-TV";
        if (st.Contains("Printer", StringComparison.OrdinalIgnoreCase)) return "Drucker";
        return null;
    }
}
