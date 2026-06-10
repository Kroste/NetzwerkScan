using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
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
/// Genau das macht das offizielle Paket intern auch.
///
/// libvlc selbst wird NICHT gebundelt, sondern via <see cref="VlcLocator"/> aus einer
/// vorhandenen VLC-Installation geladen. Fehlt sie, bleibt der Player schlicht inaktiv
/// (die UI zeigt dann statt des Videos einen Hinweis).
/// </summary>
public sealed class NativeVideoView : NativeControlHost
{
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
        if (_player is null) return;            // Control noch nicht realisiert / kein libvlc
        if (string.IsNullOrWhiteSpace(url)) { _player.Stop(); return; }
        Play(url);
    }

    private void Play(string url)
    {
        if (_player is null || VlcLocator.Shared is not { } vlc) return;
        using var media = new Media(vlc, new Uri(url));
        _player.Play(media);
    }
}
