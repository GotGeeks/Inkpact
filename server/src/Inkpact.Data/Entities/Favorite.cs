namespace Inkpact.Data.Entities;

/// <summary>
/// A page a person starred (run B.3). Private to that person: nobody else reads it, not even the workspace's owner. It is
/// kept while the page is in the trash and goes with the page when that is deleted for good.
/// </summary>
public class Favorite
{
    public Guid UserId { get; set; }

    public Guid PageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The last time a person opened a page (run B.3): the list of recently opened pages. Private to that person.</summary>
public class PageVisit
{
    public Guid UserId { get; set; }

    public Guid PageId { get; set; }

    public DateTimeOffset OpenedAt { get; set; }
}
