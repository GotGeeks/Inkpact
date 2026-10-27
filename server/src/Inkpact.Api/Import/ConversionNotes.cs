namespace Inkpact.Api.Import;

/// <summary>
/// Something in a note that did not come across as it was: the import's report lists these. <see cref="Code"/> is a
/// stable code the client translates; <see cref="Detail"/> says what exactly (a tag name, a scheme) and is never
/// shown as a sentence.
/// </summary>
public sealed record ConversionNote(string Code, string? Detail = null, int Count = 1);

/// <summary>The codes of <see cref="ConversionNote"/>.</summary>
public static class NoteCodes
{
    /// <summary>An element the editor has no node for: its text stayed, the element did not. Detail: the tag.</summary>
    public const string UnsupportedElement = "unsupported_element";

    /// <summary>An element that is removed with everything inside it (script, style, frames). Detail: the tag.</summary>
    public const string RemovedElement = "removed_element";

    /// <summary>A link with an address the editor does not allow: its text stayed. Detail: the scheme, or <c>relative</c>.</summary>
    public const string LinkRemoved = "link_removed";

    /// <summary>A link to another Joplin note: its text stayed. The link itself arrives in run 1.16.</summary>
    public const string NoteLinkPending = "note_link_pending";

    /// <summary>A link or image that refers to a resource that is not in the export. Its text stayed.</summary>
    public const string ResourceMissing = "resource_missing";

    /// <summary>A link to a file whose text differs from the file's name: the file's name is shown instead.</summary>
    public const string ResourceLinkTextLost = "resource_link_text_lost";

    /// <summary>An image from another site became a link to its address. Detail: nothing.</summary>
    public const string ExternalImage = "external_image";

    /// <summary>An image that could not be kept at all (an embedded <c>data:</c> image, a local file); its description stayed.</summary>
    public const string ImageNotKept = "image_not_kept";

    public const string Footnote = "footnote";
    public const string DefinitionList = "definition_list";
    public const string Math = "math";
    public const string Highlight = "highlight";
    public const string SubSuperscript = "sub_superscript";
    public const string Mermaid = "mermaid";

    /// <summary>Content nested deeper than the editor handles: the inner levels were flattened.</summary>
    public const string NestingFlattened = "nesting_flattened";

    /// <summary>Formatting the editor cannot combine with a link (inline code): the link stayed. Detail: the mark that went.</summary>
    public const string MarkDropped = "mark_dropped";

    /// <summary>An attribute or text that was longer than allowed and was cut. Detail: what.</summary>
    public const string Truncated = "truncated";

    // What the import itself found, besides what the conversion did. Same shape, so the report lists them alike.

    /// <summary>The item is encrypted in the export; it was skipped. Export from Joplin desktop with encryption off.</summary>
    public const string Encrypted = "encrypted";

    /// <summary>The item is larger than the importer reads (<see cref="JexArchive.MaxItemSize"/>); it was skipped.</summary>
    public const string ItemTooLarge = "item_too_large";

    /// <summary>A file the note uses is larger than an attachment may be; it is not in the note. Detail: its name.</summary>
    public const string ResourceTooLarge = "resource_too_large";

    /// <summary>A file the note uses is empty, so no attachment was made. Detail: its name.</summary>
    public const string ResourceEmpty = "resource_empty";

    /// <summary>The note's notebook is not in the export, or was not imported; the note is at the top level.</summary>
    public const string NotebookMissing = "notebook_missing";

    /// <summary>The notebook was its own ancestor in the export; it is at the top level.</summary>
    public const string NotebookCycle = "notebook_cycle";

    /// <summary>The notebook the item belongs to could not be imported, so the item was not either.</summary>
    public const string ParentFailed = "parent_failed";

    /// <summary>The page's content was refused by the Yjs builder: a fault in the conversion, not in the note.</summary>
    public const string ConversionFailed = "conversion_failed";

    /// <summary>Something else went wrong with this item (see the server's log).</summary>
    public const string ItemFailed = "item_failed";
}

/// <summary>The notes of one conversion, with the same note counted instead of repeated.</summary>
public sealed class ConversionNotes
{
    private readonly Dictionary<(string Code, string? Detail), int> _counts = [];

    public void Add(string code, string? detail = null) =>
        _counts[(code, detail)] = _counts.GetValueOrDefault((code, detail)) + 1;

    /// <summary>Adds a note that was counted elsewhere, with its count.</summary>
    public void Add(ConversionNote note) =>
        _counts[(note.Code, note.Detail)] = _counts.GetValueOrDefault((note.Code, note.Detail)) + note.Count;

    public bool IsEmpty => _counts.Count == 0;

    public IReadOnlyList<ConversionNote> ToList() =>
        [.. _counts
            .OrderBy(pair => pair.Key.Code, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Detail, StringComparer.Ordinal)
            .Select(pair => new ConversionNote(pair.Key.Code, pair.Key.Detail, pair.Value))];
}
