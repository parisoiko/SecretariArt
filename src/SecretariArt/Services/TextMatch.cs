using System.Globalization;
using System.Text;

namespace SecretariArt.Services;

// Comparing names the way people read them: ignoring case and accents. Greek is written without accents in
// capitals ("ΧΑΡΑΚΤΙΚΗ" for "Χαρακτική"), and people often type it unaccented, so a plain case-insensitive
// comparison treats them as different words. Done in memory (the lists are one organisation's), so it behaves the
// same on SQL Server and on the SQLite used by the tests.
public static class TextMatch
{
    public static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var key = new StringBuilder();
        foreach (char c in text.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                key.Append(c);
            }
        }

        // Final sigma is the same letter as σ.
        return key.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('ς', 'σ');
    }

    public static bool SameName(string? a, string? b) => Key(a) == Key(b);

    public static bool Contains(string? text, string? term) => Key(text).Contains(Key(term), StringComparison.Ordinal);
}
