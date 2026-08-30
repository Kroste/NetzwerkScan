using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NetScanner.Models;
using NetScanner.Services;
using NetScanner.Localization;

namespace NetScanner.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IScanOrchestrator _orchestrator;
    private readonly WolSender _wol;
    private readonly MediaPreviewService _preview;
    private readonly ILogger<MainViewModel> _log;
    private readonly ILogger _audit;            // Logger-Name "UserInput" -> Audit-Datei
    private CancellationTokenSource? _cts;

    /// <summary>Alle Scan-Ergebnisse (Roh-Liste). <see cref="Hosts"/> ist die gefilterte Sicht.</summary>
    private readonly List<HostResult> _allHosts = [];

    /// <summary>Gefilterte, in der Liste angezeigte Hosts.</summary>
    public ObservableCollection<HostResult> Hosts { get; } = [];

    /// <summary>Aktiver Kategorie-Filter (Alle / Kameras / Web / Schwachstellen).</summary>
    [ObservableProperty] private HostFilter _selectedFilter = HostFilter.All;

    /// <summary>Freitext-Filter (IP, Name, Hersteller, Gerätetyp).</summary>
    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnSelectedFilterChanged(HostFilter value)
    {
        _audit?.LogInformation("FILTER category={Filter}", value);
        RebuildFilteredList();
    }

    partial void OnFilterTextChanged(string value) => RebuildFilteredList();

    /// <summary>
    /// Baut die sichtbare <see cref="Hosts"/>-Liste aus der Roh-Liste neu auf.
    /// Clear+Add ist hier korrekt (einmalige Nutzer-Aktion, kein Background-Takt).
    /// Die aktuelle Auswahl wird über den Rebuild gerettet: die ListBox nullt
    /// SelectedItem bei Clear, und das TwoWay-Binding zöge das sonst ins VM.
    /// </summary>
    private void RebuildFilteredList()
    {
        var keep = SelectedHost;

        Hosts.Clear();
        foreach (var host in _allHosts)
            if (HostFilters.Matches(host, SelectedFilter, FilterText))
                Hosts.Add(host);

        if (keep is not null && Hosts.Contains(keep))
            SelectedHost = keep;
    }

    [ObservableProperty] private string _cidr;
    [ObservableProperty] private bool _scanFullPorts;
    [ObservableProperty] private bool _probeRtsp = true;
    [ObservableProperty] private bool _auditCredentials;
    [ObservableProperty] private string? _rtspUser;
    [ObservableProperty] private string? _rtspPass;
    [ObservableProperty] private int _onvifListenMs = 3000;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _status = "Bereit.";
    [ObservableProperty] private int _hostCount;
    [ObservableProperty] private int _cameraCount;

    [ObservableProperty] private HostResult? _selectedHost;

    /// <summary>Aktuell für die Vorschau gewählter RTSP-Stream (null = keiner).</summary>
    [ObservableProperty] private string? _selectedStreamUrl;

    /// <summary>Aktuelles Vorschau-Standbild (ffmpeg-Frame-Grab) oder null.</summary>
    [ObservableProperty] private Bitmap? _previewFrame;

    /// <summary>Statuszeile der Vorschau (Verbinden / kein Bild / ffmpeg fehlt) oder null.</summary>
    [ObservableProperty] private string? _previewStatus;

    private CancellationTokenSource? _previewCts;

    /// <summary>Vorschau-Bereich zeigen, sobald ein Stream gewählt ist.</summary>
    public bool ShowPreview => !string.IsNullOrWhiteSpace(SelectedStreamUrl);

    // Vorschau-Schleife starten/stoppen, sobald sich die Stream-URL ändert.
    partial void OnSelectedStreamUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowPreview));
        OpenExternalStreamCommand.NotifyCanExecuteChanged();
        RestartPreview(value);
    }

    /// <summary>
    /// Startet die Frame-Grab-Schleife neu: alle ~1,5 s ein frisches Standbild aus
    /// dem RTSP-Stream. Kein flüssiges Video — flüssig läuft der Stream nur im
    /// externen Player (bewusster Kroste-Standard, siehe MediaPreviewService).
    /// </summary>
    private void RestartPreview(string? url)
    {
        _previewCts?.Cancel();
        _previewCts = null;
        SetPreviewFrame(null);

        if (string.IsNullOrWhiteSpace(url))
        {
            PreviewStatus = null;
            return;
        }

        if (!_preview.HasFfmpeg)
        {
            PreviewStatus = L.T("Preview_FfmpegMissing");
            return;   // Externer Player bleibt nutzbar.
        }

        PreviewStatus = L.T("Preview_Connecting");
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = PreviewLoopAsync(url, cts.Token);
    }

    private async Task PreviewLoopAsync(string url, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? jpeg = await _preview.GrabFrameAsync(url, timeoutMs: 6000, ct);
                if (ct.IsCancellationRequested) break;

                // Decode pro Iteration absichern: ein abgeschnittenes/korruptes JPEG
                // (kommt bei wackligen RTSP-Streams vor) lässt "new Bitmap" werfen. Ohne
                // dieses lokale catch würde eine einzelne kaputte Kachel die GANZE
                // Schleife beenden und die Vorschau bis zur Neuwahl einfrieren.
                Bitmap? bitmap = null;
                if (jpeg is { Length: > 0 })
                {
                    try
                    {
                        using var ms = new MemoryStream(jpeg);
                        bitmap = new Bitmap(ms);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Vorschau-Frame nicht dekodierbar — übersprungen");
                    }
                }

                if (bitmap is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) { bitmap.Dispose(); return; }
                        SetPreviewFrame(bitmap);
                        PreviewStatus = null;
                    });
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (PreviewFrame is null) PreviewStatus = L.T("Preview_NoSignal");
                    });
                }

                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException) { /* Stream gewechselt/geschlossen */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vorschau-Schleife abgebrochen");
        }
    }

    private void SetPreviewFrame(Bitmap? frame)
    {
        var old = PreviewFrame;
        PreviewFrame = frame;
        old?.Dispose();   // altes Bild freigeben, sonst leckt jeder Frame Speicher
    }

    public MainViewModel(IScanOrchestrator orchestrator, WolSender wol, MediaPreviewService preview,
        ILogger<MainViewModel> log, ILoggerFactory factory)
    {
        _orchestrator = orchestrator;
        _wol = wol;
        _preview = preview;
        _log = log;
        _audit = factory.CreateLogger("UserInput");
        _cidr = IpRangeHelper.LocalSubnets().FirstOrDefault() ?? "192.168.10.0/24";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScanAsync()
    {
        // Eingabe vorab prüfen -> klare Meldung statt generischem Fehler tief im Scan.
        if (!IpRangeHelper.IsValidCidr(Cidr))
        {
            Status = L.T("Status_InvalidCidr");
            _audit.LogInformation("SCAN_REJECT | ungültige CIDR: {Cidr}", Cidr);
            return;
        }

        // --- Audit: vollständige Eingabe protokollieren ---
        _audit.LogInformation(
            "SCAN_START | cidr={Cidr} | fullPorts={Full} | probeRtsp={Rtsp} | audit={Audit} | onvifMs={Onvif} | rtspUser={User}",
            Cidr, ScanFullPorts, ProbeRtsp, AuditCredentials, OnvifListenMs,
            string.IsNullOrEmpty(RtspUser) ? "(leer)" : RtspUser);   // Passwort NICHT loggen

        _allHosts.Clear();
        Hosts.Clear();
        HostCount = 0;
        CameraCount = 0;
        SelectedStreamUrl = null;
        IsScanning = true;
        _cts = new CancellationTokenSource();

        var opt = new ScanOptions
        {
            Cidr = Cidr.Trim(),
            Ports = ScanFullPorts ? [.. Enumerable.Range(1, 65535)] : PortScanner.CommonPorts,
            ProbeRtsp = ProbeRtsp,
            AuditCredentials = AuditCredentials,
            OnvifListenMs = OnvifListenMs,
            RtspUser = RtspUser,
            RtspPass = RtspPass
        };

        try
        {
            await foreach (var host in _orchestrator.RunAsync(opt, _cts.Token))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // In die Roh-Liste, in die sichtbare Liste nur wenn der Filter passt.
                    // Add statt Clear+Refill: kein Flimmern im Streaming-Takt (Skill-Regel).
                    _allHosts.Add(host);
                    if (HostFilters.Matches(host, SelectedFilter, FilterText))
                        Hosts.Add(host);

                    HostCount = _allHosts.Count;
                    if (host.IsCamera) CameraCount++;

                    // Offener/per Werks-Login zugänglicher Stream: erste solche Kamera
                    // automatisch auswählen und die Vorschau öffnen (Beleg der Schwachstelle).
                    if (host.RtspVulnerable && string.IsNullOrEmpty(SelectedStreamUrl))
                    {
                        SelectedHost = host;
                        SelectedStreamUrl = host.RtspUri;
                    }

                    Status = L.F("Status_Running", HostCount, CameraCount);
                });
            }
            Status = L.F("Status_Done", HostCount, CameraCount);
        }
        catch (OperationCanceledException)
        {
            Status = L.F("Status_Cancelled", _allHosts.Count);
            _audit.LogInformation("SCAN_CANCEL | gefunden={Count}", _allHosts.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scan fehlgeschlagen");
            Status = L.F("Status_Error", ex.Message);
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanStart() => !IsScanning && !string.IsNullOrWhiteSpace(Cidr);

    [RelayCommand]
    private void CancelScan()
    {
        _audit.LogInformation("SCAN_CANCEL_REQUEST");
        _cts?.Cancel();
    }

    [RelayCommand]
    private void OpenStream(HostResult? host)
    {
        var uri = host?.Camera?.RtspUri;
        if (string.IsNullOrWhiteSpace(uri)) { Status = L.T("Status_NoRtspUrl"); return; }

        // Falls beim Scan keine Credentials gesetzt waren, jetzt einsetzen.
        if (!string.IsNullOrEmpty(RtspUser) && !uri.Contains('@'))
            uri = uri.Replace("rtsp://", $"rtsp://{Uri.EscapeDataString(RtspUser)}:{Uri.EscapeDataString(RtspPass ?? "")}@");

        _audit.LogInformation("STREAM_OPEN | ip={Ip} | uriOhneCreds={Uri}",
            host!.Address, MaskCreds(uri));
        SelectedStreamUrl = uri;
        Status = L.F("Status_Stream", host.Address);
    }

    /// <summary>Öffnet den aktuell gewählten Stream im System-Default-Player.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenExternalStream))]
    private void OpenExternalStream()
    {
        if (SelectedStreamUrl is { } url)
            _preview.OpenExternal(url);
    }

    private bool CanOpenExternalStream() => !string.IsNullOrWhiteSpace(SelectedStreamUrl);

    // OnXChanged-Hooks (CommunityToolkit erzeugt die partiellen Methoden) -> Eingaben loggen.
    partial void OnCidrChanged(string value) => _audit?.LogInformation("INPUT cidr={Cidr}", value);
    partial void OnScanFullPortsChanged(bool value) => _audit?.LogInformation("INPUT fullPorts={Val}", value);
    partial void OnProbeRtspChanged(bool value) => _audit?.LogInformation("INPUT probeRtsp={Val}", value);

    // --- Host-Aktionen (vom Code-behind aufgerufen) ---

    /// <summary>Sendet ein Wake-on-LAN-Paket an die MAC des Hosts.</summary>
    public async Task WakeOnLanAsync(HostResult host)
    {
        _audit.LogInformation("WOL | ip={Ip} | mac={Mac}", host.Address, host.MacAddress);
        bool ok = await _wol.SendAsync(host.MacAddress, CancellationToken.None);
        Status = ok ? L.F("Status_WolSent", host.MacAddress) : L.T("Status_WolFailed");
    }

    /// <summary>Setzt eine Statusmeldung und protokolliert die Aktion.</summary>
    public void ReportAction(string message)
    {
        Status = message;
        _audit.LogInformation("ACTION | {Msg}", message);
    }

    // --- Export ---
    public string BuildCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("IP;Name;MAC;Hersteller;OS;Gerätetyp;TTL;Latenz_ms;Kamera;OffenePorts;mDNS;NetBIOS;Arbeitsgruppe;UPnP;SSH;HTTP");
        foreach (var h in Hosts)
        {
            string ports = string.Join(" ", h.OpenPorts.Select(p => $"{p.Port}/{p.Service}"));
            sb.AppendLine(string.Join(";",
                h.Address.ToString(), Csv(h.BestName), h.MacAddress ?? "", Csv(h.Vendor),
                Csv(h.OsGuess), Csv(h.DeviceType), h.Ttl?.ToString() ?? "",
                h.RoundtripMs.ToString(), h.IsCamera ? "ja" : "nein", Csv(ports),
                Csv(h.MdnsName), Csv(h.NetbiosName), Csv(h.NetbiosGroup),
                Csv(h.UpnpDeviceType), Csv(h.SshBanner), Csv(h.HttpServer)));
        }
        return sb.ToString();
    }

    public string BuildJson()
    {
        var data = Hosts.Select(h => new
        {
            ip = h.Address.ToString(),
            name = h.BestName,
            mac = h.MacAddress,
            vendor = h.Vendor,
            os = h.OsGuess,
            deviceType = h.DeviceType,
            ttl = h.Ttl,
            latencyMs = h.RoundtripMs,
            isCamera = h.IsCamera,
            rtsp = h.Camera?.RtspUri,
            ports = h.OpenPorts.Select(p => new { p.Port, p.Service }),
            mdnsName = h.MdnsName,
            mdnsServices = h.MdnsServices,
            netbiosName = h.NetbiosName,
            netbiosGroup = h.NetbiosGroup,
            upnpType = h.UpnpDeviceType,
            upnpServer = h.UpnpServer,
            sshBanner = h.SshBanner,
            httpServer = h.HttpServer
        });
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Csv(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace(';', ',').Replace("\n", " ").Replace("\r", "");

    private static string MaskCreds(string uri)
    {
        int at = uri.IndexOf('@');
        return at < 0 ? uri : "rtsp://***:***@" + uri[(at + 1)..];
    }
}
