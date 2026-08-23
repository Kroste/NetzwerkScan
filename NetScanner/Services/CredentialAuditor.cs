using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NetScanner.Models;

namespace NetScanner.Services;

/// <summary>
/// Optionales Sicherheits-Audit: prüft, ob ein RTSP-Stream offen ist bzw. ob ein
/// Gerät (Kamera, Router) mit einem dokumentierten Werks-Login zugänglich ist.
///
/// GEDACHT FÜR DAS EIGENE NETZ. Default-Credential-Checks sind Standard in
/// Sicherheits-Audits — gegen fremde Systeme ohne Erlaubnis können sie aber rechtlich
/// relevant sein (in DE u. a. §202c StGB). Das Feature ist daher bewusst opt-in.
///
/// Die Liste enthaelt nur die gängigsten, öffentlich dokumentierten Werks-Logins
/// (kein Brute-Force). RTSP-Auth (Basic/Digest) ist selbst implementiert; HTTP-Auth
/// (Basic/Digest) übernimmt der HttpClient.
/// </summary>
public sealed class CredentialAuditor(ILogger<CredentialAuditor> log)
{
    /// <summary>Kuratierte, gängige Werks-Logins (User, Passwort).</summary>
    public static readonly IReadOnlyList<(string User, string Pass)> CommonDefaults =
    [
        ("admin", "admin"), ("admin", ""), ("admin", "12345"), ("admin", "123456"),
        ("admin", "1234"), ("admin", "password"), ("admin", "admin123"), ("admin", "9999"),
        ("admin", "888888"), ("admin", "default"), ("root", "root"), ("root", "admin"),
        ("root", "12345"), ("root", "pass"), ("user", "user"), ("supervisor", "supervisor"),
        ("service", "service"),
    ];

    // ----------------------------------------------------------------- RTSP

    /// <summary>Prüft einen RTSP-Endpunkt: offen, per Werks-Login zugänglich oder gesichert.</summary>
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

    /// <summary>Prüft ein HTTP-Basic/Digest-geschuetztes Web-Login (Router/Kamera).
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
                    // NIE das Passwort loggen: es ist ein wirksames Login für ein reales
                    // Gerät im Netz. Nur der Benutzername geht ins Log, das vollständige
                    // Paar bleibt im Rückgabewert und damit ausschließlich in der UI.
                    log.LogWarning("Web-Werks-Login wirksam auf {Url} (Benutzer {User})", baseUrl, user);
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

    // ----------------------------------------------------------------- Telnet

    /// <summary>
    /// Prüft ein Telnet-Login (Port 23) — der klassische IoT-/Mirai-Weak-Spot.
    /// Findet drei Fälle: direkter Shell-Zugriff ohne Login (Open), ein wirkender
    /// Werks-Login (DefaultCredentials) oder gesichert.
    ///
    /// Telnet ist textbasiert und uneinheitlich, deshalb bewusst konservativ: ein
    /// Werks-Login gilt nur als wirksam, wenn die Antwort einen Shell-Indikator zeigt
    /// UND weder eine erneute Login-Aufforderung noch ein Fehlerwort enthält. Lieber
    /// ein echter Fund weniger als ein falscher Alarm.
    /// </summary>
    public async Task<(AuthFinding Finding, string? User, string? Pass)> AuditTelnetAsync(
        IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var probe = new TcpClient(ip.AddressFamily);
            using var pcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pcts.CancelAfter(timeoutMs);
            await probe.ConnectAsync(ip, port, pcts.Token);
            await using var pstream = probe.GetStream();

            string greeting = await ReadTelnetAsync(pstream, timeoutMs, pcts.Token);

            // Kein Login-Prompt, aber direkt eine Shell -> offen ohne Anmeldung.
            if (!LooksLikeLoginPrompt(greeting) && LooksLikeShell(greeting))
                return (AuthFinding.Open, null, null);

            // Ohne erkennbaren Login-Prompt lässt sich nichts Verlässliches sagen.
            if (!LooksLikeLoginPrompt(greeting))
                return (AuthFinding.NotChecked, null, null);

            foreach (var (user, pass) in CommonDefaults)
            {
                ct.ThrowIfCancellationRequested();
                if (await TryTelnetLoginAsync(ip, port, user, pass, timeoutMs, ct))
                {
                    log.LogWarning("Telnet-Werks-Login wirksam auf {Ip}:{Port} (Benutzer {User})", ip, port, user);
                    return (AuthFinding.DefaultCredentials, user, pass);
                }
            }
            return (AuthFinding.Secured, null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Telnet-Audit {Ip}:{Port} fehlgeschlagen", ip, port);
            return (AuthFinding.NotChecked, null, null);
        }
    }

    private static async Task<bool> TryTelnetLoginAsync(
        IPAddress ip, int port, string user, string pass, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(ip.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        await client.ConnectAsync(ip, port, cts.Token);
        await using var stream = client.GetStream();

        string prompt = await ReadTelnetAsync(stream, timeoutMs, cts.Token);
        if (!LooksLikeLoginPrompt(prompt)) return false;

        await stream.WriteAsync(Encoding.ASCII.GetBytes(user + "\r\n"), cts.Token);
        string afterUser = await ReadTelnetAsync(stream, timeoutMs, cts.Token);

        // Manche Geräte fragen erst nach dem Benutzernamen nach dem Passwort.
        if (LooksLikePasswordPrompt(afterUser) || LooksLikePasswordPrompt(prompt))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(pass + "\r\n"), cts.Token);
            string afterPass = await ReadTelnetAsync(stream, timeoutMs, cts.Token);
            return TelnetLoginSucceeded(afterPass);
        }

        // Kein Passwort-Prompt: entweder passwortloser Login (Shell da) oder Ablehnung.
        return TelnetLoginSucceeded(afterUser);
    }

    /// <summary>
    /// Liest eine Telnet-Antwort und entfernt die IAC-Optionsverhandlung (Bytes ab
    /// 0xFF). Wir verhandeln bewusst nichts aus — die meisten Geräte fahren trotzdem
    /// bis zum Login-Prompt fort, und ein stiller Client vermeidet Nebenwirkungen.
    /// </summary>
    private static async Task<string> ReadTelnetAsync(NetworkStream stream, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Min(timeoutMs, 1500));
        var buf = new byte[2048];
        try
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            return n <= 0 ? string.Empty : StripTelnetIac(buf.AsSpan(0, n));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return string.Empty;   // nur der Lese-Timeout, nicht der Abbruch von aussen
        }
    }

    /// <summary>Entfernt IAC-Kommandos (0xFF + 2 Folgebytes, bzw. Subnegotiation bis IAC SE).</summary>
    internal static string StripTelnetIac(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0xFF)  // IAC
            {
                if (i + 1 >= data.Length) break;
                byte cmd = data[i + 1];
                if (cmd == 0xFA)  // SB (Subnegotiation) -> bis IAC SE überspringen
                {
                    i += 2;
                    while (i + 1 < data.Length && !(data[i] == 0xFF && data[i + 1] == 0xF0)) i++;
                    i++;  // auf SE
                }
                else
                {
                    i += 2;  // WILL/WONT/DO/DONT + Option
                }
                continue;
            }
            if (b is >= 0x20 and < 0x7F or (byte)'\r' or (byte)'\n' or (byte)'\t')
                sb.Append((char)b);
        }
        return sb.ToString();
    }

    internal static bool LooksLikeLoginPrompt(string text)
        => ContainsAny(text, "login:", "username:", "user name:", "user:");

    internal static bool LooksLikePasswordPrompt(string text)
        => ContainsAny(text, "password:", "passwort:", "passwd:");

    /// <summary>Positiver Shell-Indikator ODER Prompt-Zeile, die auf # $ &gt; endet.</summary>
    internal static bool LooksLikeShell(string text)
    {
        if (ContainsAny(text, "busybox", "# ", "$ ", "welcome to", "last login"))
            return true;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length > 0 && line[^1] is '#' or '$' or '>')
                return true;
        }
        return false;
    }

    internal static bool LooksLikeAuthFailure(string text)
        => ContainsAny(text, "incorrect", "invalid", "failed", "failure", "denied",
                             "fehlgeschlagen", "falsch", "wrong");

    /// <summary>Login gilt nur als erfolgreich mit Shell-Indikator UND ohne Fehlerwort/Re-Prompt.</summary>
    internal static bool TelnetLoginSucceeded(string afterPass)
        => LooksLikeShell(afterPass)
           && !LooksLikeAuthFailure(afterPass)
           && !LooksLikeLoginPrompt(afterPass)
           && !LooksLikePasswordPrompt(afterPass);

    // ----------------------------------------------------------------- FTP

    /// <summary>
    /// Prüft ein FTP-Login (Port 21): erst anonymer Zugriff (Open), dann die
    /// Werks-Logins (DefaultCredentials). Rein lesend (nur USER/PASS/QUIT), es wird
    /// nichts übertragen oder geändert.
    /// </summary>
    public async Task<(AuthFinding Finding, string? Cred)> AuditFtpAsync(
        IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            // Anonymer Zugriff ist der häufigste FTP-Weak-Spot und wird zuerst geprüft.
            if (await TryFtpLoginAsync(ip, port, "anonymous", "anonymous@netscanner", timeoutMs, ct))
            {
                log.LogWarning("FTP erlaubt anonymen Zugriff auf {Ip}:{Port}", ip, port);
                return (AuthFinding.Open, "anonymous");
            }

            foreach (var (user, pass) in CommonDefaults)
            {
                ct.ThrowIfCancellationRequested();
                if (await TryFtpLoginAsync(ip, port, user, pass, timeoutMs, ct))
                {
                    log.LogWarning("FTP-Werks-Login wirksam auf {Ip}:{Port} (Benutzer {User})", ip, port, user);
                    return (AuthFinding.DefaultCredentials, $"{user}/{(pass.Length == 0 ? "(leer)" : pass)}");
                }
            }
            return (AuthFinding.Secured, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "FTP-Audit {Ip}:{Port} fehlgeschlagen", ip, port);
            return (AuthFinding.NotChecked, null);
        }
    }

    private static async Task<bool> TryFtpLoginAsync(
        IPAddress ip, int port, string user, string pass, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(ip.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        await client.ConnectAsync(ip, port, cts.Token);
        await using var stream = client.GetStream();

        string greeting = await ReadLineAsync(stream, timeoutMs, cts.Token);
        if (FtpCode(greeting) != 220) return false;   // kein FTP-Dienst

        await stream.WriteAsync(Encoding.ASCII.GetBytes($"USER {user}\r\n"), cts.Token);
        string afterUser = await ReadLineAsync(stream, timeoutMs, cts.Token);
        int userCode = FtpCode(afterUser);
        if (userCode == 230) return true;             // ohne Passwort eingeloggt
        if (userCode != 331) return false;            // 331 = Passwort erwartet

        await stream.WriteAsync(Encoding.ASCII.GetBytes($"PASS {pass}\r\n"), cts.Token);
        string afterPass = await ReadLineAsync(stream, timeoutMs, cts.Token);

        bool ok = FtpCode(afterPass) == 230;
        try { await stream.WriteAsync(Encoding.ASCII.GetBytes("QUIT\r\n"), cts.Token); } catch { /* egal */ }
        return ok;
    }

    /// <summary>FTP-Status-Code aus der ersten Antwortzeile ("230 Login successful" -> 230).</summary>
    internal static int FtpCode(string line)
    {
        if (line.Length < 3) return -1;
        return int.TryParse(line.AsSpan(0, 3), out int code) ? code : -1;
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Min(timeoutMs, 1500));
        var buf = new byte[512];
        try
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            return n <= 0 ? string.Empty : Encoding.ASCII.GetString(buf, 0, n).TrimStart();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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
