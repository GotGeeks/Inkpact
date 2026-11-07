namespace Inkpact.Data.Entities;

/// <summary>
/// A link from one page to another, found in the source page's content: a link mark whose address is <c>/p/&lt;id&gt;</c>.
/// The index behind the backlinks of a page (run B.1). The target has no foreign key: a link may point to a page that does
/// not exist yet (the import writes such links before the note they point to is imported) or no longer exists, and it stays
/// in the content either way. Written only by the compactor and by the job that indexes pages that exist from before.
/// </summary>
public class PageLink
{
    public Guid SourcePageId { get; set; }

    public Guid TargetPageId { get; set; }
}
