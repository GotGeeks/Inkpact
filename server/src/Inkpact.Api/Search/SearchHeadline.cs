using System.Globalization;
using System.Text;

namespace Inkpact.Api.Search;

/// <summary>A piece of a title or a snippet, and whether it is what the search matched.</summary>
public sealed record SearchSegment(string Text, bool Match);

/// <summary>
/// Marks what a search matched in a title or in a piece of a page's text, as segments, so the client never reads markup.
/// A word matches as the index matches it: one that starts with a search word, with case and accents left out, whatever
/// punctuation joins it to the word before (<c>pdf</c> in <c>report.pdf</c>). Postgres finds where in a text the first such
/// word is (<see cref="Pattern"/>); this marks every such word around it.
/// </summary>
public static class SearchHeadline
{
    // Every Latin letter, with and without its accents and in both cases, by the letter it is without them: "e" -> "eEéÉèÈ…".
    private static readonly Dictionary<string, string> Variants = BuildVariants();

    /// <summary>
    /// A Postgres regular expression that finds a word starting with one of <paramref name="words"/>, with case and accents
    /// left out. The words are letters and digits only (<see cref="SearchWords"/>), so nothing in them is regex syntax.
    /// </summary>
    public static string Pattern(IReadOnlyList<string> words) =>
        @"\m(?:" + string.Join('|', words.Select(w => string.Concat(Fold(w).EnumerateRunes().Select(ClassOf)))) + ")";

    /// <summary>
    /// Marks every word of <paramref name="text"/> that starts with one of <paramref name="words"/>. The partial words at the
    /// ends of a piece cut from a longer text are dropped, unless it starts or ends where the text does.
    /// </summary>
    public static IReadOnlyList<SearchSegment> Mark(string? text, IReadOnlyList<string> words, bool atStart, bool atEnd)
    {
        if (string.IsNullOrEmpty(text) || words.Count == 0)
        {
            return [];
        }

        text = Trim(text, atStart, atEnd);
        var prefixes = words.Select(Fold).Where(w => w.Length > 0).ToList();

        // The folded text, and for each of its characters the index in the text it came from.
        var foldedText = new StringBuilder();
        var origin = new List<int>();
        for (var i = 0; i < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            foreach (var c in Fold(rune.ToString()))
            {
                foldedText.Append(c);
                origin.Add(i);
            }

            i += rune.Utf16SequenceLength;
        }

        var folded = foldedText.ToString();
        var segments = new List<SearchSegment>();
        var plainFrom = 0;
        for (var p = 0; p < folded.Length; p++)
        {
            var atWordStart = IsWordChar(folded, p) && (p == 0 || !IsWordChar(folded, p - 1));
            if (!atWordStart || !prefixes.Any(prefix => folded.AsSpan(p).StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            var end = p;
            while (end < folded.Length && IsWordChar(folded, end))
            {
                end++;
            }

            var (from, to) = (origin[p], end < origin.Count ? origin[end] : text.Length);
            if (from > plainFrom)
            {
                segments.Add(new SearchSegment(text[plainFrom..from], false));
            }

            segments.Add(new SearchSegment(text[from..to], true));
            plainFrom = to;
            p = end - 1;
        }

        if (plainFrom < text.Length)
        {
            segments.Add(new SearchSegment(text[plainFrom..], false));
        }

        return Merge(segments);
    }

    /// <summary>A snippet is for showing the match: with none in it (the page matched by its title or a tag), it is empty.</summary>
    public static IReadOnlyList<SearchSegment> OnlyWithMatch(IReadOnlyList<SearchSegment> segments) =>
        segments.Any(s => s.Match) ? segments : [];

    /// <summary>Lower case without accents, as the <c>unaccent</c> and <c>simple</c> dictionaries make a word.</summary>
    private static string Fold(string text)
    {
        try
        {
            text = text.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // A lone surrogate: left as it is.
        }

        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }

        return result.ToString();
    }

    // A letter or digit, also one of two UTF-16 units: either half of it counts as the letter.
    private static bool IsWordChar(string text, int i) =>
        char.IsLowSurrogate(text[i]) && i > 0 ? char.IsLetterOrDigit(text, i - 1) : char.IsLetterOrDigit(text, i);

    // A folded letter as a class of every way it can be written; a letter outside the Latin table in both cases.
    private static string ClassOf(Rune rune)
    {
        if (Variants.TryGetValue(rune.ToString(), out var variants))
        {
            return "[" + variants + "]";
        }

        var upper = Rune.ToUpperInvariant(rune);
        return upper == rune ? rune.ToString() : "[" + rune + upper + "]";
    }

    private static Dictionary<string, string> BuildVariants()
    {
        var variants = new Dictionary<string, StringBuilder>();
        foreach (var (first, last) in new[] { (0x41, 0x24F), (0x1E00, 0x1EFF) })
        {
            for (var code = first; code <= last; code++)
            {
                var letter = ((char)code).ToString();
                var folded = Fold(letter);
                if (folded.Length == 1 && folded[0] is >= 'a' and <= 'z')
                {
                    (variants.TryGetValue(folded, out var builder) ? builder : variants[folded] = new StringBuilder()).Append(letter);
                }
            }
        }

        return variants.ToDictionary(v => v.Key, v => v.Value.ToString());
    }

    private static string Trim(string text, bool atStart, bool atEnd)
    {
        var start = atStart ? 0 : text.IndexOfAny([' ', '\n']) + 1;
        var end = atEnd ? text.Length : text.LastIndexOfAny([' ', '\n']);
        return end > start ? text[start..end] : text;
    }

    // Two pieces of the same kind next to each other are one.
    private static List<SearchSegment> Merge(List<SearchSegment> segments)
    {
        var merged = new List<SearchSegment>();
        foreach (var segment in segments)
        {
            if (merged.Count > 0 && merged[^1].Match == segment.Match)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + segment.Text };
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }
}
