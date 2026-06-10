using System.Runtime.Versioning;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace NetScanner.Services;

/// <summary>Ergebnis der libvlc-Suche — erlaubt der UI einen praezisen Hinweis.</summary>
public enum VlcStatus
{
    /// <summary>libvlc gefunden und erfolgreich initialisiert.</summary>
    Available,
    /// <summary>Keine VLC-Installation gefunden.</summary>
    NotFound,
    /// <summary>VLC gefunden, aber falsche Architektur (z. B. 32-bit unter der 64-bit-App).</summary>
    WrongArchitecture,
    /// <summary>libvlc gefunden, aber die Initialisierung schlug fehl.</summary>
    InitFailed
}

/// <summary>
/// Lokalisiert eine vorhandene libvlc-Installation und initialisiert LibVLCSharp.
/// NetScanner buendelt libvlc bewusst NICHT mehr selbst (spart ~85 MB pro Windows-Build).
/// Die Kamera-Vorschau nutzt die libvlc der lokalen VLC-Installation:
///   Windows: VLC (64-bit) — Suchreihenfolge: NETSCANNER_VLC_DIR, Registry, %ProgramFiles%
///   Linux:   System-libvlc (Paket vlc / libvlc) ueber den normalen Loader
///
/// Schlaegt die Suche fehl, bleibt <see cref="Status"/> aussagekraeftig (NotFound /
/// WrongArchitecture / InitFailed) und die App laeuft normal weiter — die UI ersetzt die
/// Vorschau durch einen passenden Hinweis. Die Kern-Funktionen (Scan, Portscan, Erkennung)
/// haengen NICHT von libvlc ab.
/// </summary>
public static class VlcLocator
{
    private static bool _tried;

    /// <summary>Detaillierter Status der libvlc-Bereitstellung.</summary>
    public static VlcStatus Status { get; private set; } = VlcStatus.NotFound;

    /// <summary>True, wenn libvlc gefunden und initialisiert wurde.</summary>
    public static bool IsAvailable => Status == VlcStatus.Available;

    /// <summary>Verzeichnis, aus dem libvlc geladen wurde (null = System-Pfad). Nur Diagnose.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>Die gemeinsame LibVLC-Instanz oder null, wenn nicht verfuegbar.</summary>
    public static LibVLC? Shared { get; private set; }

    /// <summary>
    /// Einmalige, fehlertolerante Initialisierung. Sollte frueh beim App-Start laufen,
    /// damit der Status feststeht, bevor die UI dagegen bindet. Mehrfachaufruf ist gefahrlos.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var (dir, wrongArch) = FindWindowsVlc();
                if (wrongArch) { Status = VlcStatus.WrongArchitecture; return; }
                if (dir is null) { Status = VlcStatus.NotFound; return; }
                Core.Initialize(dir);
                LoadedFrom = dir;
            }
            else
            {
                Core.Initialize();                // System-Loader (Linux/macOS)
            }

            // Reconnect & geringe Latenz fuer Kamerastreams.
            Shared = new LibVLC("--network-caching=300", "--rtsp-tcp", "--no-audio");
            Status = VlcStatus.Available;
        }
        catch
        {
            // Core.Initialize/new LibVLC schlug fehl (z. B. beschaedigte Installation).
            Status = VlcStatus.InitFailed;
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

    /// <summary>Sucht das VLC-Verzeichnis. Rueckgabe: (Pfad, wrongArch). wrongArch=true,
    /// wenn zwar eine VLC gefunden wurde, aber in der falschen Architektur.</summary>
    [SupportedOSPlatform("windows")]
    private static (string? dir, bool wrongArch) FindWindowsVlc()
    {
        bool sawWrongArch = false;

        foreach (var cand in CandidateDirs())
        {
            var dll = Path.Combine(cand, "libvlc.dll");
            if (!File.Exists(dll)) continue;
            if (IsX64Dll(dll)) return (cand, false);
            sawWrongArch = true;            // gefunden, aber z. B. 32-bit -> weitersuchen
        }
        return (null, sawWrongArch);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> CandidateDirs()
    {
        // 1) Expliziter Override fuer Custom-/Portable-Installationen.
        var env = Environment.GetEnvironmentVariable("NETSCANNER_VLC_DIR");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        // 2) Registry (der Standard-Installer schreibt InstallDir) — fuer Pfade abseits
        //    der Vorgabe. Beide Views, falls 32/64-bit gemischt registriert ist.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? dir = null;
            try
            {
                using var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = bk.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
                dir = k?.GetValue("InstallDir") as string;
            }
            catch { /* Registry nicht lesbar -> ueberspringen */ }
            if (!string.IsNullOrWhiteSpace(dir)) yield return dir;
        }

        // 3) Standard-Installationspfad (64-bit).
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC");
    }

    /// <summary>
    /// Liest aus dem PE-Header der DLL das Machine-Feld und prueft auf x64.
    /// PE-Layout: an Offset 0x3C steht der Offset der PE-Signatur ("PE\0\0"), danach folgt
    /// im COFF-Header (Offset +4) das 2-Byte-Machine-Feld (0x8664 = AMD64).
    /// So erkennen wir eine 32-bit-VLC, bevor das Laden mit BadImageFormat crasht.
    /// </summary>
    private static bool IsX64Dll(string dllPath)
    {
        try
        {
            using var fs = File.OpenRead(dllPath);
            using var br = new BinaryReader(fs);
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x0000_4550) return false;   // "PE\0\0"
            ushort machine = br.ReadUInt16();
            return machine == 0x8664;                           // IMAGE_FILE_MACHINE_AMD64
        }
        catch { return false; }
    }
}
