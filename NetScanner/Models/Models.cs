using System.Net;
using NetScanner.Localization;

namespace NetScanner.Models;

/// <summary>Status eines einzelnen Ports nach dem Scan.</summary>
public sealed record PortResult(int Port, bool IsOpen, string? Banner = null)
{
    /// <summary>Bekannter Dienstname (best effort), z. B. "RTSP" für 554.</summary>
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
    /// <summary>Ergebnis des optionalen RTSP-Credential-Audits. Bei Open/DefaultCredentials
    /// enthaelt <see cref="RtspUri"/> die funktionierenden Zugangsdaten für die Vorschau.</summary>
    public AuthFinding RtspAudit { get; set; } = AuthFinding.NotChecked;
}

public enum CameraSource { OnvifDiscovery, PortHeuristic, Both }

/// <summary>Ergebnis einer Credential-Prüfung (Stream oder Web-Login).</summary>
public enum AuthFinding
{
    /// <summary>Nicht geprüft (Audit war aus oder nicht anwendbar).</summary>
    NotChecked,
    /// <summary>Ohne Zugangsdaten erreichbar — offen.</summary>
    Open,
    /// <summary>Zugangsdaten nötig, ein Werks-Login funktioniert.</summary>
    DefaultCredentials,
    /// <summary>Zugangsdaten nötig, kein getesteter Werks-Login passte.</summary>
    Secured
}

/// <summary>Aggregiertes Ergebnis pro erreichbarem Host.</summary>
public sealed class HostResult
{
    public required IPAddress Address { get; init; }
    public string? Hostname { get; set; }
    public string? MacAddress { get; set; }
    public string? Vendor { get; set; }
    public long RoundtripMs { get; set; }

    /// <summary>TTL aus dem ICMP-Reply (Basis für die OS-Heuristik).</summary>
    public int? Ttl { get; set; }
    /// <summary>Geschaetzte OS-Familie (z. B. "Windows", "Linux/Unix", "Netzwerkgerät").</summary>
    public string? OsGuess { get; set; }
    /// <summary>Geschaetzter Gerätetyp (z. B. "Drucker", "Kamera", "NAS", "Router").</summary>
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
    /// <summary>Aus SSDP abgeleiteter UPnP-Gerätetyp (z. B. "Router", "Media-Server").</summary>
    public string? UpnpDeviceType { get; set; }

    public List<PortResult> OpenPorts { get; } = [];
    public CameraInfo? Camera { get; set; }

    /// <summary>Ergebnis des optionalen Web-Login-Audits (Router/Gerät-Webinterface).</summary>
    public AuthFinding WebAudit { get; set; } = AuthFinding.NotChecked;
    /// <summary>Funktionierender Web-Werks-Login als "user/pass" (nur Anzeige), falls gefunden.</summary>
    public string? WebAuditCred { get; set; }

    /// <summary>Ergebnis des optionalen Telnet-Audits (Port 23, IoT-Weak-Spot).</summary>
    public AuthFinding TelnetAudit { get; set; } = AuthFinding.NotChecked;
    /// <summary>Funktionierender Telnet-Werks-Login als "user/pass" (nur Anzeige), falls gefunden.</summary>
    public string? TelnetAuditCred { get; set; }

    /// <summary>Ergebnis des optionalen FTP-Audits (Port 21, anonym/Werks-Login).</summary>
    public AuthFinding FtpAudit { get; set; } = AuthFinding.NotChecked;
    /// <summary>Funktionierender FTP-Werks-Login als "user/pass" (nur Anzeige), falls gefunden.</summary>
    public string? FtpAuditCred { get; set; }

    // --- Verwundbarkeits-Auswertung für die UI ---
    /// <summary>RTSP-Stream ist offen oder per Werks-Login zugänglich.</summary>
    public bool RtspVulnerable =>
        Camera?.RtspAudit is AuthFinding.Open or AuthFinding.DefaultCredentials;
    /// <summary>Web-Login (beliebiges Gerät) ist offen oder per Werks-Login zugänglich.</summary>
    public bool WebVulnerable =>
        WebAudit is AuthFinding.Open or AuthFinding.DefaultCredentials;
    /// <summary>Telnet ist ohne Login offen oder per Werks-Login zugänglich.</summary>
    public bool TelnetVulnerable =>
        TelnetAudit is AuthFinding.Open or AuthFinding.DefaultCredentials;
    /// <summary>FTP erlaubt anonymen Zugriff oder ein Werks-Login.</summary>
    public bool FtpVulnerable =>
        FtpAudit is AuthFinding.Open or AuthFinding.DefaultCredentials;
    public bool IsVulnerable =>
        RtspVulnerable || WebVulnerable || TelnetVulnerable || FtpVulnerable;

    /// <summary>
    /// Kompakte Badge-Zeile für gefundene Schwachstellen. Die Texte kommen aus den
    /// Ressourcen (der Wert selbst wird beim Scan gesetzt, nicht live umgeschaltet —
    /// wie bei <see cref="DeviceSummary"/>).
    /// </summary>
    public string? VulnBadge
    {
        get
        {
            var parts = new List<string>();

            if (Camera?.RtspAudit == AuthFinding.Open) parts.Add(L.T("Vuln_StreamOpen"));
            else if (Camera?.RtspAudit == AuthFinding.DefaultCredentials) parts.Add(L.T("Vuln_StreamDefaultLogin"));

            if (WebAudit == AuthFinding.Open) parts.Add(L.T("Vuln_WebOpen"));
            else if (WebAudit == AuthFinding.DefaultCredentials) parts.Add(L.F("Vuln_WebDefaultLogin", WebAuditCred ?? "?"));

            if (TelnetAudit == AuthFinding.Open) parts.Add(L.T("Vuln_TelnetOpen"));
            else if (TelnetAudit == AuthFinding.DefaultCredentials) parts.Add(L.F("Vuln_TelnetDefaultLogin", TelnetAuditCred ?? "?"));

            if (FtpAudit == AuthFinding.Open) parts.Add(L.T("Vuln_FtpAnonymous"));
            else if (FtpAudit == AuthFinding.DefaultCredentials) parts.Add(L.F("Vuln_FtpDefaultLogin", FtpAuditCred ?? "?"));

            return parts.Count == 0 ? null : string.Join("  ·  ", parts);
        }
    }
    public bool HasVulnBadge => VulnBadge is not null;

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

    /// <summary>Bester verfügbarer Name: DNS &gt; mDNS &gt; NetBIOS.</summary>
    public string? BestName => !string.IsNullOrWhiteSpace(Hostname) ? Hostname
                             : !string.IsNullOrWhiteSpace(MdnsName) ? MdnsName
                             : NetbiosName;
    public bool HasBestName => !string.IsNullOrWhiteSpace(BestName);
    // Rückwärtskompatibel zur bestehenden UI-Bindung:
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

    // --- Aktions-Helfer für Kontextmenue/Detail-Panel ---
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

    /// <summary>Kurze Zusammenfassung "Gerätetyp · OS" für die Anzeige.</summary>
    public string DeviceSummary
    {
        get
        {
            var parts = new List<string>(2);
            // DeviceClassifier liefert Resource-Keys für die übersetzbaren Typen
            // und Klartext für Produktnamen/UPnP-Angaben -- TOrText behandelt beides.
            if (!string.IsNullOrWhiteSpace(DeviceType)) parts.Add(L.TOrText(DeviceType));
            if (!string.IsNullOrWhiteSpace(OsGuess)) parts.Add(L.TOrText(OsGuess));
            return string.Join(" · ", parts);
        }
    }
    public bool HasDeviceInfo => !string.IsNullOrWhiteSpace(DeviceType) || !string.IsNullOrWhiteSpace(OsGuess);

    /// <summary>Banner-Zeile (SSH/HTTP) für die Anzeige, falls vorhanden.</summary>
    public string? BannerDisplay => SshBanner ?? (HttpServer is not null ? $"HTTP: {HttpServer}" : null);
    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerDisplay);
}

/// <summary>Eine aktive Portweiterleitung vom Router ins LAN (UPnP-IGD).
/// Heißt: Dieser externe Port ist aus dem Internet erreichbar und landet bei InternalClient.</summary>
public sealed record PortMapping(
    int ExternalPort, string Protocol, string InternalClient, int InternalPort,
    string? Description, bool Enabled)
{
    /// <summary>Wird beim Abgleich mit den Scan-Ergebnissen gesetzt (Gerätename, falls bekannt).</summary>
    public string? DeviceName { get; set; }
    /// <summary>True, wenn das Ziel eine erkannte Kamera ist (für die Hervorhebung).</summary>
    public bool TargetsCamera { get; set; }

    public string Display => $"{Protocol} :{ExternalPort}  →  {InternalClient}:{InternalPort}";
    public string DescriptionDisplay => string.IsNullOrWhiteSpace(Description) ? "—" : Description!;
}
