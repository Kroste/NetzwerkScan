using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetScanner.Config;
using NetScanner.Localization;
using NetScanner.Services;
using NetScanner.ViewModels;
using NetScanner.Views;
using NLog.Extensions.Logging;

namespace NetScanner;

public partial class App : Application
{
    /// <summary>Globaler DI-Container (einfacher Service-Locator fuer Views/ViewModels).</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    // PFLICHT-Feld: haelt die GC-Referenz auf den Tray. Ohne sie sammelt der GC den
    // Controller ein und das Tray-Icon verschwindet nach einigen Minuten wieder.
    private TrayController? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        // Sprache setzen, BEVOR das erste Fenster gebaut wird — sonst flackert die UI
        // beim Start kurz in der Systemsprache.
        var settings = Services.GetRequiredService<AppSettingsService>();
        if (settings.Current.UiCulture is { Length: > 0 } iso)
            LocalizationService.Instance.SetCulture(iso);

        // libvlc-Verfuegbarkeit frueh klaeren (laedt aus vorhandener VLC-Installation),
        // damit das ViewModel/die UI sofort wissen, ob die Vorschau angeboten werden kann.
        NetScanner.Services.VlcLocator.EnsureInitialized();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = Services.GetRequiredService<ILogger<App>>();
            log.LogInformation("Anwendung gestartet (PID {Pid})", Environment.ProcessId);
            log.LogInformation("Kamera-Vorschau (libvlc) verfuegbar: {Avail}{From}",
                NetScanner.Services.VlcLocator.IsAvailable,
                NetScanner.Services.VlcLocator.LoadedFrom is { } p ? $" (aus {p})" : "");

            // DataContext setzt das Fenster selbst im Konstruktor (vor dem Baum-Aufbau),
            // damit Bindings wie $parent[Window].DataContext beim Start nicht gegen null laufen.
            var main = new MainWindow();
            desktop.MainWindow = main;

            // Minimieren legt das Fenster in den Tray, Schliessen beendet regulaer.
            _tray = new TrayController(this, main);
            _tray.Install();
            desktop.ShutdownRequested += (_, _) =>
            {
                log.LogInformation("Anwendung wird beendet");
                NetScanner.Services.VlcLocator.Shutdown();   // native libvlc-Threads freigeben
                NLog.LogManager.Shutdown();                  // Logpuffer leeren
                // Sicherheitsnetz: libvlc kann den Prozess sonst offen halten.
                Environment.Exit(0);
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
        sc.AddSingleton<BannerGrabber>();
        sc.AddSingleton<MdnsDiscovery>();
        sc.AddSingleton<SsdpDiscovery>();
        sc.AddSingleton<NetBiosProbe>();
        sc.AddSingleton<CredentialAuditor>();
        sc.AddSingleton<PwnedPasswordChecker>();
        sc.AddSingleton<UpnpExposureProbe>();
        sc.AddSingleton<WolSender>();
        sc.AddSingleton<TracerouteService>();
        sc.AddSingleton<IScanOrchestrator, ScanOrchestrator>();
        sc.AddSingleton<UpdateService>();
        sc.AddSingleton<AppSettingsService>();

        // ViewModels
        sc.AddTransient<MainViewModel>();
        sc.AddTransient<SettingsViewModel>();

        return sc.BuildServiceProvider();
    }
}
