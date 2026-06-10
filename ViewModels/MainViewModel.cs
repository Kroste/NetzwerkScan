using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NetScanner.Models;
using NetScanner.Services;

namespace NetScanner.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IScanOrchestrator _orchestrator;
    private readonly WolSender _wol;
    private readonly ILogger<MainViewModel> _log;
    private readonly ILogger _audit;            // Logger-Name "UserInput" -> Audit-Datei
    private CancellationTokenSource? _cts;

    public ObservableCollection<HostResult> Hosts { get; } = [];

    [ObservableProperty] private string _cidr;
    [ObservableProperty] private bool _scanFullPorts;
    [ObservableProperty] private bool _probeRtsp = true;
    [ObservableProperty] private string? _rtspUser;
    [ObservableProperty] private string? _rtspPass;
    [ObservableProperty] private int _onvifListenMs = 3000;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _status = "Bereit.";
    [ObservableProperty] private int _hostCount;
    [ObservableProperty] private int _cameraCount;

    [ObservableProperty] private HostResult? _selectedHost;

    /// <summary>Wird an NativeVideoView.MediaUrl gebunden.</summary>
    [ObservableProperty] private string? _selectedStreamUrl;

    /// <summary>True, wenn libvlc (aus einer vorhandenen VLC-Installation) bereitsteht.</summary>
    public bool IsPreviewAvailable => VlcLocator.IsAvailable;

    /// <summary>Eingebettete Vorschau zeigen: Stream gewaehlt UND libvlc verfuegbar.</summary>
    public bool ShowVideoPreview =>
        IsPreviewAvailable && !string.IsNullOrWhiteSpace(SelectedStreamUrl);

    /// <summary>Hinweis "VLC installieren" zeigen: Stream gewaehlt, aber kein libvlc da.</summary>
    public bool ShowVlcMissingHint =>
        !IsPreviewAvailable && !string.IsNullOrWhiteSpace(SelectedStreamUrl);

    /// <summary>Statusabhaengiger Hinweis fuer den Fall ohne (passende) VLC-Installation.</summary>
    public string VlcHintText => VlcLocator.Status switch
    {
        VlcStatus.WrongArchitecture =>
            "Es wurde eine 32-bit-VLC gefunden. NetScanner braucht die 64-bit-Version des VLC media player.",
        VlcStatus.InitFailed =>
            "VLC wurde gefunden, aber libvlc ließ sich nicht laden. Vermutlich ist die Installation beschädigt — VLC neu installieren.",
        _ =>
            "NetScanner bringt libvlc nicht mehr mit. Installiere den VLC media player (64-bit), dann läuft der Stream direkt hier. Ohne VLC die Stream-URL kopieren und extern öffnen."
    };

    // Beide abgeleiteten Flags neu auswerten, sobald sich die Stream-URL aendert.
    partial void OnSelectedStreamUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowVideoPreview));
        OnPropertyChanged(nameof(ShowVlcMissingHint));
    }

    public MainViewModel(IScanOrchestrator orchestrator, WolSender wol, ILogger<MainViewModel> log, ILoggerFactory factory)
    {
        _orchestrator = orchestrator;
        _wol = wol;
        _log = log;
        _audit = factory.CreateLogger("UserInput");
        _cidr = IpRangeHelper.LocalSubnets().FirstOrDefault() ?? "192.168.10.0/24";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScanAsync()
    {
        // Eingabe vorab pruefen -> klare Meldung statt generischem Fehler tief im Scan.
        if (!IpRangeHelper.IsValidCidr(Cidr))
        {
            Status = "Ungültige CIDR-Angabe — erwartet z. B. 192.168.10.0/24.";
            _audit.LogInformation("SCAN_REJECT | ungueltige CIDR: {Cidr}", Cidr);
            return;
        }

        // --- Audit: vollstaendige Eingabe protokollieren ---
        _audit.LogInformation(
            "SCAN_START | cidr={Cidr} | fullPorts={Full} | probeRtsp={Rtsp} | onvifMs={Onvif} | rtspUser={User}",
            Cidr, ScanFullPorts, ProbeRtsp, OnvifListenMs,
            string.IsNullOrEmpty(RtspUser) ? "(leer)" : RtspUser);   // Passwort NICHT loggen

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
                    Hosts.Add(host);
                    HostCount = Hosts.Count;
                    if (host.IsCamera) CameraCount++;
                    Status = $"Läuft … {HostCount} Host(s), {CameraCount} Kamera(s)";
                });
            }
            Status = $"Fertig: {HostCount} Host(s), {CameraCount} Kamera(s).";
        }
        catch (OperationCanceledException)
        {
            Status = $"Abgebrochen. {Hosts.Count} Host(s) gefunden.";
            _audit.LogInformation("SCAN_CANCEL | gefunden={Count}", Hosts.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scan fehlgeschlagen");
            Status = $"Fehler: {ex.Message}";
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
        if (string.IsNullOrWhiteSpace(uri)) { Status = "Keine RTSP-URL fuer diesen Host."; return; }

        // Falls beim Scan keine Credentials gesetzt waren, jetzt einsetzen.
        if (!string.IsNullOrEmpty(RtspUser) && !uri.Contains('@'))
            uri = uri.Replace("rtsp://", $"rtsp://{Uri.EscapeDataString(RtspUser)}:{Uri.EscapeDataString(RtspPass ?? "")}@");

        _audit.LogInformation("STREAM_OPEN | ip={Ip} | uriOhneCreds={Uri}",
            host!.Address, MaskCreds(uri));
        SelectedStreamUrl = uri;
        Status = $"Stream: {host.Address}";
    }

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
        Status = ok ? $"Wake-on-LAN gesendet an {host.MacAddress}" : "WoL fehlgeschlagen (MAC unbekannt?)";
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
        sb.AppendLine("IP;Name;MAC;Hersteller;OS;Geraetetyp;TTL;Latenz_ms;Kamera;OffenePorts;mDNS;NetBIOS;Arbeitsgruppe;UPnP;SSH;HTTP");
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
