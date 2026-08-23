using System.Text;

namespace NetScanner.Services;

/// <summary>
/// Lokale, transparente Schaetzung der Passwortstaerke und der Offline-Crackzeit.
/// Bewusst keine externe Bibliothek: Zeichenraum × Laenge ergibt eine Entropie-Schaetzung,
/// die um erkennbare Muster (nur Ziffern, gaengige Woerter, Wiederholungen, Sequenzen)
/// reduziert wird. Daraus wird die mittlere Versuchszahl und — geteilt durch typische
/// Hash-Raten — die Crackzeit gegen einen schnellen (MD5) und einen langsamen (bcrypt) Hash.
///
/// Es ist eine Groessenordnungs-Schaetzung (Sekunden vs. Jahrhunderte), kein Beweis —
/// fuer die Entscheidung "aendern ja/nein" reicht das.
/// </summary>
public static class PasswordStrength
{
    // Grobe Hash-Raten einer einzelnen starken GPU (Groessenordnung):
    // MD5 = sehr schnell (schlecht fuer Passwoerter), bcrypt(cost 12) = absichtlich langsam.
    private const double Md5HashesPerSec = 1e11;     // ~100 Mrd./s
    private const double BcryptHashesPerSec = 1e4;   // ~10 Tsd./s

    // Kleine Liste sehr haeufiger Basis-Muster — nur fuer den Entropie-Abschlag,
    // NICHT als Leak-Pruefung (das macht der HIBP-Check).
    private static readonly string[] WeakTokens =
    [
        "password", "passwort", "admin", "qwertz", "qwerty", "letmein", "welcome",
        "monkey", "dragon", "iloveyou", "sommer", "winter", "hallo", "test",
    ];

    public sealed record Result(
        int Score,                 // 0 (kritisch) … 4 (sehr stark)
        string Label,              // "Sehr schwach" … "Sehr stark"
        double Entropy,            // geschaetzte Bit-Entropie
        string CrackTimeFast,      // gegen MD5 & Co.
        string CrackTimeSlow);     // gegen bcrypt & Co.

    /// <summary>Bewertet ein Passwort. <paramref name="foundInLeaks"/> aus dem HIBP-Check
    /// uebersteuert alles — ein geleaktes Passwort faellt sofort.</summary>
    public static Result Evaluate(string password, bool foundInLeaks)
    {
        if (string.IsNullOrEmpty(password))
            return new Result(0, "—", 0, "—", "—");

        if (foundInLeaks)
            return new Result(0, "In Leaks — sofort knackbar", 0,
                "sofort (steht in Wortlisten)", "sofort (steht in Wortlisten)");

        double entropy = EstimateEntropy(password);
        // Mittlere Versuchszahl = halber Suchraum = 2^entropy / 2.
        double guesses = Math.Pow(2, Math.Min(entropy, 1024)) / 2.0;

        string fast = Humanize(guesses / Md5HashesPerSec);
        string slow = Humanize(guesses / BcryptHashesPerSec);

        var (score, label) = entropy switch
        {
            < 28 => (0, "Sehr schwach"),
            < 40 => (1, "Schwach"),
            < 60 => (2, "Mittel"),
            < 80 => (3, "Stark"),
            _    => (4, "Sehr stark"),
        };
        return new Result(score, label, entropy, fast, slow);
    }

    private static double EstimateEntropy(string pw)
    {
        // 1) Zeichenraum aus tatsaechlich genutzten Klassen.
        int pool = 0;
        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (char c in pw)
        {
            if (char.IsLower(c)) lower = true;
            else if (char.IsUpper(c)) upper = true;
            else if (char.IsDigit(c)) digit = true;
            else symbol = true;
        }
        if (lower) pool += 26;
        if (upper) pool += 26;
        if (digit) pool += 10;
        if (symbol) pool += 33;
        if (pool == 0) pool = 1;

        double entropy = pw.Length * Math.Log2(pool);

        // 2) Muster-Abschlaege (konservativ, multiplikativ auf die Bits).
        string lc = pw.ToLowerInvariant();

        // Nur Ziffern (PIN/Datum) -> Suchraum real viel kleiner.
        if (digit && !lower && !upper && !symbol)
            entropy *= 0.55;

        // Enthaelt ein gaengiges Wort -> der Wortteil traegt kaum Entropie.
        foreach (var t in WeakTokens)
            if (lc.Contains(t)) { entropy -= Math.Log2(pool) * (t.Length - 1); break; }

        // Viele Wiederholungen (z. B. "aaaaaa", "ababab").
        if (HasStrongRepetition(pw)) entropy *= 0.6;

        // Aufsteigende/absteigende Sequenz (z. B. "1234", "abcd").
        if (HasSequence(lc)) entropy *= 0.7;

        return Math.Max(entropy, 0);
    }

    private static bool HasStrongRepetition(string pw)
    {
        if (pw.Length < 4) return false;
        int distinct = pw.Distinct().Count();
        return distinct <= Math.Max(2, pw.Length / 3);
    }

    private static bool HasSequence(string lc)
    {
        if (lc.Length < 4) return false;
        int asc = 0, desc = 0;
        for (int i = 1; i < lc.Length; i++)
        {
            int d = lc[i] - lc[i - 1];
            asc = d == 1 ? asc + 1 : 0;
            desc = d == -1 ? desc + 1 : 0;
            if (asc >= 3 || desc >= 3) return true;
        }
        return false;
    }

    /// <summary>Sekunden in eine lesbare Spanne uebersetzen.</summary>
    private static string Humanize(double seconds)
    {
        if (seconds < 1) return "sofort";
        if (seconds < 60) return $"{seconds:N0} Sekunden";
        double m = seconds / 60;
        if (m < 60) return $"{m:N0} Minuten";
        double h = m / 60;
        if (h < 24) return $"{h:N0} Stunden";
        double d = h / 24;
        if (d < 30) return $"{d:N0} Tage";
        double mo = d / 30;
        if (mo < 12) return $"{mo:N0} Monate";
        double y = d / 365;
        if (y < 1_000) return $"{y:N0} Jahre";
        if (y < 1e6) return $"{y / 1e3:N0} Tausend Jahre";
        if (y < 1e9) return $"{y / 1e6:N0} Millionen Jahre";
        return "praktisch unknackbar";
    }
}
