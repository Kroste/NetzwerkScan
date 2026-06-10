using System.Net;

namespace NetScanner.Models;

/// <summary>Status eines einzelnen Ports nach dem Scan.</summary>
public sealed record PortResult(int Port, bool IsOpen, string? Banner = null)
{
    /// <summary>Bekannter Dienstname (best effort), z. B. "RTSP" fuer 554.</summary>
    public string Service => Port switch
    {
        21 => "FTP", 22 => "SSH", 23 => "Telnet", 53 => "DNS",
        80 => "HTTP", 139 => "NetBIOS", 443 => "HTTPS", 445 => "SMB",
        515 => "LPD", 548 => "AFP", 554 => "RTSP", 631 => "IPP",
        1935 => "RTMP", 3389 => "RDP", 5000 => "Synology", 5001 => "Synology-S",
        8000 => "HTTP-alt", 8080 => "HTTP-proxy", 8081 => "HTTP-alt", 8443 => "HTTPS-alt",
        8554 => "RTSP-alt", 9000 => "HTTP-alt", 9100 => "JetDirect",
        32400 => "Plex", 34567 => "DVR", 37777 => "Dahua", 62078 => "iOS-lockdown", _ => "?"
    };
}

/// <summary>Ein im ONVIF-WS-Discovery oder per Heuristik gefundener Kamera-Kandidat.</summary>
public sealed record CameraInfo
{
    public required IPAddress Address { get; init; }
    public CameraSource Source { get; init; }
    /// <summary>ONVIF-Service-URL (XAddrs), nur bei WS-Discovery gesetzt.</summary>
    public string? OnvifServiceUri { get; init; }
    /// <summary>ONVIF-Scopes (enthalten oft Hersteller/Modell/Name).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];
    /// <summary>Aus OUI bzw. Scopes abgeleiteter Hersteller (best effort).</summary>
    public string? Vendor { get; init; }
    /// <summary>Ermittelte oder gemutmasste RTSP-Stream-URL.</summary>
    public string? RtspUri { get; set; }
    /// <summary>True, wenn der Stream Zugangsdaten verlangt (401/Beschreibung).</summary>
    public bool RequiresAuth { get; set; }
}

public enum CameraSource { OnvifDiscovery, PortHeuristic, Both }

/// <summary>Aggregiertes Ergebnis pro erreichbarem Host.</summary>
public sealed class HostResult
{
    public required IPAddress Address { get; init; }
    public string? Hostname { get; set; }
    public string? MacAddress { get; set; }
    public string? Vendor { get; set; }
    public long RoundtripMs { get; set; }

    /// <summary>TTL aus dem ICMP-Reply (Basis fuer die OS-Heuristik).</summary>
    public int? Ttl { get; set; }
    /// <summary>Geschaetzte OS-Familie (z. B. "Windows", "Linux/Unix", "Netzwerkgeraet").</summary>
    public string? OsGuess { get; set; }
    /// <summary>Geschaetzter Geraetetyp (z. B. "Drucker", "Kamera", "NAS", "Router").</summary>
    public string? DeviceType { get; set; }
    /// <summary>HTTP-Server-Header (z. B. "nginx", "Microsoft-IIS/10.0").</summary>
    public string? HttpServer { get; set; }
    /// <summary>SSH-Banner (z. B. "SSH-2.0-OpenSSH_9.6 Ubuntu").</summary>
    public string? SshBanner { get; set; }

    /// <summary>Per mDNS/Bonjour gemeldeter Hostname (z. B. "Wohnzimmer-TV.local").</summary>
    public string? MdnsName { get; set; }
    /// <summary>Per mDNS gefundene Service-Typen in Klartext (z. B. "Chromecast", "Drucker").</summary>
    public List<string> MdnsServices { get; } = [];
    /// <summary>NetBIOS-Workstation-Name (Windows/Samba).</summary>
    public string? NetbiosName { get; set; }
    /// <summary>NetBIOS-Arbeitsgruppe oder Domaene.</summary>
    public string? NetbiosGroup { get; set; }
    /// <summary>UPnP/SSDP-Server-Kennung (enthaelt oft OS + Produkt).</summary>
    public string? UpnpServer { get; set; }
    /// <summary>Aus SSDP abgeleiteter UPnP-Geraetetyp (z. B. "Router", "Media-Server").</summary>
    public string? UpnpDeviceType { get; set; }

    public List<PortResult> OpenPorts { get; } = [];
    public CameraInfo? Camera { get; set; }

    public bool IsCamera => Camera is not null;
    /// <summary>RTSP-URL der Kamera — null-sicher (vermeidet Binding-Fehler bei Nicht-Kameras).</summary>
    public string? RtspUri => Camera?.RtspUri;
    public bool HasRtspUri => !string.IsNullOrWhiteSpace(RtspUri);
    public string OpenPortsDisplay => OpenPorts.Count == 0
        ? "—"
        : string.Join(", ", OpenPorts.Select(p => $"{p.Port}/{p.Service}"));

    /// <summary>True = hat auf ICMP geantwortet; False = nur per ARP gesehen (z. B. Handy im Doze).</summary>
    public bool IsIcmpAlive => RoundtripMs >= 0;
    public string LatencyDisplay => RoundtripMs >= 0 ? $"{RoundtripMs} ms" : "nur ARP";

    /// <summary>Bester verfuegbarer Name: DNS &gt; mDNS &gt; NetBIOS.</summary>
    public string? BestName => !string.IsNullOrWhiteSpace(Hostname) ? Hostname
                             : !string.IsNullOrWhiteSpace(MdnsName) ? MdnsName
                             : NetbiosName;
    public bool HasBestName => !string.IsNullOrWhiteSpace(BestName);
    // Rueckwaertskompatibel zur bestehenden UI-Bindung:
    public bool HasHostname => HasBestName;

    public bool HasMac => !string.IsNullOrWhiteSpace(MacAddress);

    /// <summary>Kompakte Discovery-Zeile: mDNS-Dienste, UPnP-Typ, NetBIOS-Gruppe.</summary>
    public string? DiscoveryDisplay
    {
        get
        {
            var parts = new List<string>();
            if (MdnsServices.Count > 0) parts.Add(string.Join(", ", MdnsServices));
            if (!string.IsNullOrWhiteSpace(UpnpDeviceType)) parts.Add($"UPnP: {UpnpDeviceType}");
            if (!string.IsNullOrWhiteSpace(NetbiosGroup)) parts.Add($"Gruppe: {NetbiosGroup}");
            return parts.Count == 0 ? null : string.Join("  ·  ", parts);
        }
    }
    public bool HasDiscovery => DiscoveryDisplay is not null;

    // --- Aktions-Helfer fuer Kontextmenue/Detail-Panel ---
    /// <summary>Beste Web-URL aus offenen Ports (HTTPS bevorzugt), sonst null.</summary>
    public string? WebUrl
    {
        get
        {
            var ports = OpenPorts.Select(p => p.Port).ToHashSet();
            if (ports.Contains(443)) return $"https://{Address}";
            if (ports.Contains(80)) return $"http://{Address}";
            if (ports.Contains(8443)) return $"https://{Address}:8443";
            foreach (var p in new[] { 8080, 8000, 8081 })
                if (ports.Contains(p)) return $"http://{Address}:{p}";
            return null;
        }
    }
    public bool HasWebUi => WebUrl is not null;
    public bool HasSsh => OpenPorts.Any(p => p.Port == 22);
    public bool HasRdp => OpenPorts.Any(p => p.Port == 3389);
    public bool HasSmb => OpenPorts.Any(p => p.Port is 445 or 139);
    public bool HasMacForWol => HasMac;

    /// <summary>Kurze Zusammenfassung "Geraetetyp · OS" fuer die Anzeige.</summary>
    public string DeviceSummary
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(DeviceType)) parts.Add(DeviceType!);
            if (!string.IsNullOrWhiteSpace(OsGuess)) parts.Add(OsGuess!);
            return string.Join(" · ", parts);
        }
    }
    public bool HasDeviceInfo => !string.IsNullOrWhiteSpace(DeviceType) || !string.IsNullOrWhiteSpace(OsGuess);

    /// <summary>Banner-Zeile (SSH/HTTP) fuer die Anzeige, falls vorhanden.</summary>
    public string? BannerDisplay => SshBanner ?? (HttpServer is not null ? $"HTTP: {HttpServer}" : null);
    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerDisplay);
}
