using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetScanner.Services;

/// <summary>
/// Aktives ARP-Probing. Findet Geraete auf Layer 2 — auch solche, die ICMP-Echo
/// ignorieren (Handys im Doze/WLAN-Power-Save, viele IoT-Geraete).
///
/// Windows: SendARP() aus iphlpapi.dll. Laeuft OHNE Admin-Rechte, schickt einen
/// echten ARP-Request und liefert die MAC zurueck.
/// Linux/macOS: kein SendARP-Aequivalent ohne Raw-Sockets — dort wird stattdessen
/// nach dem Ping-Sweep die Neighbor-Tabelle ausgewertet (siehe NetworkScanner),
/// weil der Ping bereits einen ARP-Request ausloest.
/// </summary>
public static class ArpResolver
{
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [SupportedOSPlatform("windows")]
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLen);

    private const int NoError = 0;

    /// <summary>
    /// Schickt einen ARP-Request an <paramref name="ip"/> und liefert die MAC,
    /// oder null, wenn kein Geraet antwortet. Nur Windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<string?> ResolveAsync(IPAddress ip, CancellationToken ct) =>
        // SendARP blockiert bis zum ARP-Timeout -> in den Threadpool auslagern.
        Task.Run(() =>
        {
            // IPv4-Bytes ergeben auf little-endian-Maschinen direkt den network-order-DWORD,
            // den SendARP erwartet.
            var bytes = ip.GetAddressBytes();
            uint dest = BitConverter.ToUInt32(bytes, 0);

            var mac = new byte[6];
            uint len = (uint)mac.Length;
            int rc = SendARP(dest, 0, mac, ref len);
            if (rc != NoError || len == 0) return (string?)null;

            return string.Join(':', mac.Take((int)len).Select(b => b.ToString("X2")));
        }, ct);
}
