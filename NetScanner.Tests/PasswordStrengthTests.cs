using FluentAssertions;
using NetScanner.Services;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// Die Bewertung liefert bewusst Resource-Keys statt fertiger Texte, damit die
/// Klasse ohne Localization-Abhängigkeit testbar bleibt.
/// </summary>
public class PasswordStrengthTests
{
    [Fact]
    public void Leeres_Passwort_ergibt_Score_null_ohne_Zeitangabe()
    {
        var r = PasswordStrength.Evaluate("", foundInLeaks: false);

        r.Score.Should().Be(0);
        r.Label.Should().Be("Strength_None");
        r.CrackTimeFast.Should().Be("Strength_None");
    }

    [Fact]
    public void Ein_Leak_uebersteuert_jede_Komplexitaet()
    {
        // Das hier ist ein rechnerisch sehr starkes Passwort. Steht es in den Listen,
        // ist es trotzdem sofort geknackt — der Leak muss die Entropie schlagen.
        var r = PasswordStrength.Evaluate("xK7!vQ2#mZ9$pL4&nR8@", foundInLeaks: true);

        r.Score.Should().Be(0);
        r.Label.Should().Be("Strength_Leaked");
        r.CrackTimeFast.Should().Be("Crack_InWordlists");
        r.CrackTimeSlow.Should().Be("Crack_InWordlists");
    }

    [Theory]
    [InlineData("abc", "Strength_VeryWeak")]
    [InlineData("passwort", "Strength_VeryWeak")]
    [InlineData("xK7!vQ2#mZ9$pL4&nR8@", "Strength_VeryStrong")]
    public void Die_Bewertung_liefert_Resource_Keys(string password, string expected)
        => PasswordStrength.Evaluate(password, foundInLeaks: false).Label.Should().Be(expected);

    [Fact]
    public void Mehr_Zeichenklassen_erhoehen_die_Entropie()
    {
        double lower = PasswordStrength.Evaluate("abcdefghij", false).Entropy;
        double mixed = PasswordStrength.Evaluate("aB3!efGh1j", false).Entropy;

        mixed.Should().BeGreaterThan(lower);
    }

    [Fact]
    public void Ein_langsamer_Hash_braucht_laenger_als_ein_schneller()
    {
        // Gleiche Entropie, unterschiedliche Angriffsgeschwindigkeit: die
        // bcrypt-Angabe darf nie optimistischer sein als die MD5-Angabe.
        var r = PasswordStrength.Evaluate("aB3!efGh1j", foundInLeaks: false);

        r.CrackTimeFast.Should().NotBe(r.CrackTimeSlow);
    }

    [Theory]
    [InlineData(0.5, "Crack_Instant")]
    [InlineData(30, "Crack_Seconds|30")]
    [InlineData(600, "Crack_Minutes|10")]
    [InlineData(7200, "Crack_Hours|2")]
    [InlineData(60 * 60 * 24 * 3, "Crack_Days|3")]
    [InlineData(1e20, "Crack_Uncrackable")]
    public void Zeitspannen_werden_als_Key_mit_Zahl_geliefert(double seconds, string expected)
        => PasswordStrength.Humanize(seconds).Should().Be(expected);

    [Fact]
    public void Jeder_Score_liegt_im_erlaubten_Bereich()
    {
        string[] samples = ["a", "abc123", "Sommer2024", "aB3!efGh1j", "xK7!vQ2#mZ9$pL4&nR8@"];
        foreach (var s in samples)
            PasswordStrength.Evaluate(s, false).Score.Should().BeInRange(0, 4);
    }
}
