using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>
/// Wake-on-LAN: sendet ein "Magic Packet" (6× 0xFF + 16× Ziel-MAC) als UDP-Broadcast.
/// Weckt Geräte, deren NIC WoL unterstützt und aktiviert hat. Keine erhöhten Rechte nötig.
/// </summary>
public sealed class WolSender(ILogger<WolSender> log)
{
    public async Task<bool> SendAsync(string? mac, CancellationToken ct)
    {
        var addr = ParseMac(mac);
        if (addr is null)
        {
            log.LogWarning("WoL: ungueltige MAC '{Mac}'", mac);
            return false;
        }

        // Magic Packet aufbauen: 6 Synch-Bytes 0xFF, dann 16× die MAC.
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int rep = 0; rep < 16; rep++) Array.Copy(addr, 0, packet, 6 + rep * 6, 6);

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            // An den limitierten Broadcast und die gängigen WoL-Ports 9 und 7 senden.
            foreach (var port in new[] { 9, 7 })
                await udp.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, port), ct);

            log.LogInformation("WoL-Paket gesendet an {Mac}", mac);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "WoL-Versand fehlgeschlagen");
            return false;
        }
    }

    private static byte[]? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var parts = mac.Replace('-', ':').Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) return null;

        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, null, out bytes[i]))
                return null;
        return bytes;
    }
}
