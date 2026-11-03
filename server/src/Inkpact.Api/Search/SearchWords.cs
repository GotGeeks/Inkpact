using System.Text;

namespace Inkpact.Api.Search;

/// <summary>
/// What a person typed into the search box, made into a query for Postgres. Only letters and digits get through, each word
/// as a prefix, so that what is typed can never be query syntax and a half typed word already finds its page.
/// </summary>
public static class SearchWords
{
    public const int MaxWords = 8;

    public const int MaxWordLength = 64;

    // A search text is a few words; the rest of a long paste is not looked at.
    private const int MaxInputLength = 1000;

    /// <summary>The words of the text, in order: runs of letters and digits, at most <see cref="MaxWords"/> of at most <see cref="MaxWordLength"/> characters.</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        text = text.Length > MaxInputLength ? text[..MaxInputLength] : text;
        try
        {
            // An accent typed as a letter and a combining mark is one letter, as in the text it is looked for in.
            text = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // A lone surrogate: left as it is, and it is not a letter.
        }

        var words = new List<string>();
        var word = new StringBuilder();
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (length < MaxWordLength)
                {
                    word.Append(rune.ToString());
                    length++;
                }

                continue;
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
                length = 0;
                if (words.Count == MaxWords)
                {
                    return words;
                }
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary>The text as a <c>to_tsquery</c> string (<c>meet:* &amp; week:*</c>), or null when it has no words.</summary>
    public static string? ToTsQuery(string? text) => ToTsQuery(Split(text));

    /// <summary>Words from <see cref="Split"/> as a <c>to_tsquery</c> string, or null when there are none.</summary>
    public static string? ToTsQuery(IReadOnlyList<string> words) =>
        words.Count == 0 ? null : string.Join(" & ", words.Select(w => w + ":*"));
}
