using FluentAssertions;
using NetScanner.Services;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// Versionsvergleich des Update-Checks. Ein Stringvergleich wuerde hier 1.10.0
/// für kleiner als 1.9.0 halten — genau deshalb gibt es ParseVersion.
/// </summary>
public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.5.3", "1.5.3")]
    [InlineData("1.5.3", "1.5.3")]
    [InlineData("V2.0.0", "2.0.0")]
    [InlineData("1.5.4-alpha.0.1", "1.5.4")]
    [InlineData("1.5.3+abc1234", "1.5.3")]
    [InlineData("  v1.2.3  ", "1.2.3")]
    public void Tags_werden_zu_vergleichbaren_Versionen(string tag, string expected)
        => UpdateService.ParseVersion(tag).Should().Be(Version.Parse(expected));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    public void Unbrauchbare_Tags_liefern_null(string? tag)
        => UpdateService.ParseVersion(tag).Should().BeNull();

    [Fact]
    public void Zehner_Minor_ist_groesser_als_Neuner_Minor()
    {
        // Der klassische Stringvergleichs-Fehler: "1.10.0" < "1.9.0".
        var neu = UpdateService.ParseVersion("v1.10.0")!;
        var alt = UpdateService.ParseVersion("v1.9.0")!;
        neu.Should().BeGreaterThan(alt);
    }

    [Fact]
    public void Vorabversion_gilt_nicht_als_neuer_als_das_Release()
    {
        // MinVer stempelt einen ungetaggten Commit als 1.5.4-alpha.0.1. Nach dem
        // Abschneiden ist das 1.5.4 und damit NICHT kleiner als das Release 1.5.4 —
        // ein Entwicklungsstand soll sich kein Update auf dieselbe Version ziehen.
        var lokal = UpdateService.ParseVersion("1.5.4-alpha.0.1")!;
        var release = UpdateService.ParseVersion("v1.5.4")!;
        (release > lokal).Should().BeFalse();
    }

    [Fact]
    public void Laufende_Version_ist_auswertbar()
        => UpdateService.Current.Should().NotBeNull();
}
