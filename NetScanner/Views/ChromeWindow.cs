using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NetScanner.Views;

/// <summary>
/// Basisklasse für ALLE Fenster: Custom-Chrome nach Avalonia-12-Konvention plus
/// einheitliches App-Icon.
///
/// Alle vier Chrome-Zeilen sind Pflicht, zwei Fallen stecken darin:
/// <list type="bullet">
/// <item><c>BorderOnly</c>, niemals <c>None</c> — None verliert die nativen
/// Resize-Griffe und den Fensterschatten.</item>
/// <item>Ohne <c>ExtendClientAreaToDecorationsHint</c> + <c>TitleBarHeightHint = -1</c>
/// liegt die OS-Caption-Hit-Test-Zone über der eigenen Titelleiste und schluckt
/// alle Klicks und Drag-Events — Buttons ohne Funktion, Fenster nicht verschiebbar.</item>
/// </list>
/// </summary>
public class ChromeWindow : Window
{
    private const string IconUri = "avares://NetScanner/Assets/netscanner.png";

    protected ChromeWindow()
    {
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        CanResize = true;

        try
        {
            Icon = new WindowIcon(new Bitmap(AssetLoader.Open(new Uri(IconUri))));
        }
        catch
        {
            // Ohne Icon ist die App voll funktionsfähig — ein fehlendes Asset darf
            // den Fensterbau nicht abbrechen.
        }
    }
}
