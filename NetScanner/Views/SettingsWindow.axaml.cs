using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NetScanner.ViewModels;

namespace NetScanner.Views;

public partial class SettingsWindow : ChromeWindow
{
    public SettingsWindow()
    {
        // DataContext vor InitializeComponent, damit die Bindings beim Baum-Aufbau
        // nicht gegen null laufen.
        if (!Design.IsDesignMode)
            DataContext = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        Opened += (_, _) => WindowSizing.FitToScreen(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
