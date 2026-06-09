using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>
/// Liest Dienst-Banner zur Geraeteidentifikation — ohne erhoehte Rechte:
///   - SSH: der Server sendet beim Connect sofort eine Kennung ("SSH-2.0-OpenSSH_9.6 Ubuntu").
///   - HTTP: aus der Antwort wird der "Server:"-Header gezogen (nginx, Microsoft-IIS, Boa, ...).
/// </summary>
public sealed class BannerGrabber(ILogger<BannerGrabber> log)
{
    /// <summary>Liest die SSH-Identifikationszeile (RFC 4253: Server sendet sie zuerst).</summary>
    public async Task<string?> GrabSshAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient(ip.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(ip, port, cts.Token);

            await using var stream = client.GetStream();
            var buf = new byte[256];
            int n = await stream.ReadAsync(buf, cts.Token);
            var line = Encoding.ASCII.GetString(buf, 0, n).Split('\r', '\n')[0].Trim();
            if (line.StartsWith("SSH-", StringComparison.Ordinal))
            {
                log.LogDebug("SSH-Banner {Ip}: {Banner}", ip, line);
                return line;
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "SSH-Banner {Ip}:{Port} fehlgeschlagen", ip, port); }
        return null;
    }

    /// <summary>Holt den HTTP-"Server:"-Header per HEAD-Request.</summary>
    public async Task<string?> GrabHttpServerAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient(ip.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(ip, port, cts.Token);

            await using var stream = client.GetStream();
            string req = $"HEAD / HTTP/1.0\r\nHost: {ip}\r\nUser-Agent: NetScanner\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(req), cts.Token);

            var buf = new byte[2048];
            int n = await stream.ReadAsync(buf, cts.Token);
            string resp = Encoding.ASCII.GetString(buf, 0, n);

            foreach (var raw in resp.Split('\n'))
            {
                if (raw.StartsWith("Server:", StringComparison.OrdinalIgnoreCase))
                {
                    var server = raw[7..].Trim();
                    log.LogDebug("HTTP-Server {Ip}:{Port}: {Server}", ip, port, server);
                    return server;
                }
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "HTTP-Banner {Ip}:{Port} fehlgeschlagen", ip, port); }
        return null;
    }
}
