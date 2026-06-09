using NetScanner.Models;

namespace NetScanner.Services;

/// <summary>
/// Heuristische Geraete-/OS-Erkennung aus passiv/aktiv gesammelten Signalen.
/// KEIN nmap-artiges TCP/IP-Stack-Fingerprinting (das braeuchte Raw-Sockets);
/// stattdessen Kombination aus TTL, offenen Ports, MAC-Hersteller und Bannern.
/// Ergebnis ist eine Einschaetzung, keine Gewissheit.
/// </summary>
public static class DeviceClassifier
{
    public static void Classify(HostResult host)
    {
        host.OsGuess = GuessOs(host);
        host.DeviceType = GuessDeviceType(host);
    }

    /// <summary>OS-Familie primaer aus TTL, verfeinert durch Banner/Ports.</summary>
    private static string? GuessOs(HostResult h)
    {
        // Banner sind am zuverlaessigsten.
        if (h.SshBanner is { } ssh)
        {
            if (ssh.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase)) return "Linux (Ubuntu)";
            if (ssh.Contains("Debian", StringComparison.OrdinalIgnoreCase)) return "Linux (Debian)";
            if (ssh.Contains("Raspbian", StringComparison.OrdinalIgnoreCase)) return "Linux (Raspberry Pi OS)";
            if (ssh.Contains("FreeBSD", StringComparison.OrdinalIgnoreCase)) return "FreeBSD";
            if (ssh.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
            return "Linux/Unix";
        }
        if (h.HttpServer is { } srv)
        {
            if (srv.Contains("IIS", StringComparison.OrdinalIgnoreCase)) return "Windows";
            if (srv.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Windows";
        }

        // Eindeutige Port-Signale.
        var ports = h.OpenPorts.Select(p => p.Port).ToHashSet();
        if (ports.Contains(3389) || ports.Contains(445) || ports.Contains(139)) return "Windows";
        if (ports.Contains(62078)) return "iOS (iPhone/iPad)";
        if (ports.Contains(548)) return "macOS";

        // TTL-Heuristik (LAN: meist 0 Hops, also Original-TTL).
        if (h.Ttl is { } ttl)
        {
            return ttl switch
            {
                <= 64 => "Linux/Unix/Android",
                <= 128 => "Windows",
                _ => "Netzwerkgeraet"
            };
        }
        return null;
    }

    /// <summary>Geraetetyp aus Ports, Hersteller und Bannern.</summary>
    private static string? GuessDeviceType(HostResult h)
    {
        if (h.IsCamera) return "IP-Kamera";

        var ports = h.OpenPorts.Select(p => p.Port).ToHashSet();
        var vendor = h.Vendor ?? "";

        // Drucker
        if (ports.Contains(9100) || ports.Contains(515) || ports.Contains(631)) return "Drucker";
        // NAS
        if (ports.Contains(5000) && ports.Contains(5001)) return "NAS (Synology)";
        if (ports.Contains(32400)) return "Media-Server (Plex)";
        if (vendor.Contains("Synology", StringComparison.OrdinalIgnoreCase)
            || vendor.Contains("QNAP", StringComparison.OrdinalIgnoreCase)) return "NAS";
        // Mobilgeraete
        if (ports.Contains(62078)) return "iPhone/iPad";
        // Router/Gateway: HTTP/HTTPS offen + hoher TTL, oft kein anderer Dienst
        if (h.Ttl is > 200) return "Router/Switch";
        if (vendor.Contains("Ubiquiti", StringComparison.OrdinalIgnoreCase)
            || vendor.Contains("Aruba", StringComparison.OrdinalIgnoreCase)
            || vendor.Contains("Cisco", StringComparison.OrdinalIgnoreCase)) return "Netzwerk-Hardware";
        // Server vs. Workstation
        if (ports.Contains(3389)) return "Windows-PC/Server";
        if (ports.Contains(22) && (ports.Contains(80) || ports.Contains(443))) return "Server (Linux)";
        if (ports.Contains(22)) return "Linux-Host";
        if (vendor.Contains("Raspberry", StringComparison.OrdinalIgnoreCase)) return "Raspberry Pi";
        if (vendor.Contains("Apple", StringComparison.OrdinalIgnoreCase)) return "Apple-Geraet";

        // Reiner Web-Dienst
        if (ports.Contains(80) || ports.Contains(443)) return "Web-/IoT-Geraet";
        return null;
    }
}
