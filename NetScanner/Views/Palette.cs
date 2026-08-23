using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace NetScanner.Views;

/// <summary>
/// Zugriff auf die in <c>App.axaml</c> definierte Palette aus Code-Behind.
///
/// WARUM: Farbwerte im Code-Behind zu duplizieren ist die zuverlässigste Art,
/// dass ein Paletten-Refactoring die Hälfte der UI stehen lässt — genau das war
/// vor dem Kroste-Refresh der Fall (die Netzwerkkarte malte noch die alte
/// Teal-Palette, während das XAML längst umgestellt war). Alle Farben kommen
/// deshalb aus genau einer Quelle.
/// </summary>
internal static class Palette
{
    /// <summary>Löst einen Brush-Key aus den Application-Resources auf.</summary>
    /// <remarks>
    /// Fällt auf Magenta zurück, wenn der Key fehlt. Ein falscher Key erzeugt in
    /// Avalonia keinen Compile-Fehler und würde sonst nur „unsichtbar" rendern —
    /// Magenta macht den Tippfehler beim ersten Blick sichtbar.
    /// </remarks>
    public static IBrush Brush(string key)
    {
        if (Application.Current?.TryGetResource(key, ThemeVariant.Dark, out var value) == true
            && value is IBrush brush)
        {
            return brush;
        }

        return Brushes.Magenta;
    }

    /// <summary>Farbe hinter einem Brush-Key (für Border/Background-Tripel).</summary>
    public static Color Color(string key)
        => Brush(key) is ISolidColorBrush s ? s.Color : Colors.Magenta;
}
