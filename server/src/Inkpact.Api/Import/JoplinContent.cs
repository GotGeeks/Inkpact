namespace Inkpact.Api.Import;

/// <summary>A note's body as the editor's document, and what did not come across as it was.</summary>
public sealed record ConvertedContent(PmNode Document, IReadOnlyList<ConversionNote> Notes);

/// <summary>
/// Turns the body of a Joplin note (Markdown, or HTML for notes from the web clipper) into the editor's document. Pure:
/// no database and no sidecar. The document goes to the sidecar's build operation, which writes it as the page's first
/// Yjs update.
/// </summary>
public static class JoplinContent
{
    public static ConvertedContent Convert(string body, bool isHtml, IResourceResolver resources)
    {
        var notes = new ConversionNotes();
        var context = new ConversionContext(resources, notes);
        using var html = new HtmlConverter(context);
        var blocks = isHtml ? html.Convert(body) : new MarkdownConverter(context, html).Convert(body);
        return new ConvertedContent(PmNode.Doc(blocks), notes.ToList());
    }
}
