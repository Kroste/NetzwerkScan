using Avalonia.Interactivity;

namespace NetScanner.Views;

public partial class AboutWindow : ChromeWindow
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = $"Version {AppVersion.Display} · .NET 10 · Avalonia 12";
        Title = $"Über NetScanner · v{AppVersion.Display}";

        Opened += (_, _) => WindowSizing.FitToScreen(this);
    }

    private void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        // Wird im Update-Slice mit dem UpdateService verdrahtet.
        UpdateStatus.Text = "Update-Prüfung ist noch nicht verdrahtet.";
    }

    private void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
    {
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
            Log.Error(ex, "URL konnte nicht geöffnet werden: {Url}", url);
        }
    }
}
