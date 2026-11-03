using Markdig;

namespace Inkpact.Api.Import;

/// <summary>
/// The text of a note that a page keeps as a file (run 1.17c), for search: Markdown or HTML as plain text, as much as is
/// indexed of any page. The page's own text is only the file's name, and the compactor rewrites that, so this is kept in
/// a column of its own.
/// </summary>
public static class NoteFileText
{
    /// <summary>What is indexed of a page's text (migration AddSearch): the same bound, since a vector holds at most 1 MB.</summary>
    public const int MaxLength = 200_000;

    // A few times what is kept, so that markup and its removal cannot leave less than MaxLength of a long note.
    private const int MaxSource = 1_000_000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    public static string Of(string body, bool isHtml)
    {
        // NUL first: Markdown makes it a replacement character, which is no word.
        var source = body.Replace("\0", "");
        source = source.Length > MaxSource ? source[..MaxSource] : source;
        string text;
        try
        {
            text = isHtml ? ContentRules.TextOfMarkup(source) : Markdown.ToPlainText(source, Pipeline);
        }
        catch (ArgumentException)
        {
            // Markdig refuses to follow elements nested very deep: the words are what search needs, as they stand.
            text = source;
        }

        // The page's own text is cleaned this way too: no control characters, no NUL, no half surrogate pairs.
        text = ContentRules.CleanText(text).Trim();
        return text.Length > MaxLength ? ContentRules.CleanText(text[..MaxLength]) : text;
    }
}
