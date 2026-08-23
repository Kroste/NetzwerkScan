
using NLog;
using SystemFile = System.IO.File;

namespace NetScanner.Config;

/// <summary>
/// Gemeinsame Datei-Primitiven für die JSON-Stores der App (Verbindungen,
/// Einstellungen, Verlauf, …).
///
/// Zwei Regeln, die in DTM lange verletzt waren und dort ein reales
/// Totalverlust-Risiko bedeutet haben:
///
/// 1. <b>Atomar schreiben.</b> Ein <c>WriteAllText</c> direkt auf die Zieldatei
///    lässt bei Absturz/Stromausfall mitten im Schreiben eine halbe Datei
///    zurück. Stattdessen erst nach <c>&lt;datei&gt;.tmp</c>, dann
///    <c>File.Move(tmp, ziel, overwrite: true)</c> — das Move ist atomar.
///
/// 2. <b>Defekte Daten nicht stillschweigend verlieren.</b> Lässt sich die
///    Datei nicht deserialisieren, wurde vorher einfach ein leeres Ergebnis
///    zurueckgegeben — der nächste Save hat die kaputte Datei dann endgueltig
///    überschrieben. Real passiert in DTM: bei <c>connections.json</c> hiess das alle Server samt
///    DPAPI-Passwoertern weg, ohne Kopie. Jetzt wandert die kaputte Datei nach
///    <c>&lt;datei&gt;.broken</c> und bleibt für Diagnose/Rettung erhalten.
///
/// Bewusst NICHT quarantaenisiert wird bei IO-Fehlern (Datei gesperrt, Netz-
/// laufwerk kurz weg): dort ist der Inhalt ja in Ordnung, nur gerade nicht
/// lesbar. Ein Verschieben würde intakte Daten aus dem Weg raeumen.
/// </summary>
internal static class JsonFileStore
{
    private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Schreibt <paramref name="json"/> atomar nach <paramref name="path"/>.
    /// Legt das Zielverzeichnis an, falls nötig.
    /// </summary>
    public static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        try
        {
            SystemFile.WriteAllText(tmp, json);
            SystemFile.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Halb geschriebene .tmp nicht liegen lassen — sie würde beim
            // nächsten Versuch ohnehin überschrieben, aber ein Restmuell im
            // AppData-Ordner verwirrt bei der Fehlersuche.
            TryDeleteTemp(tmp);
            throw;
        }
    }

    /// <summary>
    /// Verschiebt eine nicht deserialisierbare Datei nach
    /// <c>&lt;datei&gt;.broken</c>. Schlaegt das fehl (z.B. Datei gesperrt),
    /// wird nur geloggt — der Aufrufer startet in jedem Fall leer weiter.
    /// </summary>
    public static void Quarantine(string path)
    {
        string broken = path + ".broken";
        try
        {
            SystemFile.Move(path, broken, overwrite: true);
            _logger.Error("Defekte Datei nach {0} gesichert. Es wird leer weitergestartet.", broken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Defekte Datei {0} konnte nicht nach {1} gesichert werden.", path, broken);
        }
    }

    private static void TryDeleteTemp(string tmp)
    {
        try
        {
            if (SystemFile.Exists(tmp)) SystemFile.Delete(tmp);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Temporaere Datei {0} konnte nicht aufgeraeumt werden.", tmp);
        }
    }
}
