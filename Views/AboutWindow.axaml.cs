using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NetScanner.Views;

public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();

    private async void OnCoffeeClick(object? sender, RoutedEventArgs e)
        => await OpenUrlAsync("https://buymeacoffee.com/kroste");

    private async void OnGitHubClick(object? sender, RoutedEventArgs e)
        => await OpenUrlAsync("https://github.com/Kroste");

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Oeffnet eine URL im Standardbrowser — plattformneutral via Avalonia-Launcher
    /// (Windows: ShellExecute, Linux: xdg-open, macOS: open).</summary>
    private async Task OpenUrlAsync(string url)
    {
        var top = GetTopLevel(this);
        if (top is not null)
            await top.Launcher.LaunchUriAsync(new Uri(url));
    }
}
