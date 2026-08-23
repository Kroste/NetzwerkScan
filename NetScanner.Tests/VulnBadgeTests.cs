using System.Net;
using FluentAssertions;
using NetScanner.Models;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// Die Badge-/Vulnerable-Logik entscheidet, ob und wie ein Fund dem Nutzer
/// angezeigt wird. Nach der Erweiterung auf Telnet und FTP muss jede der vier
/// Angriffsflächen einzeln durchschlagen.
/// </summary>
public class VulnBadgeTests
{
    private static HostResult Host() => new() { Address = IPAddress.Parse("192.168.10.5") };

    [Fact]
    public void Ohne_Befund_ist_der_Host_nicht_verwundbar()
    {
        var h = Host();
        h.IsVulnerable.Should().BeFalse();
        h.HasVulnBadge.Should().BeFalse();
        h.VulnBadge.Should().BeNull();
    }

    [Fact]
    public void Offener_Telnet_Zugang_macht_den_Host_verwundbar()
    {
        var h = Host();
        h.TelnetAudit = AuthFinding.Open;

        h.TelnetVulnerable.Should().BeTrue();
        h.IsVulnerable.Should().BeTrue();
        h.VulnBadge.Should().Contain("Telnet");
    }

    [Fact]
    public void Telnet_Werks_Login_zeigt_die_Zugangsdaten_im_Badge()
    {
        var h = Host();
        h.TelnetAudit = AuthFinding.DefaultCredentials;
        h.TelnetAuditCred = "root/root";

        h.VulnBadge.Should().Contain("root/root");
    }

    [Fact]
    public void Anonymer_FTP_Zugriff_schlaegt_als_Schwachstelle_durch()
    {
        var h = Host();
        h.FtpAudit = AuthFinding.Open;

        h.FtpVulnerable.Should().BeTrue();
        h.IsVulnerable.Should().BeTrue();
        h.VulnBadge.Should().Contain("FTP");
    }

    [Fact]
    public void Web_Werks_Login_bleibt_erhalten()
    {
        var h = Host();
        h.WebAudit = AuthFinding.DefaultCredentials;
        h.WebAuditCred = "admin/admin";

        h.WebVulnerable.Should().BeTrue();
        h.VulnBadge.Should().Contain("admin/admin");
    }

    [Fact]
    public void Ein_gesicherter_Befund_ist_keine_Schwachstelle()
    {
        var h = Host();
        h.TelnetAudit = AuthFinding.Secured;
        h.FtpAudit = AuthFinding.Secured;
        h.WebAudit = AuthFinding.Secured;

        h.IsVulnerable.Should().BeFalse();
        h.HasVulnBadge.Should().BeFalse();
    }

    [Fact]
    public void Mehrere_Funde_werden_alle_im_Badge_gelistet()
    {
        var h = Host();
        h.TelnetAudit = AuthFinding.Open;
        h.FtpAudit = AuthFinding.Open;

        h.VulnBadge.Should().Contain("Telnet").And.Contain("FTP");
    }
}
