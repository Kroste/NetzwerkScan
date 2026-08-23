using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NLog;
using NLog.LayoutRenderers;
using NLog.LayoutRenderers.Wrappers;

namespace NetScanner.Logging;

/// <summary>
/// Wrapper-Layout-Renderer <c>${masked:inner=...}</c>: entfernt Passwörter, Tokens und
/// Basic-Auth-Credentials aus jeder Logzeile, bevor sie geschrieben wird.
///
/// WARUM: NetScanner probiert beim optionalen Credential-Audit dokumentierte Werks-Logins
/// gegen Geräte im eigenen Netz. Ein Treffer ist ein <em>wirksames</em> Passwort für ein
/// reales Gerät — es darf nicht in einer Datei landen, die 14 Tage archiviert wird. Der
/// Renderer ist das Sicherheitsnetz für den Fall, dass an einer Aufrufstelle doch einmal
/// ein Secret in die Message rutscht.
/// </summary>
[LayoutRenderer("masked")]
public sealed class MaskingLayoutRenderer : WrapperLayoutRendererBase
{
    private const string Mask = "***";

    /// <summary>
    /// Registrierung über einen Modul-Initializer statt in <c>Program.Main</c>: der
    /// Testprozess hat kein Main. Kennt NLog das <c>${masked}</c> nicht, beendet die
    /// innere <c>}</c> den Renderer vorzeitig und die Message fehlt komplett im Log.
    /// </summary>
    [ModuleInitializer]
    internal static void Register()
        => LogManager.Setup().SetupExtensions(s =>
               s.RegisterLayoutRenderer<MaskingLayoutRenderer>("masked"));

    // key=wert / key: wert / key "wert" — deckt Passwort, Password, Pwd, Token, Secret,
    // ApiKey und Credential(s) ab. Der Wert endet am ersten Leerzeichen, Komma oder Ende.
    private static readonly Regex KeyValue = new(
        """(?ix)\b (pass(word|wort)? | pwd | token | secret | api[-_ ]?key | credentials?) \b \s* [:=] \s* (?<v>"[^"]*" | '[^']*' | \S+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // URLs mit eingebetteten Credentials: rtsp://user:geheim@10.0.0.5/stream
    private static readonly Regex UrlCredentials = new(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)(?<user>[^/:@\s]+):(?<pass>[^/@\s]*)@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // Authorization-Header (Basic/Digest/Bearer) inklusive Base64-Nutzlast.
    private static readonly Regex AuthHeader = new(
        @"(?i)\b(?<scheme>Basic|Bearer|Digest)\s+(?<v>[A-Za-z0-9+/=._\-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    protected override string Transform(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        try
        {
            // Alles vor dem Wert (Schlüssel, Trennzeichen, Leerraum) bleibt stehen,
            // damit im Log noch erkennbar ist, WELCHES Feld maskiert wurde.
            text = KeyValue.Replace(text, m =>
                string.Concat(m.Value.AsSpan(0, m.Groups["v"].Index - m.Index), Mask));
            text = UrlCredentials.Replace(text, m => $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:{Mask}@");
            text = AuthHeader.Replace(text, m => $"{m.Groups["scheme"].Value} {Mask}");
        }
        catch (RegexMatchTimeoutException)
        {
            // Im Zweifel lieber gar keine Message als eine ungeprüfte mit Secret darin.
            return "[Message wegen Masking-Timeout unterdrückt]";
        }

        return text;
    }
}
