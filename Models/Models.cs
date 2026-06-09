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

    public List<PortResult> OpenPorts { get; } = [];
    public CameraInfo? Camera { get; set; }

    public bool IsCamera => Camera is not null;
    public string OpenPortsDisplay => OpenPorts.Count == 0
        ? "—"
        : string.Join(", ", OpenPorts.Select(p => $"{p.Port}/{p.Service}"));

    /// <summary>True = hat auf ICMP geantwortet; False = nur per ARP gesehen (z. B. Handy im Doze).</summary>
    public bool IsIcmpAlive => RoundtripMs >= 0;
    public string LatencyDisplay => RoundtripMs >= 0 ? $"{RoundtripMs} ms" : "nur ARP";
    public bool HasHostname => !string.IsNullOrWhiteSpace(Hostname);
    public bool HasMac => !string.IsNullOrWhiteSpace(MacAddress);

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
