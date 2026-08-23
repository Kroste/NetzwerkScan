using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>
/// NetBIOS Name Service (NBNS, UDP 137): "Node Status Request" (NBSTAT) an einen Host.
/// Windows-Geraete und Samba antworten mit ihrer Namenstabelle — daraus lesen wir den
/// Workstation-Namen (Suffix 0x00, kein Gruppenbit) und die Arbeitsgruppe/Domaene (Gruppenbit).
/// Reines Unicast, keine erhoehten Rechte noetig.
/// </summary>
public sealed class NetBiosProbe(ILogger<NetBiosProbe> log)
{
    public async Task<(string? name, string? group)> QueryAsync(IPAddress ip, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await udp.SendAsync(BuildNbstatQuery(), new IPEndPoint(ip, 137), cts.Token);
            var result = await udp.ReceiveAsync(cts.Token);
            return ParseNbstatResponse(result.Buffer);
        }
        catch (OperationCanceledException) { /* kein NetBIOS / Timeout */ }
        catch (Exception ex) { log.LogDebug(ex, "NetBIOS-Abfrage {Ip} fehlgeschlagen", ip); }
        return (null, null);
    }

    /// <summary>NBSTAT-Query mit dem Wildcard-Namen "*" (Standard-Node-Status-Request).</summary>
    private static byte[] BuildNbstatQuery()
    {
        var p = new List<byte>
        {
            0x00, 0x00,             // Transaction ID
            0x00, 0x00,             // Flags: Query
            0x00, 0x01,             // QDCOUNT = 1
            0x00, 0x00,             // ANCOUNT
            0x00, 0x00,             // NSCOUNT
            0x00, 0x00,             // ARCOUNT
            0x20                    // Laenge des kodierten Namens = 32
        };
        // Wildcard "*" + 15 Nullbytes, jedes Halbbyte als ASCII 'A'+nibble kodiert ("first-level encoding").
        var name = new byte[16];
        name[0] = (byte)'*';
        foreach (var b in name)
        {
            p.Add((byte)('A' + (b >> 4)));
            p.Add((byte)('A' + (b & 0x0F)));
        }
        p.Add(0x00);                // Null-Terminator des Namens
        p.AddRange(new byte[] { 0x00, 0x21 });   // QTYPE = NBSTAT (0x21)
        p.AddRange(new byte[] { 0x00, 0x01 });   // QCLASS = IN
        return [.. p];
    }

    private static (string?, string?) ParseNbstatResponse(byte[] buf)
    {
        // Header (12) ueberspringen, dann RR-Name, TYPE/CLASS/TTL/RDLENGTH, dann die Namensliste.
        int pos = 12;

        // RR-Name ueberspringen (Labels bis 0x00 oder Compression-Pointer 0xC0).
        while (pos < buf.Length)
        {
            byte len = buf[pos];
            if (len == 0x00) { pos++; break; }
            if ((len & 0xC0) == 0xC0) { pos += 2; break; }   // Pointer
            pos += 1 + len;
        }

        pos += 2 + 2 + 4 + 2;   // TYPE + CLASS + TTL + RDLENGTH
        if (pos >= buf.Length) return (null, null);

        int numNames = buf[pos++];
        string? workstation = null, group = null;

        for (int i = 0; i < numNames && pos + 18 <= buf.Length; i++, pos += 18)
        {
            string name = Encoding.ASCII.GetString(buf, pos, 15).TrimEnd();
            byte suffix = buf[pos + 15];
            ushort flags = (ushort)((buf[pos + 16] << 8) | buf[pos + 17]);
            bool isGroup = (flags & 0x8000) != 0;

            if (isGroup) group ??= name;                                  // Arbeitsgruppe/Domaene
            else if (suffix == 0x00) workstation ??= name;                // Rechnername
        }
        return (workstation, group);
    }
}
