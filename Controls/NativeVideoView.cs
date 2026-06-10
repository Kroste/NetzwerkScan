using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using NetScanner.Services;

namespace NetScanner.Controls;

/// <summary>
/// Bettet einen LibVLC-MediaPlayer in Avalonia 12 ein, OHNE das offizielle
/// LibVLCSharp.Avalonia-Paket (das nur Avalonia 11 unterstuetzt).
///
/// Funktionsweise: NativeControlHost erzeugt ein echtes natives Kind-Fenster.
/// Dessen Handle reichen wir je nach Plattform an den MediaPlayer weiter:
///   Windows -> MediaPlayer.Hwnd       (HWND)
///   Linux   -> MediaPlayer.XWindow    (X11 XID; unter Wayland via XWayland)
///   macOS   -> MediaPlayer.NsObject   (NSView)
///
/// libvlc wird NICHT gebundelt, sondern via <see cref="VlcLocator"/> aus einer vorhandenen
/// VLC-Installation geladen. Der Wiedergabe-Status (Verbinden/Fehler/Timeout) wird ueber
/// <see cref="PlaybackInfo"/> nach aussen gemeldet, damit die UI bei Problemen kein
/// stummes schwarzes Bild zeigt.
/// </summary>
public sealed class NativeVideoView : NativeControlHost
{
    private MediaPlayer? _player;
    private IPlatformHandle? _handle;
    private DispatcherTimer? _watchdog;

    /// <summary>Setzt die abzuspielende URL (z. B. eine RTSP-Stream-URL).</summary>
    public static readonly StyledProperty<string?> MediaUrlProperty =
        AvaloniaProperty.Register<NativeVideoView, string?>(nameof(MediaUrl));

    public string? MediaUrl
    {
        get => GetValue(MediaUrlProperty);
        set => SetValue(MediaUrlProperty, value);
    }

    /// <summary>Wiedergabe-Status oder null, wenn das Video laeuft. Die View blendet
    /// daraus ein Overlay ein (Verbinden / Fehler / Zeitueberschreitung).</summary>
    public static readonly StyledProperty<string?> PlaybackInfoProperty =
        AvaloniaProperty.Register<NativeVideoView, string?>(nameof(PlaybackInfo));

    public string? PlaybackInfo
    {
        get => GetValue(PlaybackInfoProperty);
        private set => SetValue(PlaybackInfoProperty, value);
    }

    static NativeVideoView()
    {
        // libvlc aus vorhandener Installation initialisieren (idempotent, fehlertolerant).
        VlcLocator.EnsureInitialized();
        MediaUrlProperty.Changed.AddClassHandler<NativeVideoView>((c, e) =>
            c.OnMediaUrlChanged(e.GetNewValue<string?>()));
    }

    /// <summary>Gibt die gemeinsame LibVLC-Instanz frei. Beim App-Shutdown aufrufen.</summary>
    public static void Shutdown() => VlcLocator.Shutdown();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // base erzeugt ein leeres natives Child-Window; dessen Handle nutzt LibVLC.
        _handle = base.CreateNativeControlCore(parent);

        if (VlcLocator.Shared is { } vlc)   // nur wenn libvlc verfuegbar
        {
            _player = new MediaPlayer(vlc);
            WireEvents(_player);
            AttachHandle(_handle, _player);
            if (!string.IsNullOrWhiteSpace(MediaUrl))
                Play(MediaUrl!);
        }
        return _handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            StopWatchdog();
            _player?.Stop();
            _player?.Dispose();
            _player = null;
        }
        finally
        {
            base.DestroyNativeControlCore(control);
        }
    }

    private void WireEvents(MediaPlayer p)
    {
        // libvlc feuert diese Events auf eigenen Threads -> UI-Updates ueber den Dispatcher.
        p.Playing          += (_, _) => Ui(() => { StopWatchdog(); PlaybackInfo = null; });
        p.EncounteredError += (_, _) => Ui(() => { StopWatchdog();
            PlaybackInfo = "Stream nicht erreichbar oder Zugangsdaten falsch."; });
        p.EndReached       += (_, _) => Ui(() => { StopWatchdog(); PlaybackInfo = "Stream beendet."; });
        p.Buffering        += (_, e) => Ui(() =>
        {
            if (e.Cache < 100f) PlaybackInfo = $"Verbinde … {e.Cache:0} %";
        });
    }

    private void OnMediaUrlChanged(string? url)
    {
        if (_player is null) return;            // Control noch nicht realisiert / kein libvlc
        if (string.IsNullOrWhiteSpace(url))
        {
            StopWatchdog();
            _player.Stop();
            PlaybackInfo = null;
            return;
        }
        Play(url);
    }

    private void Play(string url)
    {
        if (_player is null || VlcLocator.Shared is not { } vlc) return;
        PlaybackInfo = "Verbinde …";
        StartWatchdog();
        using var media = new Media(vlc, new Uri(url));
        _player.Play(media);
    }

    // --- Watchdog: meldet einen haengenden Connect (es kommt kein Playing-/Error-Event). ---
    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _watchdog.Tick += (_, _) =>
        {
            StopWatchdog();
            PlaybackInfo = "Zeitüberschreitung – Stream nicht erreichbar.";
        };
        _watchdog.Start();
    }

    private void StopWatchdog()
    {
        _watchdog?.Stop();
        _watchdog = null;
    }

    private static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private static void AttachHandle(IPlatformHandle handle, MediaPlayer player)
    {
        // HandleDescriptor unterscheidet die Plattform-Handle-Typen.
        switch (handle.HandleDescriptor)
        {
            case "HWND":
                player.Hwnd = handle.Handle;
                break;
            case "XID":
                player.XWindow = (uint)handle.Handle.ToInt64();
                break;
            case "NSView":
                player.NsObject = handle.Handle;
                break;
            default:
                throw new PlatformNotSupportedException(
                    $"Unbekannter Handle-Typ: {handle.HandleDescriptor}");
        }
    }
}
