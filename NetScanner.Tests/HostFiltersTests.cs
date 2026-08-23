using System.Net;
using FluentAssertions;
using NetScanner.Models;
using NetScanner.ViewModels;
using Xunit;

namespace NetScanner.Tests;

public class HostFiltersTests
{
    private static HostResult Host(string ip = "192.168.10.5", string? vendor = null,
        string? bestName = null, params int[] ports)
    {
        var h = new HostResult { Address = IPAddress.Parse(ip), Vendor = vendor, Hostname = bestName };
        foreach (var p in ports) h.OpenPorts.Add(new PortResult(p, IsOpen: true));
        return h;
    }

    private static HostResult Camera(string ip = "192.168.10.9")
    {
        var h = Host(ip);
        h.Camera = new CameraInfo { Address = h.Address };
        return h;
    }

    [Fact]
    public void All_laesst_jeden_Host_durch()
        => HostFilters.Matches(Host(), HostFilter.All, null).Should().BeTrue();

    [Fact]
    public void Cameras_zeigt_nur_Kameras()
    {
        HostFilters.Matches(Camera(), HostFilter.Cameras, null).Should().BeTrue();
        HostFilters.Matches(Host(ports: 80), HostFilter.Cameras, null).Should().BeFalse();
    }

    [Fact]
    public void Web_zeigt_nur_Hosts_mit_Webinterface()
    {
        HostFilters.Matches(Host(ports: 443), HostFilter.Web, null).Should().BeTrue();
        HostFilters.Matches(Host(ports: 8080), HostFilter.Web, null).Should().BeTrue();
        HostFilters.Matches(Host(ports: 22), HostFilter.Web, null).Should().BeFalse();
    }

    [Fact]
    public void Vulnerable_zeigt_nur_Befunde()
    {
        var vuln = Host(ports: 80);
        vuln.WebAudit = AuthFinding.DefaultCredentials;
        HostFilters.Matches(vuln, HostFilter.Vulnerable, null).Should().BeTrue();
        HostFilters.Matches(Host(ports: 80), HostFilter.Vulnerable, null).Should().BeFalse();
    }

    [Theory]
    [InlineData("192.168.10.5", true)]
    [InlineData("10.5", true)]              // Teilstring der IP
    [InlineData("HIKVISION", true)]         // Hersteller, case-insensitiv
    [InlineData("wohnzimmer", true)]        // Name
    [InlineData("192.168.99", false)]       // andere IP
    public void Freitext_matcht_IP_Name_und_Hersteller(string query, bool expected)
    {
        var h = Host("192.168.10.5", vendor: "Hikvision", bestName: "Wohnzimmer-Cam", ports: 80);
        HostFilters.Matches(h, HostFilter.All, query).Should().Be(expected);
    }

    [Fact]
    public void Kategorie_und_Freitext_sind_UND_verknuepft()
    {
        var cam = Camera("192.168.10.9");
        cam.Vendor = "Dahua";

        // Kamera + passender Text -> ja
        HostFilters.Matches(cam, HostFilter.Cameras, "dahua").Should().BeTrue();
        // Kamera, aber Text passt nicht -> nein
        HostFilters.Matches(cam, HostFilter.Cameras, "axis").Should().BeFalse();
        // Text passt, aber Kategorie (Web) nicht (Kamera ohne Web-Port) -> nein
        HostFilters.Matches(cam, HostFilter.Web, "dahua").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Leerer_Freitext_filtert_nicht(string? query)
        => HostFilters.Matches(Host(ports: 80), HostFilter.Web, query).Should().BeTrue();
}
