using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NetScanner.Models;

namespace NetScanner.Services;

public interface INetworkScanner
{
    /// <summary>Findet erreichbare Hosts eines CIDR-Bereichs (ICMP + ARP) und liefert sie streamend.</summary>
    IAsyncEnumerable<HostResult> SweepAsync(
        string cidr, int timeoutMs, int maxParallel, CancellationToken ct);
}

/// <summary>
/// Host-Discovery kombiniert ICMP-Echo und ARP:
///   - Windows: aktives ARP-Probing (SendARP) ist die Primaerquelle und findet auch
///     ICMP-stumme Geraete (Handys im Doze, IoT). Ping liefert zusaetzlich die RTT.
///   - Linux/macOS: Ping-Sweep (loest nebenbei ARP-Requests aus); danach wird die
///     Neighbor-Tabelle gelesen und ergaenzt Geraete, die zwar per ARP, aber nicht
///     per ICMP antworten.
/// Bewusst ohne Raw-Sockets -> keine erhoehten Rechte noetig.
/// </summary>
public sealed class NetworkScanner(ILogger<NetworkScanner> log) : INetworkScanner
{
    public async IAsyncEnumerable<HostResult> SweepAsync(
        string cidr, int timeoutMs, int maxParallel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var targets = IpRangeHelper.ExpandCidr(cidr);
        log.LogInformation("Sweep gestartet: {Cidr} ({Count} Hosts, {Mode}, Timeout {Timeout} ms, Parallel {Par})",
            cidr, targets.Count, OperatingSystem.IsWindows() ? "ICMP+ARP/SendARP" : "ICMP + Neighbor-Tabelle",
            timeoutMs, maxParallel);

        using var gate = new SemaphoreSlim(maxParallel);
        var channel = System.Threading.Channels.Channel.CreateUnbounded<HostResult>();
        var seen = new ConcurrentDictionary<string, byte>();

        var producer = Task.Run(async () =>
        {
            var tasks = targets.Select(async ip =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var host = await ProbeAsync(ip, timeoutMs, ct);
                    if (host is not null && seen.TryAdd(ip.ToString(), 0))
                        await channel.Writer.WriteAsync(host, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.LogWarning(ex, "Probe fehlgeschlagen fuer {Ip}", ip); }
                finally { gate.Release(); }
            });
            try { await Task.WhenAll(tasks); }
            finally { channel.Writer.TryComplete(); }
        }, ct);

        // Verbrauchen + bei Bedarf MAC aus Neighbor-Tabelle nachziehen (fuer Ping-Treffer ohne MAC).
        IReadOnlyDictionary<string, string>? arp = null;
        await foreach (var host in channel.Reader.ReadAllAsync(ct))
        {
            if (host.MacAddress is null)
            {
                arp ??= await IpRangeHelper.ReadArpTableAsync(ct);
                if (arp.TryGetValue(host.Address.ToString(), out var mac))
                {
                    host.MacAddress = mac;
                    host.Vendor = OuiLookup.Resolve(mac);
                }
            }
            yield return host;
        }
        await producer;

        // Nicht-Windows: ICMP-stumme, aber per ARP sichtbare Geraete (z. B. Handys) ergaenzen.
        if (!OperatingSystem.IsWindows())
        {
            var table = await IpRangeHelper.ReadArpTableAsync(ct);
            var inRange = targets.Select(t => t.ToString()).ToHashSet();
            foreach (var (ipStr, mac) in table)
            {
                if (!inRange.Contains(ipStr) || !seen.TryAdd(ipStr, 0)) continue;
                var host = new HostResult
                {
                    Address = IPAddress.Parse(ipStr),
                    MacAddress = mac,
                    Vendor = OuiLookup.Resolve(mac),
                    RoundtripMs = -1            // -1 = nur per ARP gesehen, kein ICMP
                };
                await TryReverseDnsAsync(host, ct);
                log.LogDebug("Host nur per ARP gefunden (ICMP-stumm): {Ip} [{Mac}]", ipStr, mac);
                yield return host;
            }
        }

        log.LogInformation("Sweep beendet: {Cidr} ({Count} Host(s))", cidr, seen.Count);
    }

    /// <summary>Prueft einen einzelnen Host. Liefert HostResult, wenn er per ICMP ODER ARP lebt.</summary>
    private async Task<HostResult?> ProbeAsync(IPAddress ip, int timeoutMs, CancellationToken ct)
    {
        // ICMP-Ping (alle Plattformen) — liefert RTT.
        long rtt = -1;
        bool pingOk = false;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: ct);
            if (reply.Status == IPStatus.Success) { pingOk = true; rtt = reply.RoundtripTime; }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Ping nicht moeglich -> evtl. trotzdem per ARP erreichbar */ }

        // Windows: aktiver ARP-Request faengt ICMP-stumme Geraete + liefert die MAC.
        string? mac = null;
        if (OperatingSystem.IsWindows())
        {
            try { mac = await ArpResolver.ResolveAsync(ip, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogDebug(ex, "SendARP fehlgeschlagen fuer {Ip}", ip); }
        }

        bool alive = pingOk || mac is not null;
        if (!alive) return null;

        var host = new HostResult
        {
            Address = ip,
            RoundtripMs = rtt,
            MacAddress = mac,
            Vendor = OuiLookup.Resolve(mac)
        };
        if (!pingOk) log.LogDebug("Host per ARP gefunden (ICMP-stumm): {Ip} [{Mac}]", ip, mac);
        await TryReverseDnsAsync(host, ct);
        return host;
    }

    private static async Task TryReverseDnsAsync(HostResult host, CancellationToken ct)
    {
        try
        {
            var dns = Dns.GetHostEntryAsync(host.Address);
            if (await Task.WhenAny(dns, Task.Delay(400, ct)) == dns)
                host.Hostname = dns.Result.HostName;
        }
        catch { /* kein PTR-Record */ }
    }
}
