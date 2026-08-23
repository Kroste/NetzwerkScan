using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>Ein verfügbares Update samt passendem Asset für die laufende Plattform.</summary>
/// <param name="Version">Version aus dem Release-Tag, ohne führendes "v".</param>
/// <param name="ReleaseUrl">Release-Seite — Rückfallebene, wenn kein Asset passt.</param>
/// <param name="AssetName">Dateiname des Assets (null = keins für diese Plattform).</param>
/// <param name="AssetUrl">Download-URL des Assets.</param>
/// <param name="Notes">Release-Notes für die Anzeige.</param>
public sealed record UpdateInfo(
    Version Version, string ReleaseUrl, string? AssetName, string? AssetUrl, string? Notes)
{
    /// <summary>true, wenn ein Self-Update möglich ist (sonst nur Release-Seite öffnen).</summary>
    public bool CanSelfUpdate => AssetUrl is not null;
}

/// <summary>
/// Update-Check UND echtes Self-Update gegen die GitHub-Releases von
/// <c>Kroste/NetzwerkScan</c>.
///
/// Reine Notification wäre gegen den Kroste-Standard: der About-Dialog muss das
/// Update per Klick einspielen können. Der Ablauf ist immer derselbe — Asset laden,
/// plattformspezifisches Austausch-Skript starten, <b>App beenden</b>. Das Skript
/// wartet auf das Prozessende; beendet sich die App nicht selbst, bleibt die UI
/// ewig bei „100 %" stehen und das Update wird nie angewendet.
/// </summary>
public sealed class UpdateService
{
    // Repo-Slug, NICHT der Projektname: das Repository heißt NetzwerkScan.
    private const string Repo = "Kroste/NetzwerkScan";
    private const string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string ReleasesUrl = $"https://github.com/{Repo}/releases";

    private readonly ILogger<UpdateService> _log;
    private readonly HttpClient _http;
    private UpdateInfo? _cached;

    public UpdateService(ILogger<UpdateService> log)
    {
        _log = log;

        // Proxy-aware: auf dem Arbeitslaptop läuft der Verkehr über den Firmen-Proxy
        // mit Negotiate-Auth, auf Bazzite ist beides ein No-Op. Derselbe Code, beide Welten.
        var handler = new HttpClientHandler
        {
            Proxy = WebRequest.DefaultWebProxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        // User-Agent ist bei der GitHub-API Pflicht, sonst kommt 403 zurück.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"NetScanner/{AppVersion.Display}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Die laufende Version, Vorabversions-Suffix abgeschnitten.</summary>
    public static Version Current { get; } = ParseVersion(AppVersion.Display) ?? new Version(0, 0, 0);

    /// <summary>
    /// Fragt das neueste Release ab. Liefert null, wenn kein neueres vorliegt oder der
    /// Check fehlschlägt — ein Fehler wird nur geloggt (Warn) und nie als Dialog gezeigt:
    /// offline oder Proxy-Ärger darf die App nicht stören.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // GitHub begrenzt anonyme Abfragen auf 60/h pro IP -> Ergebnis merken.
        if (_cached is not null)
            return _cached.Version > Current ? _cached : null;

        var sw = Stopwatch.StartNew();
        try
        {
            _log.LogDebug("Update-Check startet: {Url}", LatestUrl);
            var release = await _http.GetFromJsonAsync<GitHubRelease>(LatestUrl, ct);
            if (release?.TagName is null)
            {
                _log.LogWarning("Update-Check: Antwort ohne tag_name nach {Ms} ms", sw.ElapsedMilliseconds);
                return null;
            }

            var version = ParseVersion(release.TagName);
            if (version is null)
            {
                _log.LogWarning("Update-Check: Tag {Tag} ist keine auswertbare Version", release.TagName);
                return null;
            }

            var asset = SelectAsset(release.Assets);
            _cached = new UpdateInfo(version, release.HtmlUrl ?? ReleasesUrl,
                asset?.Name, asset?.DownloadUrl, release.Body);

            _log.LogInformation(
                "Update-Check fertig nach {Ms} ms: neueste {Latest}, laufend {Current}, Asset {Asset}",
                sw.ElapsedMilliseconds, version, Current, asset?.Name ?? "(keins passend)");

            return version > Current ? _cached : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update-Check fehlgeschlagen nach {Ms} ms", sw.ElapsedMilliseconds);
            return null;
        }
    }

    /// <summary>
    /// Lädt das Asset herunter und startet den plattformspezifischen Austausch.
    /// </summary>
    /// <returns>
    /// true, wenn das Austausch-Skript läuft. <b>Der Aufrufer MUSS die App danach
    /// beenden</b> (<see cref="TerminateForUpdate"/>) — das Skript wartet auf das
    /// Prozessende.
    /// </returns>
    public async Task<bool> DownloadAndApplyAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (info.AssetUrl is null || info.AssetName is null)
        {
            _log.LogWarning("Kein Asset für diese Plattform — Self-Update nicht möglich");
            return false;
        }

        string work = Path.Combine(Path.GetTempPath(), $"NetScanner-update-{info.Version}");
        Directory.CreateDirectory(work);
        string package = Path.Combine(work, info.AssetName);

        try
        {
            _log.LogInformation("Update {Version} wird geladen: {Asset}", info.Version, info.AssetName);
            await DownloadAsync(info.AssetUrl, package, progress, ct);

            // SelectAsset liefert die Linux-Assets nur unter Linux; der Plattform-Check
            // hier macht das fuer den Analyzer (CA1416) explizit.
            bool started;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && info.AssetName.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            {
                started = StartAppImageInstaller(work, package);
            }
            else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                     && info.AssetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                started = StartTarballInstaller(work, package);
            }
            else
            {
                started = StartWindowsInstaller(work, package);
            }

            if (started)
                _log.LogInformation("Austausch-Skript gestartet — App wird jetzt beendet");

            return started;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update {Version} konnte nicht angewendet werden", info.Version);
            return false;
        }
    }

    /// <summary>
    /// Beendet die App, damit das wartende Austausch-Skript weiterlaufen kann.
    /// <b>Jeder</b> Aufrufer von <see cref="DownloadAndApplyAsync"/> muss das bei
    /// Erfolg tun — sonst wartet das Skript ewig auf die PID und die UI hängt bei
    /// „100 %". Der Kill-Fallback greift, falls ein Finalizer den sauberen Exit
    /// blockiert; der Installer braucht kein sauberes Ende, nur das Verschwinden
    /// der PID.
    /// </summary>
    public static void TerminateForUpdate()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Process.GetCurrentProcess().Kill();
        });

        NLog.LogManager.Shutdown();
        Environment.Exit(0);
    }

    // ------------------------------------------------------------------ Download

    private async Task DownloadAsync(
        string url, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(target);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total is > 0)
                progress?.Report(done * 100.0 / total.Value);
        }

        progress?.Report(100);
        _log.LogDebug("Download fertig: {Bytes} Byte -> {Target}", done, target);
    }

    // ---------------------------------------------------------------- Installer

    /// <summary>
    /// Windows: eine laufende .exe kann sich nicht selbst überschreiben, deshalb ein
    /// externer Batch-Prozess. Die Zeilen werden OHNE Einrückung geschrieben — ein
    /// eingerücktes <c>:label</c> ist für cmd kein gültiges Sprungziel, das goto
    /// scheitert still und xcopy läuft los, während die alte App noch sperrt.
    /// </summary>
    private bool StartWindowsInstaller(string work, string package)
    {
        string extract = Path.Combine(work, "new");
        if (Directory.Exists(extract)) Directory.Delete(extract, recursive: true);
        ZipFile.ExtractToDirectory(package, extract);

        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(appDir, "NetScanner.exe");
        string script = Path.Combine(work, "install.bat");
        int pid = Environment.ProcessId;

        File.WriteAllLines(script,
        [
            "@echo off",
            $"set LOG=\"{Path.Combine(work, "update.log")}\"",
            "echo Warte auf Prozessende... >> %LOG%",
            // Wait-Process blockiert sauber; eine tasklist-Polling-Schleife tut das nicht.
            $"powershell -NoProfile -Command \"Wait-Process -Id {pid} -ErrorAction SilentlyContinue\"",
            "timeout /t 2 /nobreak > nul",
            "echo Kopiere Dateien... >> %LOG%",
            $"xcopy \"{extract}\\*\" \"{appDir}\\\" /E /Y /I >> %LOG% 2>&1",
            "echo Starte neu... >> %LOG%",
            $"start \"\" \"{exe}\"",
        ]);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return true;
    }

    /// <summary>
    /// Linux-AppImage: die laufende Datei lässt sich nicht per mv/rm ersetzen, solange
    /// sie als Loop-Device gemountet ist ("Text file busy") — <c>cp -f</c> behält den
    /// Inode und funktioniert. Der Log geht bewusst NICHT ins BaseDirectory: das ist
    /// beim laufenden AppImage der read-only Squashfs-Mount, ein Redirect dorthin
    /// bricht bash sofort ab und die App wird nie ersetzt.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private bool StartAppImageInstaller(string work, string package)
    {
        string? target = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(target))
        {
            _log.LogWarning("APPIMAGE ist nicht gesetzt — läuft die App wirklich als AppImage?");
            return false;
        }

        string script = Path.Combine(work, "install.sh");
        WriteShellScript(script,
        [
            "#!/usr/bin/env bash",
            "set -u",
            // Schreibbarer Log-Pfad: das BaseDirectory ist read-only.
            "STATE=\"${XDG_STATE_HOME:-$HOME/.local/state}/NetScanner\"",
            "mkdir -p \"$STATE\" 2>/dev/null || STATE=/tmp",
            "exec >>\"$STATE/update.log\" 2>&1",
            "echo \"--- $(date) AppImage-Update ---\"",
            $"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.3; done",
            $"cp -f '{package}' '{target}'",
            $"chmod +x '{target}'",
            $"setsid '{target}' >/dev/null 2>&1 &",
        ]);

        Process.Start(new ProcessStartInfo("/bin/bash", $"'{script}'") { UseShellExecute = false });
        return true;
    }

    /// <summary>Linux-tar.gz: neben die Installation entpacken, Binary ausführbar machen, neu starten.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private bool StartTarballInstaller(string work, string package)
    {
        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(appDir, "NetScanner");
        string script = Path.Combine(work, "install.sh");

        WriteShellScript(script,
        [
            "#!/usr/bin/env bash",
            "set -u",
            "STATE=\"${XDG_STATE_HOME:-$HOME/.local/state}/NetScanner\"",
            "mkdir -p \"$STATE\" 2>/dev/null || STATE=/tmp",
            "exec >>\"$STATE/update.log\" 2>&1",
            "echo \"--- $(date) tar.gz-Update ---\"",
            $"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.3; done",
            $"tar -xzf '{package}' -C '{appDir}'",
            $"chmod +x '{exe}'",
            $"setsid '{exe}' >/dev/null 2>&1 &",
        ]);

        Process.Start(new ProcessStartInfo("/bin/bash", $"'{script}'") { UseShellExecute = false });
        return true;
    }

    /// <summary>
    /// Schreibt ein Shell-Skript mit harten LF-Zeilenenden. AppendLine würde auf
    /// Windows CRLF erzeugen; bash bricht dann mit "unexpected end of file" ab, weil
    /// das \r an fi/then/done klebt.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void WriteShellScript(string path, IEnumerable<string> lines)
    {
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\n")));
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // ------------------------------------------------------------------ Helfer

    /// <summary>
    /// Wählt das Asset für die laufende Plattform. Namensschema aus release.yml:
    /// NetScanner-vX.Y.Z-win-x64.zip, -linux-x64.tar.gz, -x86_64.AppImage.
    /// Unter Linux hat das AppImage Vorrang, wenn die App selbst als AppImage läuft.
    /// </summary>
    private static GitHubAsset? SelectAsset(IReadOnlyList<GitHubAsset>? assets)
    {
        if (assets is null || assets.Count == 0)
            return null;

        GitHubAsset? Find(string suffix) => assets.FirstOrDefault(
            a => a.Name?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Find("-win-x64.zip");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            bool runningAsAppImage =
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE"));
            return runningAsAppImage
                ? Find("-x86_64.AppImage")
                : Find("-linux-x64.tar.gz");
        }

        return null;
    }

    /// <summary>
    /// Parst "v1.5.3", "1.5.3" oder "1.5.4-alpha.0.1" zu einer vergleichbaren Version.
    /// Vorabversions- und Build-Suffix werden abgeschnitten; ein Stringvergleich würde
    /// sonst 1.10.0 für kleiner als 1.9.0 halten.
    /// </summary>
    internal static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        int cut = s.IndexOfAny(['-', '+']);
        if (cut > 0)
            s = s[..cut];

        return Version.TryParse(s, out var v) ? v : null;
    }

    // ------------------------------------------------------- GitHub-API-Vertrag

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}
