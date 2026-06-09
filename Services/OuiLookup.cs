namespace NetScanner.Services;

/// <summary>
/// Minimaler OUI-Lookup (erste 3 MAC-Bytes) fuer haeufige Kamera-/Netzwerk-Hersteller.
/// Bewusst klein gehalten; fuer Vollstaendigkeit die IEEE-OUI-Datei laden.
/// Zweck: einen wahrscheinlichen Kamera-Hersteller schon vor dem Portscan erkennen.
/// </summary>
public static class OuiLookup
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Kamera-/NVR-Hersteller
        ["44:19:B6"] = "Hangzhou Hikvision",
        ["C0:56:E3"] = "Hangzhou Hikvision",
        ["28:57:BE"] = "Hangzhou Hikvision",
        ["BC:AD:28"] = "Hangzhou Hikvision",
        ["3C:EF:8C"] = "Dahua",
        ["90:02:A9"] = "Dahua",
        ["E0:50:8B"] = "Zhejiang Dahua",
        ["00:40:8C"] = "Axis Communications",
        ["AC:CC:8E"] = "Axis Communications",
        ["B8:A4:4F"] = "Axis Communications",
        ["00:18:AE"] = "Vivotek",
        ["00:0F:7C"] = "ACTi",
        ["10:12:FB"] = "Reolink/Baichuan",
        ["EC:71:DB"] = "Reolink",
        ["9C:8E:CD"] = "Amcrest",
        // Netzwerk-Infrastruktur (nuetzlich zum Aussortieren)
        ["B4:FB:E4"] = "Ubiquiti",
        ["FC:EC:DA"] = "Ubiquiti",
        ["00:1A:1E"] = "Aruba",
    };

    /// <summary>Liefert den Hersteller fuer eine MAC (aus den ersten 3 Bytes), oder null.</summary>
    public static string? Resolve(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var norm = IpRangeHelper.NormalizeMac(mac);
        var oui = string.Join(':', norm.Split(':').Take(3));
        return Map.GetValueOrDefault(oui);
    }

    /// <summary>Heuristik: ist der Hersteller ein bekannter Kamera-Hersteller?</summary>
    public static bool IsLikelyCameraVendor(string? vendor) =>
        vendor is not null && (
            vendor.Contains("Hikvision", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("Dahua", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("Axis", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("Vivotek", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("ACTi", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("Reolink", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("Amcrest", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True, wenn die MAC "locally administered" ist (Bit 0x02 im ersten Oktett).
    /// Bei WLAN-Clients praktisch immer eine randomisierte MAC (Privacy-Feature) —
    /// starker Hinweis auf ein Smartphone/Tablet. OUI-Lookup ist dann sinnlos.
    /// </summary>
    public static bool IsRandomizedMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return false;
        var firstOctet = mac.Replace('-', ':').Split(':')[0];
        return byte.TryParse(firstOctet, System.Globalization.NumberStyles.HexNumber, null, out var b)
               && (b & 0x02) != 0;
    }
}
