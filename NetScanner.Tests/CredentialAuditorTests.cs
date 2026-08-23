using System.Text;
using FluentAssertions;
using NetScanner.Services;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// Die Netzwerk-Methoden des Auditors lassen sich schwer ohne echten Dienst testen,
/// die Antwort-Parser dagegen gut — und genau dort sitzt das Risiko für Fehlalarme.
/// </summary>
public class CredentialAuditorTests
{
    // ---------------------------------------------------------------- Telnet-IAC

    [Fact]
    public void IAC_Optionsverhandlung_wird_aus_dem_Telnet_Text_entfernt()
    {
        // IAC WILL ECHO (FF FB 01), IAC DO SGA (FF FD 03), dann "login: "
        var data = new List<byte> { 0xFF, 0xFB, 0x01, 0xFF, 0xFD, 0x03 };
        data.AddRange(Encoding.ASCII.GetBytes("login: "));
        CredentialAuditor.StripTelnetIac(data.ToArray()).Should().Be("login: ");
    }

    [Fact]
    public void IAC_Subnegotiation_wird_komplett_uebersprungen()
    {
        // IAC SB ... IAC SE (FF FA .. FF F0) mitten im Text
        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("ab"));
        data.AddRange(new byte[] { 0xFF, 0xFA, 0x18, 0x00, 0xFF, 0xF0 });
        data.AddRange(Encoding.ASCII.GetBytes("cd"));
        CredentialAuditor.StripTelnetIac(data.ToArray()).Should().Be("abcd");
    }

    // ---------------------------------------------------------------- Prompt-Erkennung

    [Theory]
    [InlineData("Login: ", true)]
    [InlineData("Username: ", true)]
    [InlineData("router user: ", true)]
    [InlineData("Welcome to BusyBox", false)]
    [InlineData("", false)]
    public void Login_Prompt_wird_erkannt(string text, bool expected)
        => CredentialAuditor.LooksLikeLoginPrompt(text).Should().Be(expected);

    [Theory]
    [InlineData("Password: ", true)]
    [InlineData("Passwort:", true)]
    [InlineData("login: ", false)]
    public void Passwort_Prompt_wird_erkannt(string text, bool expected)
        => CredentialAuditor.LooksLikePasswordPrompt(text).Should().Be(expected);

    [Theory]
    [InlineData("BusyBox v1.24 built-in shell", true)]
    [InlineData("root@device:~# ", true)]
    [InlineData("~ $ ", true)]
    [InlineData("myrouter> ", true)]
    [InlineData("Login incorrect", false)]
    [InlineData("Password: ", false)]
    public void Shell_Indikator_wird_erkannt(string text, bool expected)
        => CredentialAuditor.LooksLikeShell(text).Should().Be(expected);

    // ---------------------------------------------------------------- Erfolgs-Kriterium

    [Fact]
    public void Telnet_Login_gilt_nur_mit_Shell_und_ohne_Fehlerwort_als_erfolgreich()
    {
        CredentialAuditor.TelnetLoginSucceeded("root@cam:~# ").Should().BeTrue();
    }

    [Theory]
    [InlineData("Login incorrect\r\nlogin: ")]   // Ablehnung + neuer Prompt
    [InlineData("login: ")]                       // nur ein neuer Prompt
    [InlineData("Password: ")]                     // erneute Passwortabfrage
    [InlineData("")]                               // gar keine Shell
    public void Kein_Fehlalarm_ohne_klaren_Shell_Zugriff(string afterPass)
        => CredentialAuditor.TelnetLoginSucceeded(afterPass).Should().BeFalse();

    [Fact]
    public void Eine_Shell_mit_Fehlerwort_zaehlt_nicht_als_Erfolg()
    {
        // Absicht: selbst wenn ein '#' im Text steht, darf ein "incorrect" den Fund kippen.
        CredentialAuditor.TelnetLoginSucceeded("access denied # try again").Should().BeFalse();
    }

    // ---------------------------------------------------------------- FTP-Code

    [Theory]
    [InlineData("220 Welcome", 220)]
    [InlineData("230 Login successful", 230)]
    [InlineData("331 Please specify the password.", 331)]
    [InlineData("530 Login incorrect.", 530)]
    [InlineData("xx", -1)]
    [InlineData("", -1)]
    public void FTP_Statuscode_wird_aus_der_ersten_Zeile_gelesen(string line, int expected)
        => CredentialAuditor.FtpCode(line).Should().Be(expected);
}
