using System.Collections.ObjectModel;
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

    [ObservableProperty] private HostResult? _selectedHost;

    /// <summary>Wird an NativeVideoView.MediaUrl gebunden.</summary>
    [ObservableProperty] private string? _selectedStreamUrl;

    public MainViewModel(IScanOrchestrator orchestrator, ILogger<MainViewModel> log, ILoggerFactory factory)
    {
        _orchestrator = orchestrator;
        _log = log;
        _audit = factory.CreateLogger("UserInput");
        _cidr = IpRangeHelper.LocalSubnets().FirstOrDefault() ?? "192.168.10.0/24";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScanAsync()
    {
        // --- Audit: vollstaendige Eingabe protokollieren ---
        _audit.LogInformation(
            "SCAN_START | cidr={Cidr} | fullPorts={Full} | probeRtsp={Rtsp} | onvifMs={Onvif} | rtspUser={User}",
            Cidr, ScanFullPorts, ProbeRtsp, OnvifListenMs,
            string.IsNullOrEmpty(RtspUser) ? "(leer)" : RtspUser);   // Passwort NICHT loggen

        Hosts.Clear();
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
            int count = 0, cams = 0;
            await foreach (var host in _orchestrator.RunAsync(opt, _cts.Token))
            {
                Dispatcher.UIThread.Post(() => Hosts.Add(host));
                count++;
                if (host.IsCamera) cams++;
                Status = $"Läuft … {count} Host(s), {cams} Kamera(s)";
            }
            Status = $"Fertig: {count} Host(s), {cams} Kamera(s).";
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

    private static string MaskCreds(string uri)
    {
        int at = uri.IndexOf('@');
        return at < 0 ? uri : "rtsp://***:***@" + uri[(at + 1)..];
    }
}
