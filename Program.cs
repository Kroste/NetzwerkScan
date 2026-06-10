using Avalonia;
using NLog;

namespace NetScanner;

internal static class Program
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Wichtig: Initialisierung NICHT vor AppMain anfassen (Avalonia-Vorgabe).
    [STAThread]
    public static void Main(string[] args)
    {
        // Globale Sicherheitsnetze: nichts soll den Prozess ungeloggt beenden. Gerade
        // bei viel Netzwerk-I/O und nativem Interop (libvlc) ist das die wichtigste Lücke.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception,
                "Unbehandelte Ausnahme (terminating={Term})", e.IsTerminating);
            LogManager.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unbeobachtete Task-Ausnahme");
            e.SetObserved();   // markiert die Exception als behandelt
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Fataler Startfehler (z. B. Avalonia-Backend, fehlende Anzeige): loggen und
            // sichtbar abbrechen, statt ohne Spur zu verschwinden.
            Log.Fatal(ex, "Anwendung mit Ausnahme beendet");
            throw;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
