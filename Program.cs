using Avalonia;

namespace NetScanner;

internal static class Program
{
    // Wichtig: Initialisierung NICHT vor AppMain anfassen (Avalonia-Vorgabe).
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
