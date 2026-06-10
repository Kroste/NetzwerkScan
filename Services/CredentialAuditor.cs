using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NetScanner.Models;

namespace NetScanner.Services;

/// <summary>
/// Optionales Sicherheits-Audit: prueft, ob ein RTSP-Stream offen ist bzw. ob ein
/// Geraet (Kamera, Router) mit einem dokumentierten Werks-Login zugaenglich ist.
///
/// GEDACHT FUER DAS EIGENE NETZ. Default-Credential-Checks sind Standard in
/// Sicherheits-Audits — gegen fremde Systeme ohne Erlaubnis koennen sie aber rechtlich
/// relevant sein (in DE u. a. §202c StGB). Das Feature ist daher bewusst opt-in.
///
/// Die Liste enthaelt nur die gaengigsten, oeffentlich dokumentierten Werks-Logins
/// (kein Brute-Force). RTSP-Auth (Basic/Digest) ist selbst implementiert; HTTP-Auth
/// (Basic/Digest) uebernimmt der HttpClient.
/// </summary>
public sealed class CredentialAuditor(ILogger<CredentialAuditor> log)
{
    /// <summary>Kuratierte, gaengige Werks-Logins (User, Passwort).</summary>
    public static readonly IReadOnlyList<(string User, string Pass)> CommonDefaults =
    [
        ("admin", "admin"), ("admin", ""), ("admin", "12345"), ("admin", "123456"),
        ("admin", "1234"), ("admin", "password"), ("admin", "admin123"), ("admin", "9999"),
        ("admin", "888888"), ("admin", "default"), ("root", "root"), ("root", "admin"),
        ("root", "12345"), ("root", "pass"), ("user", "user"), ("supervisor", "supervisor"),
        ("service", "service"),
    ];

    // ----------------------------------------------------------------- RTSP

    /// <summary>Prueft einen RTSP-Endpunkt: offen, per Werks-Login zugaenglich oder gesichert.</summary>
    public async Task<(AuthFinding Finding, string? User, string? Pass)> AuditRtspAsync(
        IPAddress ip, int port, string path, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var (status, auth) = await DescribeAsync(ip, port, path, null, timeoutMs, ct);
            if (status == 200) return (AuthFinding.Open, null, null);       // ohne Auth erreichbar
            if (status != 401 || auth is null) return (AuthFinding.NotChecked, null, null);

            foreach (var (user, pass) in CommonDefaults)
            {
                ct.ThrowIfCancellationRequested();
                var header = BuildRtspAuth(auth, user, pass, ip, port, path);
                if (header is null) break;                                  // unbekanntes Schema
                var (s2, _) = await DescribeAsync(ip, port, path, header, timeoutMs, ct);
                if (s2 == 200)
                {
                    log.LogWarning("RTSP-Werks-Login wirksam auf {Ip}:{Port} ({User})", ip, port, user);
                    return (AuthFinding.DefaultCredentials, user, pass);
                }
            }
            return (AuthFinding.Secured, null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "RTSP-Audit {Ip}:{Port} fehlgeschlagen", ip, port);
            return (AuthFinding.NotChecked, null, null);
        }
    }

    private static async Task<(int Status, string? AuthHeader)> DescribeAsync(
        IPAddress ip, int port, string path, string? authorization, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(ip.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        await client.ConnectAsync(ip, port, cts.Token);
        await using var stream = client.GetStream();

        string url = RtspUrl(ip, port, path);
        var sb = new StringBuilder()
            .Append($"DESCRIBE {url} RTSP/1.0\r\nCSeq: 2\r\n")
            .Append("User-Agent: NetScanner\r\nAccept: application/sdp\r\n");
        if (authorization is not null) sb.Append($"Authorization: {authorization}\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), cts.Token);

        var buf = new byte[1024];
        int n = await stream.ReadAsync(buf, cts.Token);
        string resp = Encoding.ASCII.GetString(buf, 0, n);
        return (ParseStatus(resp), ExtractHeader(resp, "WWW-Authenticate"));
    }

    private static string? BuildRtspAuth(string wwwAuth, string user, string pass,
        IPAddress ip, int port, string path)
    {
        string url = RtspUrl(ip, port, path);

        if (wwwAuth.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
            return $"Basic {token}";
        }
        if (wwwAuth.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
        {
            var realm = Directive(wwwAuth, "realm");
            var nonce = Directive(wwwAuth, "nonce");
            if (realm is null || nonce is null) return null;

            string ha1 = Md5($"{user}:{realm}:{pass}");
            string ha2 = Md5($"DESCRIBE:{url}");
            var qop = Directive(wwwAuth, "qop");

            if (qop is not null && qop.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                string cnonce = Guid.NewGuid().ToString("N")[..16];
                const string nc = "00000001";
                string resp = Md5($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
                return $"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", " +
                       $"uri=\"{url}\", qop=auth, nc={nc}, cnonce=\"{cnonce}\", response=\"{resp}\"";
            }

            string responseSimple = Md5($"{ha1}:{nonce}:{ha2}");
            return $"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", " +
                   $"uri=\"{url}\", response=\"{responseSimple}\"";
        }
        return null;
    }

    // ----------------------------------------------------------------- HTTP

    /// <summary>Prueft ein HTTP-Basic/Digest-geschuetztes Web-Login (Router/Kamera).
    /// Form-Logins (Status 200) werden bewusst NICHT auditiert -> NotChecked.</summary>
    public async Task<(AuthFinding Finding, string? Cred)> AuditHttpAsync(
        string baseUrl, int timeoutMs, CancellationToken ct)
    {
        try
        {
            int noAuth = await HttpStatusAsync(baseUrl, null, null, timeoutMs, ct);
            if (noAuth != 401) return (AuthFinding.NotChecked, null);   // kein Basic/Digest-Login

            foreach (var (user, pass) in CommonDefaults)
            {
                ct.ThrowIfCancellationRequested();
                int s = await HttpStatusAsync(baseUrl, user, pass, timeoutMs, ct);
                if (s is 200 or 301 or 302)
                {
                    string cred = $"{user}/{(pass.Length == 0 ? "(leer)" : pass)}";
                    log.LogWarning("Web-Werks-Login wirksam auf {Url} ({Cred})", baseUrl, cred);
                    return (AuthFinding.DefaultCredentials, cred);
                }
            }
            return (AuthFinding.Secured, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "HTTP-Audit {Url} fehlgeschlagen", baseUrl);
            return (AuthFinding.NotChecked, null);
        }
    }

    private static async Task<int> HttpStatusAsync(
        string url, string? user, string? pass, int timeoutMs, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            // Kamera-/Router-Webinterfaces nutzen oft self-signed Zertifikate.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = false
        };
        if (user is not null) handler.Credentials = new NetworkCredential(user, pass);

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetScanner");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (int)resp.StatusCode;
        }
        catch { return -1; }
    }

    // ----------------------------------------------------------------- Helpers

    private static string RtspUrl(IPAddress ip, int port, string path) =>
        $"rtsp://{ip}:{port}{(path.StartsWith('/') ? "" : "/")}{path}";

    private static int ParseStatus(string resp)
    {
        // "RTSP/1.0 200 OK" -> 200
        var first = resp.AsSpan();
        int nl = first.IndexOf('\n');
        if (nl > 0) first = first[..nl];
        int sp = first.IndexOf(' ');
        if (sp < 0) return -1;
        var rest = first[(sp + 1)..].TrimStart();
        int sp2 = rest.IndexOf(' ');
        var code = sp2 > 0 ? rest[..sp2] : rest;
        return int.TryParse(code, out int c) ? c : -1;
    }

    private static string? ExtractHeader(string resp, string name)
    {
        foreach (var line in resp.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase))
                return trimmed[(name.Length + 1)..].Trim();
        }
        return null;
    }

    private static string? Directive(string header, string key)
    {
        // key="value" oder key=value
        int i = header.IndexOf($"{key}=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int start = i + key.Length + 1;
        if (start >= header.Length) return null;
        if (header[start] == '"')
        {
            int end = header.IndexOf('"', start + 1);
            return end < 0 ? null : header[(start + 1)..end];
        }
        int comma = header.IndexOf(',', start);
        return (comma < 0 ? header[start..] : header[start..comma]).Trim();
    }

    private static string Md5(string s) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
}
