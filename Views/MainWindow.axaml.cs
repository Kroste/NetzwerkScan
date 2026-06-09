using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NetScanner.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateChrome(WindowState);   // Initialzustand setzen
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

    // Ziehen & Doppelklick-Maximieren uebernimmt das OS via WindowDecorationProperties.ElementRole="TitleBar".

    // --- Fenster-Buttons ---
    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // --- About-Dialog ---
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        Log.Info("Info-Button geklickt → About-Dialog wird geoeffnet");
        try
        {
            await new AboutWindow().ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "About-Dialog konnte nicht geoeffnet werden");
        }
    }
}
