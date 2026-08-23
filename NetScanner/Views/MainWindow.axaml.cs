using System.Diagnostics;
using System.Linq;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Threading;
using NetScanner.Models;
using NetScanner.Services;
using NetScanner.ViewModels;

namespace NetScanner.Views;

public partial class MainWindow : ChromeWindow
{
    public MainWindow()
    {
        // DataContext VOR InitializeComponent: sonst werten statische Bindings im Baum
        // (z. B. $parent[Window].DataContext.OpenStreamCommand im Detail-Panel) beim
        // Aufbau gegen einen noch leeren DataContext aus und loggen einen Start-Fehler.
        if (!Design.IsDesignMode)
            DataContext = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();

        // Update-Check bewusst NICHT blockierend beim Start: offline oder ein
        // zaeher Proxy darf den Fensterstart nicht verzoegern.
        Opened += (_, _) => _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Sucht im Hintergrund nach einem Update und blendet bei einem Fund das
    /// Update-Abzeichen in der Titelleiste ein. Fehler bleiben still im Log —
    /// ein fehlgeschlagener Check ist kein App-Fehler.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (Design.IsDesignMode) return;

        try
        {
            var updates = App.Services.GetRequiredService<UpdateService>();
            _pendingUpdate = await updates.CheckAsync();
            if (_pendingUpdate is null) return;

            await Dispatcher.UIThread.InvokeAsync(() => UpdateBadge.IsVisible = true);
            Log.Info("Update {Version} verfuegbar (installiert {Current})",
                _pendingUpdate.Version, AppVersion.Display);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Update-Check beim Start fehlgeschlagen");
        }
    }

    // --- About-Dialog ---
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private UpdateInfo? _pendingUpdate;

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        Log.Info("Info-Button geklickt → About-Dialog wird geoeffnet");
        try
        {
            var about = new AboutWindow();
            if (_pendingUpdate is { } info)
                about.ShowPendingUpdate(info);
            await about.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "About-Dialog konnte nicht geoeffnet werden");
        }
    }

    // --- Passwort-Leak-Check ---
    private async void OnPwCheckClick(object? sender, RoutedEventArgs e)
    {
        Log.Info("Passwort-Check-Button geklickt → Dialog wird geoeffnet");
        try
        {
            var checker = App.Services.GetRequiredService<Services.PwnedPasswordChecker>();
            await new PasswordCheckWindow(checker).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Passwort-Check-Dialog konnte nicht geoeffnet werden");
        }
    }

    // --- Expositions-Prüfung (UPnP-IGD) ---
    private async void OnExposureClick(object? sender, RoutedEventArgs e)
    {
        Log.Info("Expositions-Button geklickt → Dialog wird geoeffnet");
        try
        {
            var probe = App.Services.GetRequiredService<Services.UpnpExposureProbe>();
            System.Collections.Generic.IReadOnlyList<Models.HostResult> hosts =
                DataContext is MainViewModel vm
                    ? vm.Hosts.ToList()
                    : System.Array.Empty<Models.HostResult>();
            await new ExposureWindow(probe, hosts).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Expositions-Dialog konnte nicht geoeffnet werden");
        }
    }

    // --- Netzwerkkarte ---
    private void OnMapClick(object? sender, RoutedEventArgs e)
    {
        Log.Info("Netzwerkkarte-Button geklickt");
        try
        {
            if (DataContext is MainViewModel vm)
                new NetworkMapWindow(vm).Show(this);   // nicht-modal: parallel zum Hauptfenster nutzbar
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Netzwerkkarte konnte nicht geoeffnet werden");
        }
    }

    // === Host-Aktionen (Kontextmenue + Detail-Panel teilen dieselben Handler) ===
    private static HostResult? HostOf(object? sender) => (sender as Control)?.DataContext as HostResult;
    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>
    /// Rechtsklick auf eine Host-Karte: ContextMenu selbst oeffnen. In einer ListBox faengt
    /// das ListBoxItem den Rechtsklick sonst ab, sodass das Auto-Verhalten nicht greift.
    /// </summary>
    private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right
            && sender is Control c && c.ContextMenu is { } menu)
        {
            menu.Open(c);
            e.Handled = true;
        }
    }

    private void OnHostWeb(object? sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is { WebUrl: { } url })
        {
            GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url));
            Vm?.ReportAction($"Browser geöffnet: {url}");
        }
    }

    private void OnHostSsh(object? sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is not { } h) return;
        var ip = h.Address.ToString();
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start ssh {ip}") { UseShellExecute = false });
            else
            {
                // Linux/macOS: Terminal-Start ist distro-abhaengig -> Befehl in die Zwischenablage.
                GetTopLevel(this)?.Clipboard?.SetTextAsync($"ssh {ip}");
                Vm?.ReportAction($"SSH-Befehl kopiert: ssh {ip}");
                return;
            }
            Vm?.ReportAction($"SSH zu {ip} gestartet");
        }
        catch (Exception ex) { Log.Error(ex, "SSH-Start fehlgeschlagen"); Vm?.ReportAction("SSH-Start fehlgeschlagen"); }
    }

    private void OnHostRdp(object? sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is not { } h) return;
        var ip = h.Address.ToString();
        if (!OperatingSystem.IsWindows()) { Vm?.ReportAction("RDP-Client nur unter Windows verfügbar"); return; }
        try { Process.Start("mstsc.exe", $"/v:{ip}"); Vm?.ReportAction($"RDP zu {ip} gestartet"); }
        catch (Exception ex) { Log.Error(ex, "RDP-Start fehlgeschlagen"); }
    }

    private void OnHostSmb(object? sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is not { } h) return;
        var ip = h.Address.ToString();
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $@"\\{ip}") { UseShellExecute = true });
            else
                GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri($"smb://{ip}/"));
            Vm?.ReportAction($"Dateifreigabe geöffnet: {ip}");
        }
        catch (Exception ex) { Log.Error(ex, "SMB-Start fehlgeschlagen"); }
    }

    private void OnHostCopyIp(object? sender, RoutedEventArgs e) => Copy(HostOf(sender)?.Address.ToString());
    private void OnHostCopyMac(object? sender, RoutedEventArgs e) => Copy(HostOf(sender)?.MacAddress);
    private void OnHostCopyName(object? sender, RoutedEventArgs e) => Copy(HostOf(sender)?.BestName);

    private void Copy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        Vm?.ReportAction($"Kopiert: {text}");
    }

    /// <summary>Oeffnet die VLC-Download-Seite (fuer die eingebettete Kamera-Vorschau).</summary>
    private void OnDownloadVlc(object? sender, RoutedEventArgs e) =>
        GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri("https://www.videolan.org/vlc/"));

    private async void OnHostWol(object? sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is { } h && Vm is { } vm)
            await vm.WakeOnLanAsync(h);
    }

    // === Export ===
    private async void OnExportCsv(object? sender, RoutedEventArgs e) => await ExportAsync("csv");
    private async void OnExportJson(object? sender, RoutedEventArgs e) => await ExportAsync("json");

    private async Task ExportAsync(string fmt)
    {
        if (Vm is not { } vm || GetTopLevel(this) is not { } top) return;
        if (vm.Hosts.Count == 0) { vm.ReportAction("Nichts zu exportieren — erst scannen."); return; }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Scan-Ergebnis exportieren",
            SuggestedFileName = $"netscan_{DateTime.Now:yyyyMMdd_HHmm}.{fmt}",
            DefaultExtension = fmt,
            FileTypeChoices = [new FilePickerFileType(fmt.ToUpperInvariant()) { Patterns = [$"*.{fmt}"] }]
        });
        if (file is null) return;

        try
        {
            string content = fmt == "csv" ? vm.BuildCsv() : vm.BuildJson();
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content);
            vm.ReportAction($"Exportiert: {file.Name}");
        }
        catch (Exception ex) { Log.Error(ex, "Export fehlgeschlagen"); vm.ReportAction("Export fehlgeschlagen"); }
    }
}
