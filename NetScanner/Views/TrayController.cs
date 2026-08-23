using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLog;

namespace NetScanner.Views;

/// <summary>
/// System-Tray-Integration nach Kroste-Standard.
/// - <b>Minimieren</b> → Fenster verschwindet in den Tray (<see cref="Window.Hide"/>).
/// - <b>Schließen</b> → App beendet regulär (kein <c>ShutdownMode</c>-Umbau).
/// - Klick aufs Tray-Icon oder Menü „Anzeigen" → Fenster kommt zurück.
/// - Menü „Beenden" → sauberer Desktop-Shutdown.
///
/// Vier Pflicht-Absicherungen (Skill: references/design.md → System-Tray):
/// - GC-Referenz: die App muss die Instanz in einem Feld halten.
/// - Restore-Guard: _restoreInProgress-Flag + Dispatcher.UIThread.Post.
/// - try/catch mit Fallback: headless-Server / kaputtes DBus → Standard-Minimieren.
/// - Linux hängt an Tmds.DBus.Protocol (transitive via Avalonia, kein neues Paket).
/// </summary>
public sealed class TrayController
{
    private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

    private readonly Application _app;
    private readonly Window _window;
    private TrayIcon? _tray;
    private bool _restoreInProgress;

    public TrayController(Application app, Window window)
    {
        _app = app;
        _window = window;
    }

    public void Install()
    {
        try
        {
            var iconUri = new Uri("avares://NetScanner/Assets/netscanner.png");
            var icon = AssetLoader.Exists(iconUri)
                ? new WindowIcon(new Bitmap(AssetLoader.Open(iconUri)))
                : null;

            _tray = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "NetScanner",
                IsVisible = true,
                Menu = BuildMenu(),
            };
            _tray.Clicked += (_, _) => Restore();

            TrayIcon.SetIcons(_app, new TrayIcons { _tray });
            _window.PropertyChanged += OnWindowPropertyChanged;

            _logger.Info("System-Tray installiert (Minimize → Tray).");
        }
        catch (Exception ex)
        {
            _tray = null;
            _logger.Warn(ex, "System-Tray nicht verfügbar — Fallback: Standard-Minimieren.");
        }
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("Anzeigen");
        showItem.Click += (_, _) => Restore();
        menu.Add(showItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Beenden");
        quitItem.Click += (_, _) => Quit();
        menu.Add(quitItem);

        return menu;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        if (_restoreInProgress) return;
        if (_window.WindowState != WindowState.Minimized) return;

        // Hide() schließt nicht — Prozess bleibt am Leben.
        _window.Hide();
    }

    private void Restore()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _restoreInProgress = true;
            try
            {
                _window.Show();
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            }
            finally
            {
                _restoreInProgress = false;
            }
        });
    }

    private void Quit()
    {
        if (_app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
