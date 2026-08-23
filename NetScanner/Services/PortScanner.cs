using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetScanner.Models;

namespace NetScanner.Services;

public interface IPortScanner
{
    Task<IReadOnlyList<PortResult>> ScanAsync(
        IPAddress address, IEnumerable<int> ports, int timeoutMs, int maxParallel, CancellationToken ct);
}

/// <summary>
/// TCP-Connect-Scan: pro Port ein ConnectAsync mit Timeout. Kein SYN-Scan
/// (der braeuchte Raw-Sockets/Rechte und ist nicht plattformneutral).
/// </summary>
public sealed class PortScanner(ILogger<PortScanner> log) : IPortScanner
{
    /// <summary>Pragmatische Default-Portliste inkl. typischer Kamera-/DVR-, Drucker-, NAS- und OS-Ports.</summary>
    public static readonly int[] CommonPorts =
    [
        21, 22, 23, 53, 80, 139, 443, 445, 515, 548, 554, 631,
        1935, 3389, 5000, 5001, 8000, 8080, 8081, 8443, 8554, 9000, 9100,
        32400, 34567, 37777, 62078
    ];

    public async Task<IReadOnlyList<PortResult>> ScanAsync(
        IPAddress address, IEnumerable<int> ports, int timeoutMs, int maxParallel, CancellationToken ct)
    {
        var portList = ports.ToArray();
        log.LogDebug("Portscan {Ip}: {Count} Ports, Timeout {Timeout} ms", address, portList.Length, timeoutMs);

        using var gate = new SemaphoreSlim(maxParallel);
        var tasks = portList.Select(async port =>
        {
            await gate.WaitAsync(ct);
            try { return await ProbeAsync(address, port, timeoutMs, ct); }
            finally { gate.Release(); }
        });

        var results = await Task.WhenAll(tasks);
        var open = results.Where(r => r.IsOpen).OrderBy(r => r.Port).ToList();
        if (open.Count > 0)
            log.LogInformation("{Ip}: offene Ports {Ports}", address, string.Join(",", open.Select(o => o.Port)));
        return open;
    }

    private static async Task<PortResult> ProbeAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(ip.AddressFamily);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(ip, port, timeoutCts.Token);
            return new PortResult(port, IsOpen: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PortResult(port, IsOpen: false);   // Timeout -> zu
        }
        catch (SocketException)
        {
            return new PortResult(port, IsOpen: false);   // refused/unreachable -> zu
        }
    }
}
