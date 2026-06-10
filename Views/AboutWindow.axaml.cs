using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NetScanner.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        UpdateChrome(WindowState);
    }

    // Avalonia 12: kein Subscribe(Action<T>) ohne System.Reactive -> Property-Override nutzen.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateChrome(WindowState);
    }

    /// <summary>Bei maximiertem Fenster oben Luft lassen (Custom-Chrome) und Max/Restore-Glyph umschalten.</summary>
    private void UpdateChrome(WindowState state)
    {
        Padding = state == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (this.FindControl<Control>("MaxGlyph") is { } max)
            max.IsVisible = state != WindowState.Maximized;
        if (this.FindControl<Control>("RestoreGlyph") is { } restore)
            restore.IsVisible = state == WindowState.Maximized;
    }

    // --- Fenster-Buttons ---
    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // --- Inhalt ---
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
