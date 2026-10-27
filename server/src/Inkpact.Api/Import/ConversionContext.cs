namespace Inkpact.Api.Import;

/// <summary>A Joplin resource that is stored as an attachment, as far as the conversion needs to know.</summary>
public sealed record ImportedResource(Guid AttachmentId, string FileName, long Size, bool IsInlineImage);

/// <summary>
/// Finds the attachment for a Joplin resource id. The import stores the resources before the notes that use them, and the
/// document refers to attachments only through this: an id written in a note's HTML is never taken as an attachment id.
/// </summary>
public interface IResourceResolver
{
    /// <summary>The attachment for the resource, or null when the export has no such resource (or could not be stored).</summary>
    ImportedResource? Find(string joplinResourceId);
}

/// <summary>A resolver for content without resources.</summary>
public sealed class NoResources : IResourceResolver
{
    public static readonly NoResources Instance = new();

    public ImportedResource? Find(string joplinResourceId) => null;
}

/// <summary>What one conversion shares: where resources are, and where the notes for the report go.</summary>
internal sealed class ConversionContext(IResourceResolver resources, ConversionNotes notes)
{
    public IResourceResolver Resources { get; } = resources;

    public ConversionNotes Notes { get; } = notes;

    /// <summary>
    /// A reference to a resource as it appears in the text: an image (a block) when the stored file is a picture the
    /// editor shows inline, or a file (inline) otherwise. Null when the export has no such resource.
    /// </summary>
    public PmNode? ResourceNode(string resourceId, string? description, out bool isBlock)
    {
        isBlock = false;
        if (Resources.Find(resourceId) is not { } resource)
        {
            return null;
        }

        if (resource.IsInlineImage)
        {
            isBlock = true;
            return PmNode.Element("image", attrs: new Dictionary<string, object?>
            {
                ["attachmentId"] = resource.AttachmentId.ToString(),
                ["alt"] = ContentRules.Limit(ContentRules.CleanText(description ?? ""), ContentRules.MaxAltLength, Notes, "alt"),
            });
        }

        return File(resource);
    }

    /// <summary>
    /// Decides what a link becomes. A link to a stored resource is a file (the returned node); a link the editor allows
    /// gives its address in <paramref name="href"/>; anything else keeps only its text, and the report says why.
    /// </summary>
    /// <param name="address">The link as written.</param>
    /// <param name="visibleText">What the link shows, for telling whether a file's link text is lost.</param>
    public PmNode? ResolveLink(string? address, Func<string> visibleText, out string? href)
    {
        href = null;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (ContentRules.JoplinItemId(address) is { } id)
        {
            if (Resources.Find(id) is not { } resource)
            {
                // Another note (arriving in run 1.16), or a resource that is not in the export.
                Notes.Add(NoteCodes.NoteLinkPending);
                return null;
            }

            var text = visibleText().Trim();
            if (text.Length > 0 && !string.Equals(text, resource.FileName, StringComparison.Ordinal))
            {
                Notes.Add(NoteCodes.ResourceLinkTextLost);
            }

            return File(resource);
        }

        href = ContentRules.AllowedLink(address, out var scheme);
        if (href is null)
        {
            Notes.Add(NoteCodes.LinkRemoved, scheme);
        }

        return null;
    }

    /// <summary>
    /// Adds an image to <paramref name="sink"/>. One of the export's pictures is an image block (or a file in the text,
    /// when it is not a kind the editor shows); a picture from another site becomes a link to it, since the editor only
    /// shows its own attachments; anything else keeps its description.
    /// </summary>
    public void AddImage(BlockSink sink, string? source, string description, TextMarks marks)
    {
        source = source?.Trim();
        if (ContentRules.JoplinItemId(source) is { } id)
        {
            if (ResourceNode(id, description, out var isBlock) is not { } node)
            {
                Notes.Add(NoteCodes.ResourceMissing);
                sink.AddText(description, marks);
            }
            else if (isBlock)
            {
                sink.AddBlock(node);
            }
            else
            {
                sink.AddInline(node);
            }

            return;
        }

        var address = ContentRules.AllowedLink(source, out var scheme);
        if (address is not null && scheme is "http" or "https")
        {
            Notes.Add(NoteCodes.ExternalImage);
            var text = description.Trim().Length > 0 ? description : address;
            sink.AddText(text, marks.Href is null ? marks with { Href = address } : marks);
            return;
        }

        Notes.Add(NoteCodes.ImageNotKept, string.IsNullOrEmpty(source) ? "none" : scheme);
        sink.AddText(description, marks);
    }

    public static PmNode File(ImportedResource resource) => PmNode.Element("attachment", attrs: new Dictionary<string, object?>
    {
        ["attachmentId"] = resource.AttachmentId.ToString(),
        ["fileName"] = resource.FileName,
        ["size"] = resource.Size,
    });
}
