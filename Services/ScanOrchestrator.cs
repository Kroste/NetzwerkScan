using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NetScanner.Models;

namespace NetScanner.Services;

/// <summary>Alle vom Benutzer steuerbaren Scan-Parameter.</summary>
public sealed record ScanOptions
{
    public required string Cidr { get; init; }
    public int PingTimeoutMs { get; init; } = 600;
    public int PingParallel { get; init; } = 64;
    public IReadOnlyList<int> Ports { get; init; } = PortScanner.CommonPorts;
    public int PortTimeoutMs { get; init; } = 400;
    public int PortParallel { get; init; } = 200;
    public int OnvifListenMs { get; init; } = 3000;
    public bool ProbeRtsp { get; init; } = true;
    public string? RtspUser { get; init; }
    public string? RtspPass { get; init; }
}

public interface IScanOrchestrator
{
    IAsyncEnumerable<HostResult> RunAsync(ScanOptions opt, CancellationToken ct);
}

/// <summary>
/// Fuehrt den kompletten Ablauf zusammen:
/// 1) ONVIF WS-Discovery (parallel, kurzes Listen-Fenster)
/// 2) Ping-Sweep (streamend)
/// 3) je Host Portscan
/// 4) Kamera-Klassifizierung: ONVIF-Treffer ODER Port-Heuristik (554/8554/...) ODER Kamera-OUI
/// 5) bei Kamera: RTSP-OPTIONS-Probe + Stream-URL nach Hersteller-Muster
/// 6) ONVIF-Geraete, die im Ping nicht auftauchten, werden am Ende ergaenzt.
/// </summary>
public sealed class ScanOrchestrator(
    INetworkScanner sweeper,
    IPortScanner portScanner,
    ICameraDiscovery onvif,
    RtspProbe rtsp,
    BannerGrabber banner,
    MdnsDiscovery mdns,
    SsdpDiscovery ssdp,
    NetBiosProbe netbios,
    ILogger<ScanOrchestrator> log) : IScanOrchestrator
{
    private static readonly int[] RtspPorts = [554, 8554];
    private static readonly int[] HttpPorts = [80, 8080, 8000, 8081];

    public async IAsyncEnumerable<HostResult> RunAsync(
        ScanOptions opt, [EnumeratorCancellation] CancellationToken ct)
    {
        log.LogInformation("=== Scan-Lauf START === {Opt}", opt);

        // Alle Multicast-Discoverer parallel anstossen. mDNS/SSDP befuellen ihre Sinks
        // live, sodass spaeter gesweepte Hosts bereits Treffer sehen koennen.
        var mdnsSink = new ConcurrentDictionary<string, MdnsRecord>();
        var ssdpSink = new ConcurrentDictionary<string, SsdpRecord>();
        var onvifTask = onvif.DiscoverAsync(opt.OnvifListenMs, ct);
        var mdnsTask = mdns.DiscoverAsync(mdnsSink, opt.OnvifListenMs, ct);
        var ssdpTask = ssdp.DiscoverAsync(ssdpSink, opt.OnvifListenMs, ct);

        var seen = new HashSet<string>();
        await foreach (var host in sweeper.SweepAsync(opt.Cidr, opt.PingTimeoutMs, opt.PingParallel, ct))
        {
            ct.ThrowIfCancellationRequested();
            var onvifMap = onvifTask.IsCompletedSuccessfully ? onvifTask.Result : null;
            await EnrichAsync(host, opt, onvifMap, mdnsSink, ssdpSink, ct);
            seen.Add(host.Address.ToString());
            yield return host;
        }

        // Alle Discovery-Tasks abwarten -> vollstaendige Sinks fuer die "nur-Discovery"-Hosts.
        var cams = await onvifTask;
        await Task.WhenAll(mdnsTask, ssdpTask);

        // Geraete, die nicht auf Ping geantwortet haben, aber per ONVIF/mDNS/SSDP sichtbar sind
        // (z. B. ICMP-stumme Kameras, Smart-TVs, Drucker).
        var extra = new HashSet<string>();
        foreach (var ip in cams.Select(c => c.Address.ToString())
                                .Concat(mdnsSink.Keys).Concat(ssdpSink.Keys))
            if (!seen.Contains(ip)) extra.Add(ip);

        foreach (var ipStr in extra)
        {
            var ip = IPAddress.Parse(ipStr);
            var cam = cams.FirstOrDefault(c => c.Address.ToString() == ipStr);
            var host = new HostResult { Address = ip, Camera = cam, Vendor = cam?.Vendor };
            await EnrichAsync(host, opt, cams, mdnsSink, ssdpSink, ct);
            log.LogInformation("Discovery-only Host ergaenzt: {Ip}", ip);
            yield return host;
        }

        log.LogInformation("=== Scan-Lauf ENDE ===");
    }

    private async Task EnrichAsync(
        HostResult host, ScanOptions opt, IReadOnlyList<CameraInfo>? onvifMatches,
        ConcurrentDictionary<string, MdnsRecord> mdnsSink,
        ConcurrentDictionary<string, SsdpRecord> ssdpSink,
        CancellationToken ct)
    {
        string ipKey = host.Address.ToString();

        // Portscan, NetBIOS und Reverse-DNS NEBENLAEUFIG starten — ihre Timeouts
        // ueberlappen so, statt sich pro Host zu summieren.
        var portTask = portScanner.ScanAsync(host.Address, opt.Ports, opt.PortTimeoutMs, opt.PortParallel, ct);
        var nbTask = netbios.QueryAsync(host.Address, opt.PortTimeoutMs, ct);
        var dnsTask = string.IsNullOrWhiteSpace(host.Hostname)
            ? ReverseDnsAsync(host.Address, ct)
            : Task.FromResult<string?>(host.Hostname);

        var open = await portTask;
        host.OpenPorts.AddRange(open);

        // Banner grabben (OS-/Geraete-Hinweise) — nur wenn passende Ports offen sind.
        await GrabBannersAsync(host, open, opt, ct);

        // NetBIOS-Ergebnis (Windows/Samba-Hostname + Arbeitsgruppe).
        (host.NetbiosName, host.NetbiosGroup) = await nbTask;

        // Reverse-DNS (best effort).
        host.Hostname = await dnsTask;

        // mDNS-/SSDP-Treffer aus den live befuellten Sinks uebernehmen.
        if (mdnsSink.TryGetValue(ipKey, out var m))
        {
            host.MdnsName = m.Name;
            host.MdnsServices.AddRange(m.Services);
        }
        if (ssdpSink.TryGetValue(ipKey, out var s))
        {
            host.UpnpServer = s.Server;
            host.UpnpDeviceType = s.DeviceType;
        }

        // Kamera-Erkennung (kann ohne Treffer früh aussteigen).
        await ClassifyCameraAsync(host, opt, onvifMatches, open, ct);

        // Geraetetyp + OS einschaetzen (nutzt Ports, TTL, Vendor, Banner, Discovery, IsCamera).
        DeviceClassifier.Classify(host);
        if (host.HasDeviceInfo)
            log.LogInformation("{Ip} erkannt als: {Summary} (TTL {Ttl})",
                host.Address, host.DeviceSummary, host.Ttl?.ToString() ?? "?");
    }

    private static async Task<string?> ReverseDnsAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(500);
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), timeout.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch { return null; }
    }

    private async Task GrabBannersAsync(HostResult host, IReadOnlyList<PortResult> open, ScanOptions opt, CancellationToken ct)
    {
        if (open.Any(p => p.Port == 22))
            host.SshBanner = await banner.GrabSshAsync(host.Address, 22, opt.PortTimeoutMs, ct);

        var httpPort = open.Select(p => p.Port).FirstOrDefault(HttpPorts.Contains, 0);
        if (httpPort != 0)
            host.HttpServer = await banner.GrabHttpServerAsync(host.Address, httpPort, opt.PortTimeoutMs, ct);
    }

    private async Task ClassifyCameraAsync(
        HostResult host, ScanOptions opt, IReadOnlyList<CameraInfo>? onvifMatches,
        IReadOnlyList<PortResult> open, CancellationToken ct)
    {
        var onvifHit = onvifMatches?.FirstOrDefault(c => c.Address.Equals(host.Address));
        bool hasRtspPort = open.Any(p => RtspPorts.Contains(p.Port));
        bool vendorIsCamera = OuiLookup.IsLikelyCameraVendor(host.Vendor);

        if (onvifHit is null && !hasRtspPort && !vendorIsCamera)
            return;   // kein Kamera-Indiz

        var cam = onvifHit ?? host.Camera ?? new CameraInfo
        {
            Address = host.Address,
            Source = CameraSource.PortHeuristic,
            Vendor = host.Vendor
        };
        if (onvifHit is not null && (hasRtspPort || vendorIsCamera))
            cam = cam with { Source = CameraSource.Both };

        int rtspPort = open.Select(p => p.Port).FirstOrDefault(RtspPorts.Contains, 554);

        if (opt.ProbeRtsp)
        {
            var (isRtsp, needsAuth) = await rtsp.ProbeAsync(host.Address, rtspPort, opt.PortTimeoutMs, ct);
            cam.RequiresAuth = needsAuth;
            if (isRtsp)
            {
                var path = RtspProbe.PathsForVendor(cam.Vendor).First();
                cam.RtspUri = RtspProbe.BuildUri(host.Address, rtspPort, path, opt.RtspUser, opt.RtspPass);
            }
        }
        host.Camera = cam;
        log.LogInformation("Kamera klassifiziert: {Ip} Quelle={Src} RTSP={Uri}",
            host.Address, cam.Source, cam.RtspUri ?? "(unbekannt)");
    }
}
