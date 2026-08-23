namespace NetScanner.Localization;

/// <summary>
/// Kurzform für Übersetzungen aus Code (ViewModels, Services, Log-freie
/// UI-Texte). Im XAML wird stattdessen <c>{loc:Tr Key}</c> genutzt.
/// </summary>
public static class L
{
    /// <summary>Übersetzt einen Key.</summary>
    public static string T(string key) => LocalizationService.Instance[key];

    /// <summary>Übersetzt einen Key und setzt Platzhalter ein.</summary>
    public static string F(string key, params object?[] args)
        => string.Format(LocalizationService.Instance[key], args);

    /// <summary>
    /// Übersetzt einen Wert, der entweder ein reiner Key, ein Key mit einem
    /// Argument in der Form <c>Key|Wert</c> oder ein sprachneutraler Klartext ist.
    ///
    /// WARUM: Services wie <c>DeviceClassifier</c> und <c>PasswordStrength</c>
    /// liefern bewusst Keys statt fertiger Texte — sonst müssten sie den
    /// LocalizationService kennen und wären schlechter testbar. Manche ihrer
    /// Rückgaben sind aber Produktnamen oder Text, den ein Gerät selbst gemeldet
    /// hat ("Windows", "Chromecast/Google TV", UPnP-Angaben). Alles, was kein
    /// bekannter Key ist, geht deshalb unverändert durch.
    /// </summary>
    public static string TOrText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        int bar = value.IndexOf('|');
        if (bar > 0)
        {
            string key = value[..bar];
            string arg = value[(bar + 1)..];
            string pattern = LocalizationService.Instance[key];
            return pattern.StartsWith('!') ? arg : string.Format(pattern, arg);
        }

        string text = LocalizationService.Instance[value];
        // Der Service liefert !Key! zurück, wenn er den Key nicht kennt — dann ist
        // es Klartext und bleibt, wie es ist.
        return text.StartsWith('!') ? value : text;
    }
}
