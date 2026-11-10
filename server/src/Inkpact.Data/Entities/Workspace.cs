namespace Inkpact.Data.Entities;

public class Workspace
{
    public const int DefaultHistoryGranularHours = 24;
    public const int DefaultHistoryDailyDays = 30;
    public const int DefaultHistoryExpiryDays = 365;

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

    public bool HistoryEnabled { get; set; } = true;

    public int HistoryGranularHours { get; set; } = DefaultHistoryGranularHours;

    public int HistoryDailyDays { get; set; } = DefaultHistoryDailyDays;

    public int HistoryExpiryDays { get; set; } = DefaultHistoryExpiryDays;

    public long? HistoryCapBytes { get; set; }
}
