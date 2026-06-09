using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetScanner.Services;
using NetScanner.ViewModels;
using NetScanner.Views;
using NLog.Extensions.Logging;

namespace NetScanner;

public partial class App : Application
{
    /// <summary>Globaler DI-Container (einfacher Service-Locator fuer Views/ViewModels).</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = Services.GetRequiredService<ILogger<App>>();
            log.LogInformation("Anwendung gestartet (PID {Pid})", Environment.ProcessId);

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
            desktop.ShutdownRequested += (_, _) =>
            {
                log.LogInformation("Anwendung wird beendet");
                NLog.LogManager.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();

        sc.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddNLog();   // liest nlog.config neben der EXE
        });

        // Scan-Services
        sc.AddSingleton<INetworkScanner, NetworkScanner>();
        sc.AddSingleton<IPortScanner, PortScanner>();
        sc.AddSingleton<ICameraDiscovery, OnvifDiscovery>();
        sc.AddSingleton<RtspProbe>();
        sc.AddSingleton<IScanOrchestrator, ScanOrchestrator>();

        // ViewModels
        sc.AddTransient<MainViewModel>();

        return sc.BuildServiceProvider();
    }
}
