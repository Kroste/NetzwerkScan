using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>
/// Kamera-Vorschau nach Kroste-Standard: ein Standbild per ffmpeg-Frame-Grab aus
/// dem RTSP-Stream plus die Möglichkeit, den Stream im System-Default-Player zu
/// öffnen. Bewusst KEIN eingebettetes LibVLC — das ist auf Linux/AppImage
/// Deployment-Hölle und bringt das Airspace-Problem mit (siehe
/// kroste-avalonia/references/media-preview.md). Vorbild: Spektivs FfmpegFrameGrabber.
///
/// ffmpeg ist eine Laufzeit-Abhängigkeit, aber optional: fehlt es, liefert
/// <see cref="GrabFrameAsync"/> null und die UI zeigt statt Standbild einen Hinweis.
/// Der externe Player funktioniert auch ohne ffmpeg.
/// </summary>
public sealed class MediaPreviewService
{
    private readonly ILogger<MediaPreviewService> _log;

    public MediaPreviewService(ILogger<MediaPreviewService> log)
    {
        _log = log;
        FfmpegPath = ResolveFfmpeg();
        _log.LogInformation("ffmpeg für Kamera-Vorschau: {Path}", FfmpegPath ?? "nicht gefunden");
    }

    /// <summary>Pfad zur ffmpeg-Executable oder null, wenn keine gefunden wurde.</summary>
    public string? FfmpegPath { get; }

    /// <summary>True, wenn ffmpeg verfügbar ist und ein Standbild gegrabbt werden kann.</summary>
    public bool HasFfmpeg => FfmpegPath is not null;

    /// <summary>
    /// Grabbt ein einzelnes Standbild aus einem (RTSP-)Stream als JPEG-Bytes.
    /// Liefert null, wenn ffmpeg fehlt, der Stream nicht erreichbar ist oder das
    /// Zeitlimit greift — der Aufrufer fällt dann still auf einen Hinweis zurück.
    /// </summary>
    public async Task<byte[]?> GrabFrameAsync(string streamUrl, int timeoutMs, CancellationToken ct)
    {
        if (FfmpegPath is null) return null;

        // In eine temporäre Datei grabben und danach lesen (robuster als pipe:1 quer
        // über ffmpeg-Versionen). Eindeutiger Name -> keine Kollision bei parallelen Grabs.
        string tmp = Path.Combine(Path.GetTempPath(), $"netscanner-frame-{Guid.NewGuid():N}.jpg");
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        // -nostdin: ffmpeg soll nicht auf die Konsole warten. -rtsp_transport tcp:
        // viele Kameras liefern über UDP kein zuverlässiges erstes Frame.
        foreach (var a in new[]
                 {
                     "-nostdin", "-loglevel", "error", "-rtsp_transport", "tcp",
                     "-y", "-i", streamUrl, "-frames:v", "1", "-q:v", "4", tmp,
                 })
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Stream hängt: ffmpeg abschießen und als "kein Bild" behandeln.
                TryKill(proc);
                _log.LogDebug("Frame-Grab Zeitüberschreitung nach {Ms} ms für {Url}",
                    timeoutMs, MaskCredentials(streamUrl));
                return null;
            }

            if (proc.ExitCode != 0 || !File.Exists(tmp))
            {
                string err = (await proc.StandardError.ReadToEndAsync(ct)).Trim();
                _log.LogDebug("Frame-Grab fehlgeschlagen (Exit {Code}) für {Url}: {Err}",
                    proc.ExitCode, MaskCredentials(streamUrl), err);
                return null;
            }

            return await File.ReadAllBytesAsync(tmp, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Frame-Grab-Fehler für {Url}", MaskCredentials(streamUrl));
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* Aufräumen best effort */ }
        }
    }

    /// <summary>
    /// Öffnet den Stream im System-Default-Player (Linux xdg-open, Windows
    /// Datei-/URL-Assoziation, macOS open) — echtes Live-Video mit Ton in einem
    /// eigenen Fenster. Braucht kein ffmpeg.
    /// </summary>
    public bool OpenExternal(string streamUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(streamUrl) { UseShellExecute = true });
            _log.LogInformation("Stream im externen Player geöffnet: {Url}", MaskCredentials(streamUrl));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Externer Player konnte nicht gestartet werden: {Url}", MaskCredentials(streamUrl));
            return false;
        }
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* egal */ }
    }

    /// <summary>Credentials in einer rtsp://user:pass@host-URL fürs Log maskieren.</summary>
    private static string MaskCredentials(string url)
    {
        int at = url.IndexOf('@');
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (at > 0 && scheme > 0 && at > scheme)
            return string.Concat(url.AsSpan(0, scheme + 3), "***@", url.AsSpan(at + 1));
        return url;
    }

    /// <summary>
    /// Sucht ffmpeg: erst die Override-Env-Variable, dann PATH, dann typische Pfade.
    /// Kein Fund -> null, die App läuft ohne Standbild weiter.
    /// </summary>
    private static string? ResolveFfmpeg()
    {
        var overridePath = Environment.GetEnvironmentVariable("NETSCANNER_FFMPEG");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        // PATH durchsuchen.
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* ungültiger PATH-Eintrag */ }
            }
        }

        // Typische Installationsorte als Rückfallebene.
        string[] common = OperatingSystem.IsWindows()
            ? [@"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe"]
            : ["/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg", "/run/host/usr/bin/ffmpeg"];
        return common.FirstOrDefault(File.Exists);
    }
}
