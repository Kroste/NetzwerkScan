using System.Net;
using FluentAssertions;
using NetScanner.Models;
using NetScanner.Services;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// Die Klassifikation liefert für übersetzbare Typen stabile Resource-Keys
/// (Präfix "Device_") und für Produktnamen bzw. vom Gerät gemeldete Angaben
/// Klartext. Die Tests prüfen genau diese Aufteilung mit.
/// </summary>
public class DeviceClassifierTests
{
    private static HostResult Host(int? ttl = null, params int[] ports)
    {
        var h = new HostResult { Address = IPAddress.Parse("192.168.10.42"), Ttl = ttl };
        foreach (var p in ports)
            h.OpenPorts.Add(new PortResult(p, IsOpen: true));
        return h;
    }

    // ----------------------------------------------------------- OS-Heuristik

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3", "Linux (Ubuntu)")]
    [InlineData("SSH-2.0-OpenSSH_9.2p1 Debian-2", "Linux (Debian)")]
    [InlineData("SSH-2.0-OpenSSH_9.2p1 Raspbian-2", "Linux (Raspberry Pi OS)")]
    [InlineData("SSH-2.0-OpenSSH_9.3 FreeBSD-20230316", "FreeBSD")]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", "Windows")]
    [InlineData("SSH-2.0-Irgendwas", "Linux/Unix")]
    public void Das_SSH_Banner_schlaegt_jede_andere_Heuristik(string banner, string expected)
    {
        // Der TTL-Wert zeigt hier bewusst auf Windows: das Banner muss gewinnen.
        var h = Host(ttl: 128);
        h.SshBanner = banner;

        DeviceClassifier.Classify(h);

        h.OsGuess.Should().Be(expected);
    }

    [Theory]
    [InlineData(64, "Linux/Unix/Android")]
    [InlineData(128, "Windows")]
    [InlineData(255, "Device_NetworkDevice")]
    public void Ohne_Banner_entscheidet_die_TTL(int ttl, string expected)
    {
        var h = Host(ttl);
        DeviceClassifier.Classify(h);
        h.OsGuess.Should().Be(expected);
    }

    [Fact]
    public void Ein_offener_RDP_Port_bedeutet_Windows_auch_ohne_TTL()
    {
        var h = Host(ttl: null, 3389);
        DeviceClassifier.Classify(h);
        h.OsGuess.Should().Be("Windows");
    }

    [Fact]
    public void Ohne_jedes_Signal_bleibt_die_OS_Schaetzung_leer()
    {
        var h = Host();
        DeviceClassifier.Classify(h);
        h.OsGuess.Should().BeNull();
    }

    // --------------------------------------------------------- Gerätetyp

    [Theory]
    [InlineData(9100, "Device_Printer")]
    [InlineData(631, "Device_Printer")]
    [InlineData(32400, "Device_MediaServerPlex")]
    [InlineData(22, "Device_LinuxHost")]
    public void Port_Signale_ergeben_den_Gerätetyp(int port, string expected)
    {
        var h = Host(ttl: null, port);
        DeviceClassifier.Classify(h);
        h.DeviceType.Should().Be(expected);
    }

    [Fact]
    public void Eine_hohe_TTL_deutet_auf_Router_oder_Switch()
    {
        var h = Host(ttl: 255);
        DeviceClassifier.Classify(h);
        h.DeviceType.Should().Be("Device_RouterSwitch");
    }

    [Fact]
    public void Ein_UPnP_Gerätetyp_hat_Vorrang_vor_der_Port_Heuristik()
    {
        // Was das Gerät selbst meldet, ist verlaesslicher als jede Heuristik —
        // und bleibt als Klartext stehen, weil es keine Übersetzung dafür gibt.
        var h = Host(ttl: null, 9100);
        h.UpnpDeviceType = "InternetGatewayDevice";

        DeviceClassifier.Classify(h);

        h.DeviceType.Should().Be("InternetGatewayDevice");
    }

    [Fact]
    public void Eine_erkannte_Kamera_schlaegt_alles_andere()
    {
        var h = Host(ttl: 255, 9100);
        h.Camera = new CameraInfo { Address = h.Address };

        DeviceClassifier.Classify(h);

        h.DeviceType.Should().Be("Device_IpCamera");
    }

    [Fact]
    public void Eine_randomisierte_MAC_ohne_offene_Ports_deutet_auf_ein_Mobilgerät()
    {
        var h = Host();
        h.MacAddress = "02:11:22:33:44:55";

        DeviceClassifier.Classify(h);

        h.DeviceType.Should().Be("Device_MobileRandomMac");
    }

    [Theory]
    [InlineData("Synology", "NAS")]
    [InlineData("QNAP Systems", "NAS")]
    [InlineData("Ubiquiti Networks", "Device_NetworkHardware")]
    [InlineData("Raspberry Pi Foundation", "Raspberry Pi")]
    public void Der_Hersteller_praezisiert_den_Typ(string vendor, string expected)
    {
        var h = Host();
        h.Vendor = vendor;

        DeviceClassifier.Classify(h);

        h.DeviceType.Should().Be(expected);
    }

    [Fact]
    public void Ein_mDNS_Dienst_wird_zum_Gerätetyp()
    {
        var h = Host();
        h.MdnsServices.Add("Chromecast");

        DeviceClassifier.Classify(h);

        h.DeviceType.Should().Be("Chromecast/Google TV");
    }
}
