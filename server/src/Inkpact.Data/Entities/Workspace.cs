namespace Inkpact.Data.Entities;

public class Workspace
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = "";

    public Guid OwnerId { get; set; }

    public User? Owner { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// +1 on every change to the page tree. Every tree operation starts by raising it, which locks this row until the
    /// transaction ends, so the operations of one workspace run one at a time.
    /// </summary>
    public long TreeVersion { get; set; }
}
