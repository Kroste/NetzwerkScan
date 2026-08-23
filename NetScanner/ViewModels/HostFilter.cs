using NetScanner.Models;

namespace NetScanner.ViewModels;

/// <summary>Kategorie-Filter für die Host-Liste.</summary>
public enum HostFilter
{
    /// <summary>Alle gefundenen Hosts.</summary>
    All,
    /// <summary>Nur erkannte Kameras.</summary>
    Cameras,
    /// <summary>Nur Hosts mit einem Web-Interface (HTTP/HTTPS).</summary>
    Web,
    /// <summary>Nur Hosts mit einem Schwachstellen-Befund aus dem Audit.</summary>
    Vulnerable
}

/// <summary>
/// Reine Filter-Logik für die Host-Liste — bewusst getrennt vom ViewModel, damit
/// sie ohne UI testbar bleibt. Kombiniert Kategorie und Freitext (UND-Verknüpfung).
/// </summary>
public static class HostFilters
{
    /// <summary>
    /// True, wenn der Host zur gewählten Kategorie UND zum (optionalen) Suchtext passt.
    /// Der Suchtext matcht case-insensitiv gegen IP, besten Namen, Hersteller und
    /// Gerätezusammenfassung.
    /// </summary>
    public static bool Matches(HostResult host, HostFilter filter, string? search)
    {
        if (!MatchesCategory(host, filter)) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;

        string q = search.Trim();
        return Contains(host.Address.ToString(), q)
            || Contains(host.BestName, q)
            || Contains(host.Vendor, q)
            || Contains(host.DeviceSummary, q)
            || Contains(host.MacAddress, q);
    }

    private static bool MatchesCategory(HostResult host, HostFilter filter) => filter switch
    {
        HostFilter.Cameras => host.IsCamera,
        HostFilter.Web => host.HasWebUi,
        HostFilter.Vulnerable => host.IsVulnerable,
        _ => true
    };

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
