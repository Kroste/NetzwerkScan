using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NetScanner.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Custom-Chrome: bei maximiertem Fenster oben etwas Luft lassen, damit der
        // Inhalt nicht am Bildschirmrand klebt, und das Max/Restore-Glyph umschalten.
        this.GetObservable(WindowStateProperty).Subscribe(state =>
        {
            Padding = state == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            if (this.FindControl<Control>("MaxGlyph") is { } max)
                max.IsVisible = state != WindowState.Maximized;
            if (this.FindControl<Control>("RestoreGlyph") is { } restore)
                restore.IsVisible = state == WindowState.Maximized;
        });
    }

    // --- Titelleiste: ziehen & per Doppelklick maximieren ---
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    // --- Fenster-Buttons ---
    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // --- About-Dialog ---
    private async void OnAboutClick(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);
}
