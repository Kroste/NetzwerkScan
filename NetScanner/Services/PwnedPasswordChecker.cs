using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetScanner.Services;

/// <summary>
/// Prüft ein Passwort gegen die "Pwned Passwords"-Datenbank von Have I Been Pwned
/// (https://haveibeenpwned.com/Passwords) per k-Anonymity-Verfahren:
///
/// Es wird NUR das 5-stellige Praefix des SHA-1-Hashes übertragen — niemals das
/// Passwort und niemals der vollständige Hash. Die API liefert alle Hash-Endungen
/// zu diesem Praefix (typ. mehrere hundert) samt Leak-Häufigkeit; der eigentliche
/// Abgleich passiert lokal. Die API kann daraus nicht ableiten, welches Passwort
/// geprüft wurde. Genau dieses Verfahren nutzen Bitwarden, 1Password, Google und
/// Firefox Monitor für ihre Leak-Warnungen.
/// </summary>
public sealed class PwnedPasswordChecker(ILogger<PwnedPasswordChecker> log)
{
    // Static -> ein Socket-Pool, kein Exhaustion bei wiederholten Prüfungen.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // HIBP verlangt einen aussagekraeftigen User-Agent, sonst 403.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NetScanner-PwnedCheck/1.3");
        return c;
    }

    /// <summary>Found = Passwort taucht in Leaks auf; Count = Anzahl der Vorkommen.</summary>
    public sealed record Result(bool Found, long Count);

    /// <summary>
    /// Prüft das Passwort. Liefert null, wenn die Prüfung nicht möglich war
    /// (z. B. offline / API nicht erreichbar). Das Passwort verlaesst das Gerät nicht.
    /// </summary>
    public async Task<Result?> CheckAsync(string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) return null;
        try
        {
            // SHA-1 (HIBP-Vorgabe), Hex in Grossbuchstaben -> "ABC12..." (40 Zeichen).
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
            string prefix = hash[..5];     // geht an die API
            string suffix = hash[5..];     // bleibt lokal (35 Zeichen)

            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.pwnedpasswords.com/range/{prefix}");
            // Padding fuellt die Antwort mit Dummy-Einträgen (Count 0) auf, sodass die
            // Antwortgröße nicht verraet, ob ein Treffer dabei ist.
            req.Headers.Add("Add-Padding", "true");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync(ct);

            foreach (var line in body.Split('\n'))
            {
                // Jede Zeile: "<35-stelliger Suffix>:<count>".
                if (line.Length < 37 || line[35] != ':') continue;
                if (line.AsSpan(0, 35).Equals(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    long count = long.TryParse(line.AsSpan(36).Trim(), out var c) ? c : 0;
                    if (count > 0)
                        log.LogWarning("Passwort in {Count} Leaks gefunden (HIBP)", count);
                    return new Result(count > 0, count);
                }
            }
            return new Result(false, 0);   // Suffix nicht in der Liste -> nicht geleakt
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Pwned-Password-Check fehlgeschlagen");
            return null;
        }
    }
}
