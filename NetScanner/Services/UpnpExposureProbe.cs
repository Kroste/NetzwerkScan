using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NetScanner.Models;

namespace NetScanner.Services;

/// <summary>
/// Fragt per UPnP-IGD den Router nach seinen aktiven Portweiterleitungen ab
/// (GetGenericPortMappingEntry) und nach der oeffentlichen IP (GetExternalIPAddress).
///
/// Damit wird sichtbar, welche Ports aus dem Internet erreichbar sind und auf welches
/// interne Geraet sie zeigen — die zentrale Frage fuer "ist meine Kamera von aussen offen?".
/// Reine Leseabfrage: es werden KEINE Weiterleitungen angelegt oder geaendert.
/// </summary>
public sealed class UpnpExposureProbe(ILogger<UpnpExposureProbe> log)
{
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 1900);

    public sealed record ExposureResult(string? ExternalIp, IReadOnlyList<PortMapping> Mappings, bool IgdFound);

    public async Task<ExposureResult> ProbeAsync(int searchMs, CancellationToken ct)
    {
        var location = await FindIgdLocationAsync(searchMs, ct);
        if (location is null)
        {
            log.LogInformation("Kein UPnP-IGD gefunden (Router antwortet nicht oder UPnP aus)");
            return new ExposureResult(await PublicIpFallbackAsync(ct), [], false);
        }

        var wan = await GetWanServiceAsync(location, ct);
        if (wan is not { } svc)
        {
            log.LogInformation("IGD gefunden, aber kein WAN-Connection-Service in {Loc}", location);
            return new ExposureResult(await PublicIpFallbackAsync(ct), [], true);
        }

        string? extIp = await GetExternalIpAsync(svc.ControlUrl, svc.ServiceType, ct)
                        ?? await PublicIpFallbackAsync(ct);
        var mappings = await GetMappingsAsync(svc.ControlUrl, svc.ServiceType, ct);
        log.LogInformation("Exposition: {Count} Portweiterleitung(en), externe IP {Ip}",
            mappings.Count, extIp ?? "?");
        return new ExposureResult(extIp, mappings, true);
    }

    // ---------------------------------------------------- IGD-Suche (SSDP)

    private async Task<string?> FindIgdLocationAsync(int searchMs, CancellationToken ct)
    {
        foreach (var local in NetInterfaces.LocalUnicastV4())
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(local, 0));
                udp.Ttl = 2;

                string mSearch =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
                await udp.SendAsync(Encoding.ASCII.GetBytes(mSearch), Multicast, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(searchMs);
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var r = await udp.ReceiveAsync(cts.Token);
                        string resp = Encoding.ASCII.GetString(r.Buffer);
                        if (HeaderValue(resp, "LOCATION") is { } loc
                            && (resp.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase)
                                || resp.Contains("WANConnection", StringComparison.OrdinalIgnoreCase)))
                            return loc;
                    }
                }
                catch (OperationCanceledException) { /* Suchfenster zu Ende */ }
            }
            catch (Exception ex) { log.LogDebug(ex, "IGD-Suche auf {Local} fehlgeschlagen", local); }
        }
        return null;
    }

    // ---------------------------------------------------- Device-Description

    private async Task<(string ControlUrl, string ServiceType)?> GetWanServiceAsync(
        string location, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string xml = await http.GetStringAsync(location, ct);
            var doc = XDocument.Parse(xml);

            var locUri = new Uri(location);
            string baseUrl = $"{locUri.Scheme}://{locUri.Authority}";

            foreach (var svc in doc.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                string? type = svc.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value;
                string? ctrl = svc.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
                if (type is null || string.IsNullOrWhiteSpace(ctrl)) continue;

                if (type.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase))
                {
                    string controlUrl = ctrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? ctrl
                        : baseUrl + (ctrl.StartsWith('/') ? "" : "/") + ctrl;
                    return (controlUrl, type.Trim());
                }
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Device-Description {Loc} nicht lesbar", location); }
        return null;
    }

    // ---------------------------------------------------- SOAP-Abfragen

    private async Task<IReadOnlyList<PortMapping>> GetMappingsAsync(
        string controlUrl, string serviceType, CancellationToken ct)
    {
        var list = new List<PortMapping>();
        for (int i = 0; i < 100; i++)   // harte Obergrenze gegen Endlosschleife
        {
            ct.ThrowIfCancellationRequested();
            string body = SoapEnvelope(serviceType, "GetGenericPortMappingEntry",
                $"<NewPortMappingIndex>{i}</NewPortMappingIndex>");
            var resp = await SoapAsync(controlUrl, serviceType, "GetGenericPortMappingEntry", body, ct);
            if (resp is null) break;            // SOAP-Fault 713 = Ende der Liste
            var pm = ParseMapping(resp);
            if (pm is null) break;
            list.Add(pm);
        }
        return list;
    }

    private async Task<string?> GetExternalIpAsync(string controlUrl, string serviceType, CancellationToken ct)
    {
        string body = SoapEnvelope(serviceType, "GetExternalIPAddress", "");
        var resp = await SoapAsync(controlUrl, serviceType, "GetExternalIPAddress", body, ct);
        if (resp is null) return null;
        try
        {
            var ip = XDocument.Parse(resp).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
            return string.IsNullOrWhiteSpace(ip) ? null : ip;
        }
        catch { return null; }
    }

    private static async Task<string?> SoapAsync(
        string controlUrl, string serviceType, string action, string body, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml")
            };
            req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");
            using var resp = await http.SendAsync(req, ct);
            // 500 = SOAP-Fault (z. B. Array-Index am Ende) -> als "kein Eintrag" behandeln.
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch { return null; }
    }

    private static PortMapping? ParseMapping(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            string? Val(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value;

            string? extPort = Val("NewExternalPort");
            string? intClient = Val("NewInternalClient");
            if (extPort is null || string.IsNullOrWhiteSpace(intClient)) return null;

            return new PortMapping(
                int.TryParse(extPort, out var ep) ? ep : 0,
                (Val("NewProtocol") ?? "?").ToUpperInvariant(),
                intClient,
                int.TryParse(Val("NewInternalPort"), out var ip) ? ip : 0,
                Val("NewPortMappingDescription"),
                Val("NewEnabled") is "1" or "true");
        }
        catch { return null; }
    }

    // ---------------------------------------------------- Helpers

    private static string SoapEnvelope(string serviceType, string action, string inner) =>
        "<?xml version=\"1.0\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
        "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
        $"<s:Body><u:{action} xmlns:u=\"{serviceType}\">{inner}</u:{action}></s:Body></s:Envelope>";

    private static string? HeaderValue(string resp, string name)
    {
        foreach (var line in resp.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase))
                return t[(name.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Oeffentliche IP ueber einen externen Dienst, falls das IGD keine liefert.</summary>
    private async Task<string?> PublicIpFallbackAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ip = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim();
            return IPAddress.TryParse(ip, out _) ? ip : null;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Oeffentliche-IP-Fallback fehlgeschlagen");
            return null;
        }
    }
}
