using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using LibVLCSharp.Shared;

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
/// Genau das macht das offizielle Paket intern auch.
/// </summary>
public sealed class NativeVideoView : NativeControlHost
{
    private static LibVLC? _sharedLibVlc;
    private MediaPlayer? _player;
    private IPlatformHandle? _handle;

    /// <summary>Setzt die abzuspielende URL (z. B. eine RTSP-Stream-URL).</summary>
    public static readonly StyledProperty<string?> MediaUrlProperty =
        AvaloniaProperty.Register<NativeVideoView, string?>(nameof(MediaUrl));

    public string? MediaUrl
    {
        get => GetValue(MediaUrlProperty);
        set => SetValue(MediaUrlProperty, value);
    }

    static NativeVideoView()
    {
        // libvlc-Native einmalig initialisieren. Auf Windows liefert das NuGet
        // VideoLAN.LibVLC.Windows die Binaries; auf Linux wird System-libvlc genutzt.
        Core.Initialize();
        MediaUrlProperty.Changed.AddClassHandler<NativeVideoView>((c, e) =>
            c.OnMediaUrlChanged(e.GetNewValue<string?>()));
    }

    private static LibVLC SharedLibVlc => _sharedLibVlc ??= new LibVLC(
        // Reconnect & geringe Latenz fuer Kamerastreams.
        "--network-caching=300", "--rtsp-tcp", "--no-audio");

    /// <summary>Gibt die gemeinsame LibVLC-Instanz frei. Beim App-Shutdown aufrufen,
    /// sonst halten die nativen libvlc-Threads den Prozess am Leben.</summary>
    public static void Shutdown()
    {
        try { _sharedLibVlc?.Dispose(); }
        catch { /* beim Beenden ignorieren */ }
        _sharedLibVlc = null;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // base erzeugt ein leeres natives Child-Window; dessen Handle nutzt LibVLC.
        _handle = base.CreateNativeControlCore(parent);
        _player = new MediaPlayer(SharedLibVlc);
        AttachHandle(_handle, _player);

        if (!string.IsNullOrWhiteSpace(MediaUrl))
            Play(MediaUrl!);
        return _handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _player = null;
        }
        finally
        {
            base.DestroyNativeControlCore(control);
        }
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

    private void OnMediaUrlChanged(string? url)
    {
        if (_player is null) return;            // Control noch nicht realisiert
        if (string.IsNullOrWhiteSpace(url)) { _player.Stop(); return; }
        Play(url);
    }

    private void Play(string url)
    {
        if (_player is null) return;
        using var media = new Media(SharedLibVlc, new Uri(url));
        _player.Play(media);
    }
}
