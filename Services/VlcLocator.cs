using LibVLCSharp.Shared;

namespace NetScanner.Services;

/// <summary>
/// Lokalisiert eine vorhandene libvlc-Installation und initialisiert LibVLCSharp.
/// NetScanner buendelt libvlc bewusst NICHT mehr selbst (spart ~85 MB pro Windows-Build).
/// Die Kamera-Vorschau nutzt die libvlc der lokalen VLC-Installation:
///   Windows: VLC (64-bit) unter %ProgramFiles%\VideoLAN\VLC
///   Linux:   System-libvlc (Paket vlc / libvlc) ueber den normalen Loader
///
/// Ist libvlc nicht auffindbar oder nicht ladbar (z. B. 32-bit-VLC unter einer 64-bit-App),
/// bleibt <see cref="IsAvailable"/> false und die App laeuft normal weiter — die UI ersetzt
/// die Vorschau dann durch einen Hinweis. Die Kern-Funktionen (Scan, Portscan, Erkennung)
/// haengen NICHT von libvlc ab.
/// </summary>
public static class VlcLocator
{
    private static bool _tried;

    /// <summary>True, sobald libvlc gefunden und erfolgreich initialisiert wurde.</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>Verzeichnis, aus dem libvlc geladen wurde (null = System-Pfad). Nur Diagnose.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>Die gemeinsame LibVLC-Instanz oder null, wenn nicht verfuegbar.</summary>
    public static LibVLC? Shared { get; private set; }

    /// <summary>
    /// Einmalige, fehlertolerante Initialisierung. Sollte frueh beim App-Start laufen,
    /// damit <see cref="IsAvailable"/> feststeht, bevor die UI dagegen bindet.
    /// Mehrfachaufruf ist gefahrlos.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dir = FindWindowsVlc();
                if (dir is null) return;          // keine passende VLC-Installation
                Core.Initialize(dir);
                LoadedFrom = dir;
            }
            else
            {
                Core.Initialize();                // System-Loader (Linux/macOS)
            }

            // Reconnect & geringe Latenz fuer Kamerastreams. new LibVLC wirft bei einem
            // Architektur-Mismatch (z. B. 32-bit-libvlc) — das faengt der catch ab.
            Shared = new LibVLC("--network-caching=300", "--rtsp-tcp", "--no-audio");
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
            Shared = null;
            LoadedFrom = null;
        }
    }

    /// <summary>Gibt die LibVLC-Instanz frei. Beim App-Shutdown aufrufen, sonst halten
    /// die nativen libvlc-Threads den Prozess am Leben.</summary>
    public static void Shutdown()
    {
        try { Shared?.Dispose(); } catch { /* beim Beenden ignorieren */ }
        Shared = null;
    }

    private static string? FindWindowsVlc()
    {
        // 1) Optionaler Override fuer Custom-Installationen.
        var env = Environment.GetEnvironmentVariable("NETSCANNER_VLC_DIR");
        if (!string.IsNullOrWhiteSpace(env) && HasLibVlc(env)) return env;

        // 2) Standard-Installationspfad. Bewusst NUR der 64-bit-Pfad (%ProgramFiles%):
        //    Die App ist x64, eine 32-bit-VLC (Program Files (x86)) wuerde beim Laden
        //    der libvlc.dll mit BadImageFormat abstuerzen.
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dir = Path.Combine(pf, "VideoLAN", "VLC");
        return HasLibVlc(dir) ? dir : null;
    }

    private static bool HasLibVlc(string dir) =>
        File.Exists(Path.Combine(dir, "libvlc.dll"));
}
