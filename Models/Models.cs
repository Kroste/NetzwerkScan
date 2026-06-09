using System.Net;

namespace NetScanner.Models;

/// <summary>Status eines einzelnen Ports nach dem Scan.</summary>
public sealed record PortResult(int Port, bool IsOpen, string? Banner = null)
{
    /// <summary>Bekannter Dienstname (best effort), z. B. "RTSP" fuer 554.</summary>
    public string Service => Port switch
    {
        21 => "FTP", 22 => "SSH", 23 => "Telnet", 53 => "DNS",
        80 => "HTTP", 443 => "HTTPS", 554 => "RTSP", 1935 => "RTMP",
        3389 => "RDP", 8000 => "HTTP-alt", 8080 => "HTTP-proxy",
        8554 => "RTSP-alt", 37777 => "Dahua", 34567 => "DVR", _ => "?"
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

    public List<PortResult> OpenPorts { get; } = [];
    public CameraInfo? Camera { get; set; }

    public bool IsCamera => Camera is not null;
    public string OpenPortsDisplay => OpenPorts.Count == 0
        ? "—"
        : string.Join(", ", OpenPorts.Select(p => $"{p.Port}/{p.Service}"));
}
