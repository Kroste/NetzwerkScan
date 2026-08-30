using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>Eine beim Crawl gefundene Seite eines Webinterfaces.</summary>
/// <param name="Url">Absolute URL.</param>
/// <param name="Path">Pfad + Query (für die Anzeige).</param>
/// <param name="Status">HTTP-Statuscode (-1 = nicht erreichbar).</param>
/// <param name="Title">&lt;title&gt; der Seite, falls HTML.</param>
/// <param name="ContentType">Content-Type-Header.</param>
/// <param name="Source">Woher die URL stammt: "Link", "robots.txt", "sitemap.xml", "Start".</param>
public sealed record WebPage(string Url, string Path, int Status, string? Title, string? ContentType, string Source);

/// <summary>
/// Leichter, begrenzter Crawler für das Webinterface eines Geräts im EIGENEN Netz.
///
/// WICHTIG — Abgrenzung: Das ist KEIN Directory-Bruteforce. Es werden keine Pfade
/// aus einer Wortliste geraten (das ist in NetScanner ausdrücklich unerwünscht).
/// Gefunden wird ausschließlich, was der Server selbst preisgibt: verlinkte Seiten
/// (&lt;a href&gt;), <c>robots.txt</c> und <c>sitemap.xml</c>. Same-Origin, in Tiefe
/// und Anzahl begrenzt — wie ein Browser, der den Links folgt.
/// </summary>
public sealed class WebPageScanner(ILogger<WebPageScanner> log)
{
    private const int MaxPages = 40;
    private const int MaxDepth = 2;
    private const int RequestTimeoutMs = 4000;
    private const int MaxBodyBytes = 512 * 1024;   // 512 KB reichen für Titel + Links

    /// <summary>
    /// Crawlt ab <paramref name="baseUrl"/> und liefert die gefundenen Seiten,
    /// nach Pfad sortiert. Streamt die Ergebnisse (IAsyncEnumerable), damit die UI
    /// sie einlaufen sieht statt am Ende auf alles zu warten.
    /// </summary>
    public async IAsyncEnumerable<WebPage> CrawlAsync(
        string baseUrl, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
            yield break;

        using var http = CreateClient();

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(Uri Url, int Depth, string Source)>();

        void Enqueue(Uri url, int depth, string source)
        {
            string key = url.GetLeftPart(UriPartial.Query);
            if (SameOrigin(root, url) && visited.Add(key))
                queue.Enqueue((url, depth, source));
        }

        Enqueue(root, 0, "Start");
        // robots.txt / sitemap.xml sind die beiden Standard-Selbstauskünfte eines Servers.
        if (Uri.TryCreate(root, "/robots.txt", out var robots)) Enqueue(robots, MaxDepth, "robots.txt");
        if (Uri.TryCreate(root, "/sitemap.xml", out var sitemap)) Enqueue(sitemap, MaxDepth, "sitemap.xml");

        int emitted = 0;
        while (queue.Count > 0 && emitted < MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            var (url, depth, source) = queue.Dequeue();

            var (page, body, contentType) = await FetchAsync(http, url, source, ct);
            emitted++;
            yield return page;

            if (body is null) continue;

            // robots.txt / sitemap.xml auswerten -> weitere URLs.
            if (source == "robots.txt")
            {
                foreach (var path in ParseRobots(body))
                    if (Uri.TryCreate(root, path, out var u)) Enqueue(u, MaxDepth, "robots.txt");
                foreach (var loc in ParseRobotsSitemaps(body))
                    if (Uri.TryCreate(loc, UriKind.Absolute, out var u)) Enqueue(u, MaxDepth, "sitemap.xml");
                continue;
            }
            if (source == "sitemap.xml" || (contentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                foreach (var loc in ParseSitemap(body))
                    if (Uri.TryCreate(loc, UriKind.Absolute, out var u)) Enqueue(u, MaxDepth, "sitemap.xml");
                continue;
            }

            // HTML-Links folgen (nur innerhalb der Tiefe).
            if (depth < MaxDepth && (contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false))
                foreach (var href in ExtractLinks(body))
                    if (Uri.TryCreate(url, href, out var u)) Enqueue(u, depth + 1, "Link");
        }

        log.LogInformation("Web-Crawl {Base}: {Count} Seite(n) gefunden", root.GetLeftPart(UriPartial.Authority), emitted);
    }

    private async Task<(WebPage Page, string? Body, string? ContentType)> FetchAsync(
        HttpClient http, Uri url, string source, CancellationToken ct)
    {
        string path = string.IsNullOrEmpty(url.PathAndQuery) ? "/" : url.PathAndQuery;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeoutMs);
            // ResponseHeadersRead: erst die Header, DANN entscheiden, ob der Body
            // überhaupt gelesen wird. Mit ResponseContentRead wäre eine verlinkte
            // Firmware-.bin schon komplett im RAM, bevor der Content-Type geprüft ist.
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            string? contentType = resp.Content.Headers.ContentType?.MediaType;
            string? body = null;
            // Body nur bei text/* bzw. xml lesen (kein Download von Binärdateien/Firmware).
            if (contentType is not null && (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                                            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)))
            {
                body = await ReadCappedAsync(resp, cts.Token);
            }

            string? title = body is not null ? ExtractTitle(body) : null;
            return (new WebPage(url.ToString(), path, (int)resp.StatusCode, title, contentType, source), body, contentType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Web-Crawl: {Url} nicht erreichbar", url);
            return (new WebPage(url.ToString(), path, -1, null, null, source), null, null);
        }
    }

    /// <summary>
    /// Liest den Body als Text, aber höchstens <see cref="MaxBodyBytes"/> — auch ein
    /// als text/plain deklariertes Riesen-Log soll den RAM nicht sprengen. Für
    /// Titel-/Link-Extraktion reichen die ersten Kilobytes ohnehin.
    /// </summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes];
        int total = 0;
        int read;
        while (total < buffer.Length
               && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // Ziel ist ein INTERNER Host -> Firmen-Proxy umgehen (Skill-Regel), sonst
            // läuft der Request an den Upstream und ins Timeout.
            UseProxy = false,
            // Kamera-/Router-Webinterfaces nutzen oft self-signed Zertifikate.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs + 1000) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetScanner");
        return http;
    }

    // ----------------------------------------------------------------- Parser (testbar)

    private static readonly Regex HrefRegex = new(
        """href\s*=\s*["']([^"'#]+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex TitleRegex = new(
        @"<title[^>]*>(.*?)</title>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    private static readonly Regex LocRegex = new(
        @"<loc>\s*(.*?)\s*</loc>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    /// <summary>Extrahiert die href-Ziele aus HTML — ohne mailto:/javascript:/tel:.</summary>
    internal static IEnumerable<string> ExtractLinks(string html)
    {
        foreach (Match m in HrefRegex.Matches(html))
        {
            string href = m.Groups[1].Value.Trim();
            if (href.Length == 0) continue;
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            yield return href;
        }
    }

    /// <summary>Liest den &lt;title&gt; aus HTML (getrimmt, entpackte HTML-Entities, eine Zeile).</summary>
    internal static string? ExtractTitle(string html)
    {
        var m = TitleRegex.Match(html);
        if (!m.Success) return null;
        string title = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        title = Regex.Replace(title, @"\s+", " ");
        return title.Length == 0 ? null : title;
    }

    /// <summary>Pfade aus Allow:/Disallow:-Zeilen einer robots.txt (die der Server selbst nennt).</summary>
    internal static IEnumerable<string> ParseRobots(string robots)
    {
        foreach (var raw in robots.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var key in new[] { "Allow:", "Disallow:" })
            {
                if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    string path = line[key.Length..].Trim();
                    if (path.Length > 0 && path != "/" && !path.Contains('*'))
                        yield return path;
                }
            }
        }
    }

    /// <summary>Sitemap-URLs aus den Sitemap:-Zeilen einer robots.txt.</summary>
    internal static IEnumerable<string> ParseRobotsSitemaps(string robots)
    {
        foreach (var raw in robots.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase))
            {
                string loc = line["Sitemap:".Length..].Trim();
                if (loc.Length > 0) yield return loc;
            }
        }
    }

    /// <summary>URLs aus den &lt;loc&gt;-Einträgen einer sitemap.xml.</summary>
    internal static IEnumerable<string> ParseSitemap(string xml)
    {
        foreach (Match m in LocRegex.Matches(xml))
        {
            string loc = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (loc.Length > 0) yield return loc;
        }
    }

    /// <summary>Gleicher Origin = gleiches Schema, Host und Port.</summary>
    internal static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
