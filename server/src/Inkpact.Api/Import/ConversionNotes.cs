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
}

/// <summary>The notes of one conversion, with the same note counted instead of repeated.</summary>
public sealed class ConversionNotes
{
    private readonly Dictionary<(string Code, string? Detail), int> _counts = [];

    public void Add(string code, string? detail = null) =>
        _counts[(code, detail)] = _counts.GetValueOrDefault((code, detail)) + 1;

    public bool IsEmpty => _counts.Count == 0;

    public IReadOnlyList<ConversionNote> ToList() =>
        [.. _counts
            .OrderBy(pair => pair.Key.Code, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Detail, StringComparer.Ordinal)
            .Select(pair => new ConversionNote(pair.Key.Code, pair.Key.Detail, pair.Value))];
}
