using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>
/// mDNS/Bonjour-Discovery (Multicast 224.0.0.251:5353). Es werden PTR-Anfragen für die
/// gängigsten Service-Typen gesendet; antwortende Geräte liefern A-Records (Name↔IP) und
/// PTR/SRV-Records (Service-Typen). Daraus entstehen pro IP Hostname + Klartext-Dienste.
/// </summary>
public sealed class MdnsDiscovery(ILogger<MdnsDiscovery> log)
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int Port = 5353;

    // Service-Typen, nach denen aktiv gefragt wird (+ ihre Klartextbedeutung).
    private static readonly (string svc, string label)[] Queries =
    [
        ("_services._dns-sd._udp.local", ""),       // Meta-Query: listet vorhandene Typen
        ("_googlecast._tcp.local",  "Chromecast"),
        ("_airplay._tcp.local",     "AirPlay"),
        ("_raop._tcp.local",        "AirPlay-Audio"),
        ("_ipp._tcp.local",         "Drucker"),
        ("_ipps._tcp.local",        "Drucker"),
        ("_printer._tcp.local",     "Drucker"),
        ("_pdl-datastream._tcp.local","Drucker"),
        ("_homekit._tcp.local",     "HomeKit"),
        ("_hap._tcp.local",         "HomeKit"),
        ("_spotify-connect._tcp.local","Spotify"),
        ("_sonos._tcp.local",       "Sonos"),
        ("_smb._tcp.local",         "SMB/Datei"),
        ("_ssh._tcp.local",         "SSH"),
        ("_workstation._tcp.local", "Rechner"),
        ("_http._tcp.local",        "Web"),
        ("_androidtvremote2._tcp.local","Android TV"),
    ];

    private static readonly Dictionary<string, string> Labels =
        Queries.Where(q => q.label.Length > 0)
               .GroupBy(q => q.svc.Split('.')[0])
               .ToDictionary(g => g.Key, g => g.First().label);

    public async Task DiscoverAsync(ConcurrentDictionary<string, MdnsRecord> sink, int listenMs, CancellationToken ct)
    {
        log.LogInformation("mDNS-Discovery startet (Listen {Ms} ms)", listenMs);
        var query = BuildQuery();
        var tasks = NetInterfaces.LocalUnicastV4().Select(local => RunOnInterfaceAsync(local, query, sink, listenMs, ct));
        await Task.WhenAll(tasks);
        log.LogInformation("mDNS-Discovery beendet: {Count} Host(s)", sink.Count);
    }

    private async Task RunOnInterfaceAsync(
        IPAddress local, byte[] query, ConcurrentDictionary<string, MdnsRecord> sink, int listenMs, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // Bevorzugt auf 5353 binden (empfaengt auch Multicast-Antworten + Announcements).
            // Ist der Port belegt (lokaler avahi/Bonjour), auf einen ephemeren Port ausweichen —
            // dank QU-Bit in der Query antworten die meisten Geraete dann unicast an uns.
            bool boundOn5353 = true;
            try { udp.Client.Bind(new IPEndPoint(local, Port)); }
            catch (SocketException)
            {
                boundOn5353 = false;
                udp.Client.Bind(new IPEndPoint(local, 0));
                log.LogDebug("mDNS: Port 5353 belegt auf {Local}, weiche auf Unicast aus", local);
            }
            if (boundOn5353) udp.JoinMulticastGroup(Group, local);
            udp.Ttl = 2;

            await udp.SendAsync(query, new IPEndPoint(Group, Port), ct);

            using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listenCts.CancelAfter(listenMs);
            try
            {
                while (!listenCts.IsCancellationRequested)
                {
                    var r = await udp.ReceiveAsync(listenCts.Token);
                    ParseInto(r.Buffer, r.RemoteEndPoint.Address, sink);
                }
            }
            catch (OperationCanceledException) { /* Listen-Fenster zu Ende */ }
        }
        catch (Exception ex) { log.LogDebug(ex, "mDNS auf {Local} fehlgeschlagen", local); }
    }

    private static byte[] BuildQuery()
    {
        var p = new List<byte> { 0x00, 0x00, 0x00, 0x00 };  // ID, Flags (Standard-Query)
        p.AddRange([0x00, (byte)Queries.Length]);            // QDCOUNT
        p.AddRange([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);    // AN/NS/AR = 0
        foreach (var (svc, _) in Queries)
        {
            foreach (var label in svc.Split('.'))
            {
                p.Add((byte)label.Length);
                p.AddRange(Encoding.ASCII.GetBytes(label));
            }
            p.Add(0x00);
            p.AddRange([0x00, 0x0C]);   // QTYPE = PTR
            p.AddRange([0x80, 0x01]);   // QCLASS = IN + QU-Bit (Unicast-Antwort erbeten)
        }
        return [.. p];
    }

    private void ParseInto(byte[] buf, IPAddress sender, ConcurrentDictionary<string, MdnsRecord> sink)
    {
        try
        {
            if (buf.Length < 12) return;
            int qd = (buf[4] << 8) | buf[5];
            int an = (buf[6] << 8) | buf[7];
            int ns = (buf[8] << 8) | buf[9];
            int ar = (buf[10] << 8) | buf[11];

            int pos = 12;
            for (int i = 0; i < qd; i++)   // Fragen überspringen
            {
                SkipName(buf, ref pos);
                pos += 4;
            }

            string? hostName = null;
            var services = new HashSet<string>();
            var aMap = new Dictionary<string, IPAddress>();   // Name -> IP aus A-Records

            int total = an + ns + ar;
            for (int i = 0; i < total && pos < buf.Length; i++)
            {
                string name = ReadName(buf, ref pos);
                if (pos + 10 > buf.Length) break;
                int type = (buf[pos] << 8) | buf[pos + 1];
                int rdlen = (buf[pos + 8] << 8) | buf[pos + 9];
                pos += 10;
                int rdStart = pos;

                switch (type)
                {
                    case 1 when rdlen == 4:   // A-Record
                        aMap[name] = new IPAddress(buf[rdStart..(rdStart + 4)]);
                        break;
                    case 12:                  // PTR -> RR-Name ist der Service-Typ
                        AddService(name, services);
                        break;
                    case 33:                  // SRV -> Ziel-Hostname
                        int t = rdStart + 6;
                        hostName ??= ReadName(buf, ref t);
                        AddService(name, services);
                        break;
                }
                pos = rdStart + rdlen;
            }

            // Hostname bevorzugt aus dem A-Record, der zur Absender-IP passt.
            string? name2 = aMap.FirstOrDefault(kv => kv.Value.Equals(sender)).Key
                            ?? aMap.Keys.FirstOrDefault() ?? hostName;

            if (name2 is null && services.Count == 0) return;

            var rec = sink.GetOrAdd(sender.ToString(), _ => new MdnsRecord());
            if (name2 is not null) rec.Name = Clean(name2);
            foreach (var s in services) rec.Services.Add(s);
            if (!string.IsNullOrWhiteSpace(rec.Name) || rec.Services.Count > 0)
                log.LogDebug("mDNS {Ip}: {Name} [{Svc}]", sender, rec.Name, string.Join(",", rec.Services));
        }
        catch { /* defekte Pakete ignorieren */ }
    }

    private static void AddService(string rrName, HashSet<string> services)
    {
        // rrName z. B. "_googlecast._tcp.local" -> erstes Label "_googlecast"
        var first = rrName.Split('.').FirstOrDefault(l => l.StartsWith('_'));
        if (first is null) return;
        services.Add(Labels.TryGetValue(first, out var label) ? label : first.TrimStart('_'));
    }

    private static string Clean(string n) => n.Replace(".local", "", StringComparison.OrdinalIgnoreCase).TrimEnd('.');

    // --- DNS-Namensdekodierung mit Compression-Pointern -------------------
    private static string ReadName(byte[] buf, ref int pos)
    {
        var sb = new StringBuilder();
        bool jumped = false;
        int safety = 0;
        while (pos < buf.Length && safety++ < 128)
        {
            byte len = buf[pos];
            if (len == 0x00) { if (!jumped) pos++; break; }
            if ((len & 0xC0) == 0xC0)   // Pointer
            {
                int ptr = ((len & 0x3F) << 8) | buf[pos + 1];
                if (!jumped) pos += 2;
                jumped = true;
                pos = ptr;
                continue;
            }
            pos++;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(buf, pos, len));
            pos += len;
        }
        return sb.ToString();
    }

    private static void SkipName(byte[] buf, ref int pos)
    {
        while (pos < buf.Length)
        {
            byte len = buf[pos];
            if (len == 0x00) { pos++; return; }
            if ((len & 0xC0) == 0xC0) { pos += 2; return; }
            pos += 1 + len;
        }
    }
}
