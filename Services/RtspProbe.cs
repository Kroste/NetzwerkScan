using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>
/// Prueft per RTSP-OPTIONS, ob an einem Port ein RTSP-Server lauscht, und
/// erzeugt Stream-URL-Kandidaten nach oeffentlich dokumentierten Hersteller-Mustern.
/// Diese Klasse raet selbst keine Passwoerter — das optionale Default-Login-Audit
/// (offene Streams + Werks-Logins) steckt in <see cref="CredentialAuditor"/>.
/// </summary>
public sealed class RtspProbe(ILogger<RtspProbe> log)
{
    /// <summary>Dokumentierte Standard-RTSP-Pfade je Hersteller (Haupt-/Substream).</summary>
    public static IReadOnlyList<string> PathsForVendor(string? vendor) => vendor switch
    {
        not null when vendor.Contains("Hikvision", StringComparison.OrdinalIgnoreCase)
            => ["/Streaming/Channels/101", "/Streaming/Channels/102"],
        not null when vendor.Contains("Dahua", StringComparison.OrdinalIgnoreCase)
            => ["/cam/realmonitor?channel=1&subtype=0", "/cam/realmonitor?channel=1&subtype=1"],
        not null when vendor.Contains("Axis", StringComparison.OrdinalIgnoreCase)
            => ["/axis-media/media.amp"],
        not null when vendor.Contains("Reolink", StringComparison.OrdinalIgnoreCase)
            => ["/h264Preview_01_main", "/h264Preview_01_sub"],
        not null when vendor.Contains("Amcrest", StringComparison.OrdinalIgnoreCase)
            => ["/cam/realmonitor?channel=1&subtype=0"],
        // Generische Fallbacks, decken viele ONVIF-Kameras ab.
        _ => ["/", "/live", "/stream1", "/11", "/Streaming/Channels/101"]
    };

    /// <summary>
    /// Sendet RTSP OPTIONS an ip:port und liest die Statuszeile.
    /// Liefert (lauscht?, verlangtAuth?).
    /// </summary>
    public async Task<(bool IsRtsp, bool RequiresAuth)> ProbeAsync(
        IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient(ip.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(ip, port, cts.Token);

            await using var stream = client.GetStream();
            string req = $"OPTIONS rtsp://{ip}:{port}/ RTSP/1.0\r\nCSeq: 1\r\n" +
                         "User-Agent: NetScanner\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(req), cts.Token);

            var buf = new byte[512];
            int n = await stream.ReadAsync(buf, cts.Token);
            string resp = Encoding.ASCII.GetString(buf, 0, n);

            bool isRtsp = resp.StartsWith("RTSP/1.0", StringComparison.Ordinal);
            bool needsAuth = resp.Contains("401", StringComparison.Ordinal)
                          || resp.Contains("WWW-Authenticate", StringComparison.OrdinalIgnoreCase);
            if (isRtsp)
                log.LogInformation("RTSP erkannt auf {Ip}:{Port} (Auth: {Auth})", ip, port, needsAuth);
            return (isRtsp, needsAuth);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "RTSP-Probe {Ip}:{Port} fehlgeschlagen", ip, port);
            return (false, false);
        }
    }

    /// <summary>Baut eine RTSP-URL inkl. optionaler Credentials zusammen.</summary>
    public static string BuildUri(IPAddress ip, int port, string path,
        string? user = null, string? pass = null)
    {
        string cred = !string.IsNullOrEmpty(user)
            ? $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass ?? "")}@"
            : "";
        string sep = path.StartsWith('/') ? "" : "/";
        return $"rtsp://{cred}{ip}:{port}{sep}{path}";
    }
}
