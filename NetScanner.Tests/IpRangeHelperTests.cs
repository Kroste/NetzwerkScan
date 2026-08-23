using System.Net;
using FluentAssertions;
using NetScanner.Services;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// CIDR-Expansion ist die Grundlage jedes Scans: ein Fehler hier scannt entweder
/// die falschen Adressen oder verfehlt Hosts still.
/// </summary>
public class IpRangeHelperTests
{
    [Fact]
    public void Ein_24er_Netz_liefert_254_Hosts_ohne_Netz_und_Broadcast()
    {
        var hosts = IpRangeHelper.ExpandCidr("192.168.10.0/24");

        hosts.Should().HaveCount(254);
        hosts[0].Should().Be(IPAddress.Parse("192.168.10.1"));
        hosts[^1].Should().Be(IPAddress.Parse("192.168.10.254"));
        hosts.Should().NotContain(IPAddress.Parse("192.168.10.0"));
        hosts.Should().NotContain(IPAddress.Parse("192.168.10.255"));
    }

    [Fact]
    public void Eine_Adresse_mitten_im_Netz_wird_auf_die_Netzadresse_normalisiert()
    {
        // "192.168.10.77/24" meint dasselbe Netz wie "192.168.10.0/24" — der Nutzer
        // tippt oft seine eigene IP ein.
        IpRangeHelper.ExpandCidr("192.168.10.77/24")
            .Should().BeEquivalentTo(IpRangeHelper.ExpandCidr("192.168.10.0/24"));
    }

    [Fact]
    public void Ein_30er_Netz_liefert_genau_die_zwei_nutzbaren_Adressen()
        => IpRangeHelper.ExpandCidr("10.0.0.0/30").Should().Equal(
            IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2"));

    [Theory]
    [InlineData("10.0.0.5/32", 1)]
    [InlineData("10.0.0.4/31", 2)]
    public void Kleine_Praefixe_liefern_alle_Adressen_statt_keiner(string cidr, int expected)
    {
        // /31 und /32 haben rechnerisch keine "normalen" Hosts. Ohne den Sonderfall
        // waere die Liste leer und ein Einzelhost-Scan wuerde stillschweigend nichts tun.
        IpRangeHelper.ExpandCidr(cidr).Should().HaveCount(expected);
    }

    [Theory]
    [InlineData("192.168.0.0/33")]
    [InlineData("192.168.0.0/-1")]
    [InlineData("192.168.0.0")]
    [InlineData("keine-ip/24")]
    [InlineData("")]
    public void Unbrauchbare_Angaben_werfen_statt_still_leer_zu_liefern(string cidr)
        => FluentActions.Invoking(() => IpRangeHelper.ExpandCidr(cidr))
            .Should().Throw<FormatException>();

    [Theory]
    [InlineData("192.168.10.0/24")]
    [InlineData("  10.0.0.1/8  ")]
    [InlineData("172.16.0.0/32")]
    [InlineData("0.0.0.0/0")]
    public void Gueltige_CIDR_wird_akzeptiert(string cidr)
        => IpRangeHelper.IsValidCidr(cidr).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.10.0")]
    [InlineData("192.168.10.0/")]
    [InlineData("192.168.10.0/33")]
    [InlineData("192.168.10.256/24")]
    [InlineData("fe80::1/64")]      // IPv6 wird nicht unterstuetzt
    public void Unbrauchbare_CIDR_wird_abgelehnt(string? cidr)
        => IpRangeHelper.IsValidCidr(cidr).Should().BeFalse();

    [Theory]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF")]
    [InlineData("aa:bb:cc:dd:ee:ff", "AA:BB:CC:DD:EE:FF")]
    public void MAC_Adressen_werden_einheitlich_normalisiert(string raw, string expected)
        => IpRangeHelper.NormalizeMac(raw).Should().Be(expected);
}
