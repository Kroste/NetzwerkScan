using FluentAssertions;
using NetScanner.Services;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// Der Crawl selbst braucht einen HTTP-Server; getestet werden die reinen Parser,
/// in denen das eigentliche Risiko sitzt (falsche Links, Fremd-Origin, kaputte
/// Titel). Der Netzwerk-Teil wird separat gegen einen lokalen Testserver geprüft.
/// </summary>
public class WebPageScannerTests
{
    // ---------------------------------------------------------------- Links

    [Fact]
    public void Links_werden_aus_dem_HTML_extrahiert()
    {
        string html = """
            <a href="/status">Status</a>
            <a href='config.html'>Config</a>
            <a href="https://example.org/extern">Extern</a>
            """;
        WebPageScanner.ExtractLinks(html)
            .Should().BeEquivalentTo("/status", "config.html", "https://example.org/extern");
    }

    [Theory]
    [InlineData("<a href=\"mailto:a@b.de\">Mail</a>")]
    [InlineData("<a href=\"javascript:void(0)\">JS</a>")]
    [InlineData("<a href=\"tel:12345\">Tel</a>")]
    [InlineData("<a href=\"data:text/plain,x\">Data</a>")]
    [InlineData("<a href=\"\">leer</a>")]
    public void Nicht_navigierbare_Links_werden_ignoriert(string html)
        => WebPageScanner.ExtractLinks(html).Should().BeEmpty();

    // ---------------------------------------------------------------- Titel

    [Theory]
    [InlineData("<title>Router-Login</title>", "Router-Login")]
    [InlineData("<title>  viel   Whitespace  </title>", "viel Whitespace")]
    [InlineData("<title>Caf&eacute;</title>", "Café")]
    [InlineData("<html><head><title>\nMehrzeilig\n</title></head>", "Mehrzeilig")]
    public void Titel_wird_gelesen_und_normalisiert(string html, string expected)
        => WebPageScanner.ExtractTitle(html).Should().Be(expected);

    [Theory]
    [InlineData("<html>ohne Titel</html>")]
    [InlineData("<title></title>")]
    public void Fehlender_oder_leerer_Titel_ist_null(string html)
        => WebPageScanner.ExtractTitle(html).Should().BeNull();

    // ---------------------------------------------------------------- robots.txt

    [Fact]
    public void Robots_Pfade_werden_aus_Allow_und_Disallow_gelesen()
    {
        string robots = """
            User-agent: *
            Disallow: /admin
            Allow: /public
            Disallow: /
            Disallow: /tmp/*
            """;
        // "/" und Wildcards werden ausgelassen (kein Raten).
        WebPageScanner.ParseRobots(robots).Should().BeEquivalentTo("/admin", "/public");
    }

    [Fact]
    public void Sitemap_Zeile_der_robots_wird_erkannt()
    {
        string robots = "Sitemap: http://host/sitemap.xml\nDisallow: /x";
        WebPageScanner.ParseRobotsSitemaps(robots).Should().Equal("http://host/sitemap.xml");
    }

    // ---------------------------------------------------------------- sitemap.xml

    [Fact]
    public void Sitemap_Locs_werden_extrahiert()
    {
        string xml = """
            <?xml version="1.0"?>
            <urlset><url><loc>http://host/a</loc></url><url><loc>http://host/b</loc></url></urlset>
            """;
        WebPageScanner.ParseSitemap(xml).Should().BeEquivalentTo("http://host/a", "http://host/b");
    }

    // ---------------------------------------------------------------- Same-Origin

    [Theory]
    [InlineData("http://host/a", "http://host/b", true)]
    [InlineData("http://host:80/a", "http://host:80/b", true)]
    [InlineData("http://host/a", "https://host/a", false)]   // anderes Schema
    [InlineData("http://host/a", "http://other/a", false)]   // anderer Host
    [InlineData("http://host:80/a", "http://host:8080/a", false)] // anderer Port
    public void SameOrigin_prueft_Schema_Host_und_Port(string a, string b, bool expected)
        => WebPageScanner.SameOrigin(new Uri(a), new Uri(b)).Should().Be(expected);
}
