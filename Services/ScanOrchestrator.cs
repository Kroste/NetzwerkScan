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
    ILogger<ScanOrchestrator> log) : IScanOrchestrator
{
    private static readonly int[] RtspPorts = [554, 8554];

    public async IAsyncEnumerable<HostResult> RunAsync(
        ScanOptions opt, [EnumeratorCancellation] CancellationToken ct)
    {
        log.LogInformation("=== Scan-Lauf START === {Opt}", opt);

        // ONVIF parallel anstossen; Ergebnisse stehen i. d. R. vor Sweep-Ende bereit.
        var onvifTask = onvif.DiscoverAsync(opt.OnvifListenMs, ct);

        var seen = new HashSet<string>();
        await foreach (var host in sweeper.SweepAsync(opt.Cidr, opt.PingTimeoutMs, opt.PingParallel, ct))
        {
            ct.ThrowIfCancellationRequested();
            var onvifMap = onvifTask.IsCompletedSuccessfully ? onvifTask.Result : null;
            await EnrichAsync(host, opt, onvifMap, ct);
            seen.Add(host.Address.ToString());
            yield return host;
        }

        // ONVIF-only: Geraete, die nicht auf Ping geantwortet haben (manche Kameras blocken ICMP).
        var cams = await onvifTask;
        foreach (var cam in cams)
        {
            if (seen.Contains(cam.Address.ToString())) continue;
            var host = new HostResult { Address = cam.Address, Camera = cam, Vendor = cam.Vendor };
            await EnrichAsync(host, opt, cams, ct);
            log.LogInformation("ONVIF-only Host ergaenzt: {Ip}", cam.Address);
            yield return host;
        }

        log.LogInformation("=== Scan-Lauf ENDE ===");
    }

    private async Task EnrichAsync(
        HostResult host, ScanOptions opt, IReadOnlyList<CameraInfo>? onvifMatches, CancellationToken ct)
    {
        // Portscan
        var open = await portScanner.ScanAsync(host.Address, opt.Ports, opt.PortTimeoutMs, opt.PortParallel, ct);
        host.OpenPorts.AddRange(open);

        // ONVIF-Treffer fuer genau diese IP?
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

        // RTSP-Port bestimmen (offener Port bevorzugt, sonst Default 554).
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
