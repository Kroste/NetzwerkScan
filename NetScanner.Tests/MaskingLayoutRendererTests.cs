using System.Runtime.CompilerServices;
using FluentAssertions;
using NLog;
using NLog.Layouts;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// Der masked-Renderer ist die letzte Verteidigungslinie gegen Secrets im Log.
/// Getestet wird über die echte NLog-Layout-Pipeline und nicht gegen die
/// Transform-Methode direkt — nur so fällt auf, wenn die Registrierung per
/// ModuleInitializer nicht greift (dann bleibt die Message komplett leer).
/// </summary>
public class MaskingLayoutRendererTests
{
    private static string Render(string message)
    {
        // Den ModuleInitializer des Hauptprojekts erzwingen. Ein blosses typeof()
        // genuegt NICHT: die Laufzeit laedt dabei nur das Typ-Token und ruft den
        // Modul-Konstruktor noch nicht. Ohne diesen Aufruf kennt NLog "masked" beim
        // Parsen des Layouts nicht, die innere Klammer beendet den Renderer vorzeitig
        // und übrig bleibt ein einzelnes "}" statt der Message.
        RuntimeHelpers.RunModuleConstructor(
            typeof(NetScanner.Logging.MaskingLayoutRenderer).Module.ModuleHandle);

        var layout = Layout.FromString("${masked:inner=${message}}");
        var evt = new LogEventInfo(LogLevel.Info, "Test", message);
        return layout.Render(evt);
    }

    [Fact]
    public void Registrierung_greift_und_Message_bleibt_erhalten()
        => Render("Scan auf 10.0.0.0/24 gestartet")
            .Should().Be("Scan auf 10.0.0.0/24 gestartet");

    [Theory]
    [InlineData("Passwort=geheim123", "Passwort=***")]
    [InlineData("password: hunter2", "password: ***")]
    [InlineData("Pwd = s3cr3t", "Pwd = ***")]
    [InlineData("token=abc.def.ghi", "token=***")]
    [InlineData("ApiKey: sk-livedeadbeef", "ApiKey: ***")]
    [InlineData("api-key=xyz", "api-key=***")]
    [InlineData("secret=\"mit Leerzeichen\"", "secret=***")]
    [InlineData("credentials=admin/12345", "credentials=***")]
    public void Schluessel_Wert_Paare_werden_maskiert(string input, string expected)
        => Render(input).Should().Be(expected);

    [Fact]
    public void Credentials_in_URLs_werden_maskiert_Benutzer_bleibt()
        => Render("Stream rtsp://admin:12345@10.0.0.5:554/live")
            .Should().Be("Stream rtsp://admin:***@10.0.0.5:554/live");

    [Theory]
    [InlineData("Authorization: Basic YWRtaW46MTIzNDU=", "Authorization: Basic ***")]
    [InlineData("Header Bearer eyJhbGciOi.J9", "Header Bearer ***")]
    [InlineData("WWW-Authenticate Digest cnonce123", "WWW-Authenticate Digest ***")]
    public void Auth_Header_werden_maskiert(string input, string expected)
        => Render(input).Should().Be(expected);

    [Fact]
    public void Mehrere_Treffer_in_einer_Zeile_werden_alle_maskiert()
        => Render("user=admin password=geheim token=xyz")
            .Should().Be("user=admin password=*** token=***");

    [Fact]
    public void Harmlose_Zuweisungen_bleiben_unangetastet()
        => Render("Port=554 Host=10.0.0.5 Dauer=1234ms")
            .Should().Be("Port=554 Host=10.0.0.5 Dauer=1234ms");

    [Fact]
    public void Leere_Message_bleibt_leer()
        => Render(string.Empty).Should().BeEmpty();
}
