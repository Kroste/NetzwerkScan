using System.Globalization;
using System.Reflection;
using System.Resources;
using FluentAssertions;
using NetScanner.Localization;
using Xunit;

namespace NetScanner.Tests;

/// <summary>
/// Haelt die beiden Sprachdateien synchron. Ein fehlender Key fällt sonst erst
/// auf, wenn ein Nutzer die andere Sprache wählt und dort ein "!Key!" steht.
/// </summary>
public class LocalizationTests
{
    private static readonly ResourceManager Rm = new(
        "NetScanner.Localization.Strings", typeof(LocalizationService).Assembly);

    private static IReadOnlyDictionary<string, string> Entries(CultureInfo culture)
    {
        // createIfNotExists: true, tryParents: false -> nur die Einträge DIESER
        // Kultur, ohne Rückfall auf die neutrale Datei. Sonst wäre jeder fehlende
        // deutsche Key durch den englischen Text "abgedeckt" und der Test blind.
        var set = Rm.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        set.Should().NotBeNull($"für '{culture.Name}' muss eine Strings-Ressource existieren");

        return set!.Cast<System.Collections.DictionaryEntry>()
                   .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty);
    }

    [Fact]
    public void Deutsch_und_Englisch_haben_dieselben_Keys()
    {
        var en = Entries(CultureInfo.InvariantCulture);
        var de = Entries(CultureInfo.GetCultureInfo("de"));

        de.Keys.Except(en.Keys).Should().BeEmpty("kein Key darf nur auf Deutsch existieren");
        en.Keys.Except(de.Keys).Should().BeEmpty("jeder englische Key braucht eine deutsche Übersetzung");
    }

    [Fact]
    public void Keine_Uebersetzung_ist_leer()
    {
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de") })
        {
            foreach (var (key, value) in Entries(culture))
                value.Should().NotBeNullOrWhiteSpace($"{key} ({culture.Name}) darf nicht leer sein");
        }
    }

    [Fact]
    public void Platzhalter_stimmen_zwischen_den_Sprachen_ueberein()
    {
        // Ein {1} in der einen und nur {0} in der anderen Sprache laesst string.Format
        // zur Laufzeit werfen — genau dann, wenn ein Nutzer die Sprache umstellt.
        var en = Entries(CultureInfo.InvariantCulture);
        var de = Entries(CultureInfo.GetCultureInfo("de"));

        foreach (var (key, english) in en)
        {
            if (!de.TryGetValue(key, out var german)) continue;
            Placeholders(german).Should().BeEquivalentTo(Placeholders(english),
                $"{key} muss in beiden Sprachen dieselben Platzhalter nutzen");
        }
    }

    [Theory]
    [InlineData("en", "Ready.")]
    [InlineData("de", "Bereit.")]
    public void Der_Service_liefert_die_Sprache_der_aktiven_Kultur(string iso, string expected)
    {
        var service = LocalizationService.Instance;
        var before = service.Current;
        try
        {
            service.SetCulture(iso);
            service["Status_Ready"].Should().Be(expected);
        }
        finally
        {
            service.Current = before;
        }
    }

    [Fact]
    public void Unbekannter_Key_wird_sichtbar_markiert()
        => LocalizationService.Instance["Gibt_Es_Nicht"].Should().Be("!Gibt_Es_Nicht!");

    [Fact]
    public void LocalizedString_wird_pro_Key_gecacht()
    {
        // Der Cache ist die Voraussetzung für den Live-Sprachwechsel: Avalonia haelt
        // Binding.Source nicht stark, ein frisch erzeugter Wrapper wäre nach dem
        // ersten Rendering weg und die Benachrichtigung liefe ins Leere.
        LocalizedString.Get("Status_Ready").Should().BeSameAs(LocalizedString.Get("Status_Ready"));
    }

    private static IEnumerable<string> Placeholders(string text)
    {
        for (int i = 0; i < 10; i++)
            if (text.Contains($"{{{i}}}", StringComparison.Ordinal))
                yield return $"{{{i}}}";
    }
}
