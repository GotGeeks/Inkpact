namespace Inkpact.Data.Entities;

public class Workspace
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = "";

    public Guid OwnerId { get; set; }

    public User? Owner { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
