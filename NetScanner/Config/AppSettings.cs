using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetScanner.Config;

/// <summary>
/// Persistente Nutzereinstellungen. Alle Felder sind nullable bzw. haben einen
/// Default, damit eine ältere Datei ohne das Feld weiterhin lädt.
/// </summary>
/// <param name="UiCulture">ISO-Code der UI-Sprache ("en"/"de"). null = Systemsprache.</param>
/// <param name="LastCidr">Zuletzt gescannter Netzbereich, als Vorbelegung beim Start.</param>
public sealed record AppSettings(string? UiCulture = null, string? LastCidr = null);

/// <summary>
/// Lädt und speichert <see cref="AppSettings"/> unter
/// <c>$XDG_CONFIG_HOME/NetScanner</c> bzw. <c>%AppData%\NetScanner</c> — bewusst
/// nicht neben der Exe, die kann in einem schreibgeschützten Verzeichnis liegen
/// (AppImage-Squashfs, Program Files).
/// </summary>
public sealed class AppSettingsService
{
    private readonly ILogger<AppSettingsService> _log;
    private readonly string _path;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettingsService(ILogger<AppSettingsService> log)
    {
        _log = log;
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetScanner", "settings.json");
        Current = Load();
    }

    /// <summary>Der aktuelle Stand. Änderungen über <see cref="Update"/>.</summary>
    public AppSettings Current { get; private set; }

    /// <summary>Ändert die Einstellungen und schreibt sie sofort weg.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        Save();
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
            _log.LogDebug("Einstellungen geladen: {Path}", _path);
            return loaded ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            // NUR bei kaputtem JSON quarantänisieren. Bei einem IO-Fehler (Datei
            // gesperrt, Laufwerk kurz weg) ist der Inhalt intakt — ein .broken-Move
            // wuerde dann gute Daten wegraeumen.
            _log.LogError(ex, "Einstellungen sind kein gültiges JSON: {Path}", _path);
            JsonFileStore.Quarantine(_path);
            return new AppSettings();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Einstellungen konnten nicht gelesen werden: {Path}", _path);
            return new AppSettings();
        }
    }

    private void Save()
    {
        try
        {
            JsonFileStore.WriteAtomic(_path, JsonSerializer.Serialize(Current, JsonOptions));
            _log.LogDebug("Einstellungen gespeichert: {Path}", _path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Einstellungen konnten nicht gespeichert werden: {Path}", _path);
        }
    }
}
