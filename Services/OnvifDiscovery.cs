using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NetScanner.Models;

namespace NetScanner.Services;

public interface ICameraDiscovery
{
    /// <summary>Findet ONVIF-Geraete per WS-Discovery (Multicast) ueber alle aktiven Interfaces.</summary>
    Task<IReadOnlyList<CameraInfo>> DiscoverAsync(int listenMs, CancellationToken ct);
}

/// <summary>
/// ONVIF-Geraeteerkennung nach WS-Discovery: SOAP-"Probe" an die Multicast-Gruppe
/// 239.255.255.250:3702. Antwortende Geraete ("ProbeMatch") liefern ihre Service-URL
/// (XAddrs) und Scopes (Hersteller/Modell/Name).
/// </summary>
public sealed class OnvifDiscovery(ILogger<OnvifDiscovery> log) : ICameraDiscovery
{
    private static readonly IPAddress MulticastAddr = IPAddress.Parse("239.255.255.250");
    private const int WsdPort = 3702;

    public async Task<IReadOnlyList<CameraInfo>> DiscoverAsync(int listenMs, CancellationToken ct)
    {
        log.LogInformation("ONVIF WS-Discovery startet (Listen {Ms} ms)", listenMs);
        var found = new Dictionary<string, CameraInfo>();   // dedupliziert per Adresse

        // Auf JEDEM aktiven IPv4-Interface separat senden/lauschen
        // (Multicast ist interface-gebunden; ein einzelner Socket reicht nicht zuverlaessig).
        var interfaces = LocalUnicastV4().ToList();
        var tasks = interfaces.Select(local => ProbeOnInterfaceAsync(local, listenMs, found, ct));
        await Task.WhenAll(tasks);

        log.LogInformation("WS-Discovery beendet: {Count} ONVIF-Geraet(e)", found.Count);
        return found.Values.ToList();
    }

    private async Task ProbeOnInterfaceAsync(
        IPAddress local, int listenMs, Dictionary<string, CameraInfo> found, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(local, 0));
            udp.Ttl = 2;

            string probe = BuildProbe(Guid.NewGuid());
            byte[] data = Encoding.UTF8.GetBytes(probe);
            await udp.SendAsync(data, new IPEndPoint(MulticastAddr, WsdPort), ct);
            log.LogDebug("Probe gesendet ueber {Local}", local);

            using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listenCts.CancelAfter(listenMs);
            try
            {
                while (!listenCts.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(listenCts.Token);
                    string xml = Encoding.UTF8.GetString(result.Buffer);
                    var info = ParseProbeMatch(xml, result.RemoteEndPoint.Address);
                    if (info is not null)
                    {
                        lock (found) found[info.Address.ToString()] = info;
                        log.LogInformation("ONVIF-Geraet: {Ip} ({Vendor})", info.Address, info.Vendor ?? "?");
                    }
                }
            }
            catch (OperationCanceledException) { /* Listen-Fenster abgelaufen */ }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "WS-Discovery auf {Local} fehlgeschlagen", local);
        }
    }

    /// <summary>SOAP-Probe-Envelope nach WS-Discovery, Geraetetyp NetworkVideoTransmitter.</summary>
    private static string BuildProbe(Guid messageId) =>
        $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                    xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                    xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                    xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
          <e:Header>
            <w:MessageID>uuid:{{messageId}}</w:MessageID>
            <w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
            <w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
          </e:Header>
          <e:Body>
            <d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe>
          </e:Body>
        </e:Envelope>
        """;

    private static CameraInfo? ParseProbeMatch(string xml, IPAddress remote)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace d = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
            var match = doc.Descendants(d + "ProbeMatch").FirstOrDefault();
            if (match is null) return null;

            string? xaddrs = match.Element(d + "XAddrs")?.Value?.Split(' ').FirstOrDefault();
            var scopes = (match.Element(d + "Scopes")?.Value ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Adresse bevorzugt aus XAddrs (echte Geraete-IP), sonst Absender.
            IPAddress addr = remote;
            if (Uri.TryCreate(xaddrs, UriKind.Absolute, out var u)
                && IPAddress.TryParse(u.Host, out var fromXaddr))
                addr = fromXaddr;

            return new CameraInfo
            {
                Address = addr,
                Source = CameraSource.OnvifDiscovery,
                OnvifServiceUri = xaddrs,
                Scopes = scopes,
                Vendor = VendorFromScopes(scopes)
            };
        }
        catch { return null; }
    }

    /// <summary>Hersteller/Name aus ONVIF-Scopes ziehen (onvif://www.onvif.org/name/... etc.).</summary>
    private static string? VendorFromScopes(IReadOnlyList<string> scopes)
    {
        foreach (var key in new[] { "/name/", "/hardware/", "/mfr/", "/manufacturer/" })
        {
            var s = scopes.FirstOrDefault(x => x.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (s is not null)
                return Uri.UnescapeDataString(s[(s.IndexOf(key, StringComparison.OrdinalIgnoreCase) + key.Length)..]);
        }
        return null;
    }

    private static IEnumerable<IPAddress> LocalUnicastV4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!ni.Supports(NetworkInterfaceComponent.IPv4)) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return ua.Address;
        }
    }
}
