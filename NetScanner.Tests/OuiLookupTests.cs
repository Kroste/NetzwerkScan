using FluentAssertions;
using NetScanner.Services;
using Xunit;

namespace NetScanner.Tests;

public class OuiLookupTests
{
    [Theory]
    [InlineData("fc:ec:da:11:22:33", "Ubiquiti")]
    [InlineData("FC-EC-DA-11-22-33", "Ubiquiti")]
    [InlineData("00:1a:1e:aa:bb:cc", "Aruba")]
    public void Bekannte_OUI_liefert_den_Hersteller(string mac, string vendor)
        => OuiLookup.Resolve(mac).Should().Be(vendor);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ff:ff:ff:00:00:00")]
    public void Unbekannte_oder_leere_MAC_liefert_null(string? mac)
        => OuiLookup.Resolve(mac).Should().BeNull();

    [Theory]
    [InlineData("Hikvision Digital Technology", true)]
    [InlineData("hikvision", true)]
    [InlineData("Zhejiang Dahua", true)]
    [InlineData("AXIS Communications", true)]
    [InlineData("Reolink Innovation", true)]
    [InlineData("Synology", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Kamera_Hersteller_werden_unabhaengig_von_Gross_Kleinschreibung_erkannt(
        string? vendor, bool expected)
        => OuiLookup.IsLikelyCameraVendor(vendor).Should().Be(expected);

    [Theory]
    // Bit 0x02 im ersten Oktett = "locally administered" -> randomisierte WLAN-MAC.
    [InlineData("02:11:22:33:44:55", true)]
    [InlineData("06:11:22:33:44:55", true)]
    [InlineData("DA-11-22-33-44-55", true)]
    [InlineData("00:11:22:33:44:55", false)]
    [InlineData("FC:EC:DA:11:22:33", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Randomisierte_MAC_wird_am_Local_Bit_erkannt(string? mac, bool expected)
        => OuiLookup.IsRandomizedMac(mac).Should().Be(expected);
}
