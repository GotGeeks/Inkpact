namespace Inkpact.Data.Entities;

/// <summary>
/// A time at which a person wants to be reminded of a page. The Joplin import makes one for every to-do that has a due
/// date. Nothing delivers them yet (phase 2); they are stored so that nothing is lost before that exists.
/// </summary>
public class Reminder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid PageId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset RemindAt { get; set; }

    /// <summary>Null until the reminder has been delivered.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
