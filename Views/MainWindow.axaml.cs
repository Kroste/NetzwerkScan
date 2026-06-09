using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NetScanner.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);
}
