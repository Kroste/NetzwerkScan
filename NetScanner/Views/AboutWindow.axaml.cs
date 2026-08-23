using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NetScanner.Services;
using NetScanner.Localization;

namespace NetScanner.Views;

public partial class AboutWindow : ChromeWindow
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly UpdateService? _updates;
    private UpdateInfo? _pending;

    public AboutWindow()
    {
        InitializeComponent();

        if (!Design.IsDesignMode)
            _updates = App.Services.GetRequiredService<UpdateService>();

        VersionText.Text = L.F("About_VersionLine", AppVersion.Display);
        Title = L.F("About_Title", AppVersion.Display);

        Opened += (_, _) => WindowSizing.FitToScreen(this);
    }

    /// <summary>Zeigt ein bereits beim Start gefundenes Update sofort an.</summary>
    public void ShowPendingUpdate(UpdateInfo info)
    {
        _pending = info;
        UpdateStatus.Text = L.F("About_Available", info.Version, AppVersion.Display);
        InstallUpdateButton.IsVisible = info.CanSelfUpdate;
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_updates is null) return;

        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = L.T("About_Checking");
        try
        {
            var info = await _updates.CheckAsync();
            if (info is null)
            {
                UpdateStatus.Text = L.F("About_UpToDate", AppVersion.Display);
                InstallUpdateButton.IsVisible = false;
                return;
            }

            ShowPendingUpdate(info);
            if (!info.CanSelfUpdate)
            {
                UpdateStatus.Text += L.T("About_NoAsset");
            }
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagener Check ist kein App-Fehler (offline, Proxy, Rate-Limit).
            Log.Warn(ex, "Update-Pruefung im About-Dialog fehlgeschlagen");
            UpdateStatus.Text = L.T("About_CheckFailed");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_updates is null || _pending is null) return;

        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        UpdateProgress.Value = 0;
        UpdateStatus.Text = L.F("About_Downloading", _pending.Version);

        // Fortschritt kommt aus dem Download-Task: explizit auf den UI-Thread
        // dispatchen, sonst sehen die Bindings die Aenderung nicht zuverlaessig.
        var progress = new Progress<double>(p =>
            Dispatcher.UIThread.Post(() => UpdateProgress.Value = p));

        try
        {
            bool ok = await _updates.DownloadAndApplyAsync(_pending, progress);
            if (!ok)
            {
                UpdateStatus.Text = L.T("About_ApplyFailed");
                UpdateProgress.IsVisible = false;
                InstallUpdateButton.IsEnabled = true;
                CheckUpdateButton.IsEnabled = true;
                return;
            }

            // PFLICHT: das Austausch-Skript wartet auf das Prozessende. Ohne dieses
            // Beenden bliebe die Anzeige ewig bei 100 % stehen und nichts passiert.
            UpdateStatus.Text = L.T("About_Installing");
            UpdateService.TerminateForUpdate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update-Installation fehlgeschlagen");
            UpdateStatus.Text = L.T("About_ApplyFailed");
            UpdateProgress.IsVisible = false;
            InstallUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void OnCoffeeClick(object? sender, RoutedEventArgs e)
        => await OpenUrlAsync("https://buymeacoffee.com/kroste");

    private async void OnGitHubClick(object? sender, RoutedEventArgs e)
        => await OpenUrlAsync("https://github.com/Kroste/NetzwerkScan");

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Öffnet eine URL im Standardbrowser — plattformneutral via Avalonia-Launcher
    /// (Windows: ShellExecute, Linux: xdg-open, macOS: open).</summary>
    private async Task OpenUrlAsync(string url)
    {
        try
        {
            if (GetTopLevel(this) is { } top)
                await top.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "URL konnte nicht geoeffnet werden: {Url}", url);
        }
    }
}
