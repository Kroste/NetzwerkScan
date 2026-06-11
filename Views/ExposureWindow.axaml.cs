using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NetScanner.Models;
using NetScanner.Services;

namespace NetScanner.Views;

public partial class ExposureWindow : Window
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
        UpdateChrome(WindowState);
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
                m.DeviceName = host?.BestName ?? host?.DeviceType ?? "unbekanntes Gerät im LAN";
                m.TargetsCamera = host?.IsCamera ?? false;
            }

            if (result.Mappings.Count == 0)
            {
                StatusText.Text = "Router gefunden — keine aktiven Weiterleitungen.";
                EmptyHint.IsVisible = true;
            }
            else
            {
                int cams = result.Mappings.Count(m => m.TargetsCamera);
                StatusText.Text = cams > 0
                    ? $"{result.Mappings.Count} Weiterleitung(en) — davon {cams} auf eine Kamera!"
                    : $"{result.Mappings.Count} aktive Weiterleitung(en) gefunden.";
                MappingsHeader.IsVisible = true;
                MappingsList.ItemsSource = result.Mappings;
            }
        }
        catch (System.Exception)
        {
            LoadingBar.IsVisible = false;
            StatusText.Text = "Prüfung fehlgeschlagen.";
        }
    }

    // --- Custom-Chrome ---
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateChrome(WindowState);
    }

    private void UpdateChrome(WindowState state)
    {
        Padding = state == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (this.FindControl<Control>("MaxGlyph") is { } max)
            max.IsVisible = state != WindowState.Maximized;
        if (this.FindControl<Control>("RestoreGlyph") is { } restore)
            restore.IsVisible = state == WindowState.Maximized;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnShodanClick(object? sender, RoutedEventArgs e)
    {
        if (_publicIp is null) return;
        var top = GetTopLevel(this);
        if (top is not null)
            await top.Launcher.LaunchUriAsync(new System.Uri($"https://www.shodan.io/host/{_publicIp}"));
    }
}
