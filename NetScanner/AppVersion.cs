using System.Reflection;

namespace NetScanner;

/// <summary>
/// Eine Stelle für die Anzeige-Version. Quelle ist die von MinVer gesetzte
/// InformationalVersion (z. B. "1.5.4-alpha.0.1+abc123"); das Git-Suffix hinter
/// dem "+" wird abgeschnitten, der Vorabversions-Teil bleibt sichtbar, damit ein
/// Entwicklungsstand im About-Dialog nicht wie ein Release aussieht.
/// </summary>
public static class AppVersion
{
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
