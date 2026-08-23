using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NetScanner.Models;
using NetScanner.Services;
using NetScanner.Localization;

namespace NetScanner.Views;

public partial class ExposureWindow : ChromeWindow
{
    private readonly UpnpExposureProbe _probe;
    private readonly IReadOnlyList<HostResult> _hosts;
    private string? _publicIp;

    // Parameterloser Konstruktor nur fuer den XAML-Designer.
    public ExposureWindow() : this(
        new UpnpExposureProbe(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpnpExposureProbe>.Instance),
        [])
    { }

    public ExposureWindow(UpnpExposureProbe probe, IReadOnlyList<HostResult> hosts)
    {
        InitializeComponent();
        _probe = probe;
        _hosts = hosts;
        Opened += async (_, _) => { WindowSizing.FitToScreen(this); await RunAsync(); };
    }

    private async System.Threading.Tasks.Task RunAsync()
    {
        try
        {
            var result = await _probe.ProbeAsync(2500, default);
            LoadingBar.IsVisible = false;

            // Öffentliche IP.
            _publicIp = result.ExternalIp;
            if (_publicIp is not null)
            {
                PublicIpText.Text = _publicIp;
                PublicIpBox.IsVisible = true;
            }

            if (!result.IgdFound)
            {
                StatusText.Text = "Kein UPnP-Router erreichbar.";
                NoIgdHint.IsVisible = true;
                return;
            }

            // Mappings mit den Scan-Ergebnissen anreichern (Gerätename, Kamera-Flag).
            foreach (var m in result.Mappings)
            {
                var host = _hosts.FirstOrDefault(h => h.Address.ToString() == m.InternalClient);
                m.DeviceName = host?.BestName ?? host?.DeviceType ?? L.T("Exp_UnknownDevice");
                m.TargetsCamera = host?.IsCamera ?? false;
            }

            if (result.Mappings.Count == 0)
            {
                StatusText.Text = L.T("Exp_NoForwardings");
                EmptyHint.IsVisible = true;
            }
            else
            {
                int cams = result.Mappings.Count(m => m.TargetsCamera);
                StatusText.Text = cams > 0
                    ? L.F("Exp_WithCameras", result.Mappings.Count, cams)
                    : L.F("Exp_FoundCount", result.Mappings.Count);
                MappingsHeader.IsVisible = true;
                MappingsList.ItemsSource = result.Mappings;
            }
        }
        catch (System.Exception)
        {
            LoadingBar.IsVisible = false;
            StatusText.Text = L.T("Exp_Failed");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnShodanClick(object? sender, RoutedEventArgs e)
    {
        if (_publicIp is null) return;
        var top = GetTopLevel(this);
        if (top is not null)
            await top.Launcher.LaunchUriAsync(new System.Uri($"https://www.shodan.io/host/{_publicIp}"));
    }
}
