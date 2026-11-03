namespace Inkpact.Api.Search;

/// <summary>A piece of a title or a snippet, and whether it is what the search matched.</summary>
public sealed record SearchSegment(string Text, bool Match);

/// <summary>
/// Turns what <c>ts_headline</c> returns into segments. The query marks a match with two control characters and ends a
/// fragment with a third, after taking those out of the text itself, so the client never reads markup.
/// </summary>
public static class SearchHeadline
{
    public const char Start = '\u0001';

    public const char Stop = '\u0002';

    public const char FragmentEnd = '\u0003';

    private const string Gap = " … ";

    public static IReadOnlyList<SearchSegment> Segments(string? headline)
    {
        var segments = new List<SearchSegment>();
        if (string.IsNullOrEmpty(headline))
        {
            return segments;
        }

        var match = false;
        var start = 0;
        void Flush(int end)
        {
            if (end > start)
            {
                segments.Add(new SearchSegment(headline.Substring(start, end - start), match));
            }
        }

        for (var i = 0; i < headline.Length; i++)
        {
            switch (headline[i])
            {
                case Start:
                    Flush(i);
                    match = true;
                    start = i + 1;
                    break;
                case Stop:
                    Flush(i);
                    match = false;
                    start = i + 1;
                    break;
                case FragmentEnd:
                    Flush(i);
                    match = false;
                    segments.Add(new SearchSegment(Gap, false));
                    start = i + 1;
                    break;
            }
        }

        Flush(headline.Length);
        return Merge(segments);
    }

    /// <summary>A snippet is for showing the match: with none in it (the page matched by its title or a tag), it is empty.</summary>
    public static IReadOnlyList<SearchSegment> OnlyWithMatch(IReadOnlyList<SearchSegment> segments) =>
        segments.Any(s => s.Match) ? segments : [];

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
