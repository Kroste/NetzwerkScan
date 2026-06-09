using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NetScanner.Models;

namespace NetScanner.Services;

public interface INetworkScanner
{
    /// <summary>Pingt alle Hosts eines CIDR-Bereichs und liefert erreichbare Hosts streamend.</summary>
    IAsyncEnumerable<HostResult> SweepAsync(
        string cidr, int timeoutMs, int maxParallel, CancellationToken ct);
}

/// <summary>
/// Host-Discovery per ICMP-Echo (System.Net.NetworkInformation.Ping).
/// Bewusst KEINE Raw-Sockets: Ping nutzt den OS-Stack und braucht keine erhoehten Rechte.
/// Ergebnisse werden gestreamt (IAsyncEnumerable), damit die UI live fuellt.
/// </summary>
public sealed class NetworkScanner(ILogger<NetworkScanner> log) : INetworkScanner
{
    public async IAsyncEnumerable<HostResult> SweepAsync(
        string cidr, int timeoutMs, int maxParallel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var targets = IpRangeHelper.ExpandCidr(cidr);
        log.LogInformation("Ping-Sweep gestartet: {Cidr} ({Count} Hosts, Timeout {Timeout} ms, Parallel {Par})",
            cidr, targets.Count, timeoutMs, maxParallel);

        using var gate = new SemaphoreSlim(maxParallel);
        // Channel entkoppelt die parallelen Pings vom sequentiellen yield.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<HostResult>();

        var producer = Task.Run(async () =>
        {
            var tasks = targets.Select(async ip =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var host = await PingOnceAsync(ip, timeoutMs, ct);
                    if (host is not null)
                    {
                        log.LogDebug("Host erreichbar: {Ip} ({Ms} ms)", ip, host.RoundtripMs);
                        await channel.Writer.WriteAsync(host, ct);
                    }
                }
                catch (OperationCanceledException) { /* erwartet bei Abbruch */ }
                catch (Exception ex) { log.LogWarning(ex, "Ping fehlgeschlagen fuer {Ip}", ip); }
                finally { gate.Release(); }
            });
            try { await Task.WhenAll(tasks); }
            finally { channel.Writer.TryComplete(); }
        }, ct);

        // ARP-Tabelle erst NACH ein paar Pings sinnvoll -> wir reichern beim Lesen an.
        IReadOnlyDictionary<string, string>? arp = null;

        await foreach (var host in channel.Reader.ReadAllAsync(ct))
        {
            arp ??= await IpRangeHelper.ReadArpTableAsync(ct);
            if (arp.TryGetValue(host.Address.ToString(), out var mac))
            {
                host.MacAddress = mac;
                host.Vendor = OuiLookup.Resolve(mac);
            }
            yield return host;
        }

        await producer;
        log.LogInformation("Ping-Sweep beendet: {Cidr}", cidr);
    }

    private static async Task<HostResult?> PingOnceAsync(IPAddress ip, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeoutMs),
            cancellationToken: ct);
        if (reply.Status != IPStatus.Success) return null;

        var host = new HostResult { Address = ip, RoundtripMs = reply.RoundtripTime };
        // Reverse-DNS best effort, mit kurzem Timeout ueber Task.WhenAny.
        try
        {
            var dns = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(dns, Task.Delay(400, ct)) == dns)
                host.Hostname = dns.Result.HostName;
        }
        catch { /* kein PTR-Record -> egal */ }
        return host;
    }
}
