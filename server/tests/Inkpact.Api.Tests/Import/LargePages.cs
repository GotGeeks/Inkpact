using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// Limits that make a page of a few kilobytes stand for one of several MiB, for the import's replacement of a large page and
/// the compaction right after it (run 1.22). A page may be 4,000 bytes (half the queue), and a page's stored updates with a
/// replacement 6,000 (three quarters of it). Shared by the test classes of the import, so the numbers live in one place.
/// </summary>
internal static class LargePages
{
    public const int TextLimit = 3500;
    public const int UpdateLimit = 4000;
    public const int Queue = 2 * UpdateLimit;

    public static Dictionary<string, string> Settings(IReadOnlyDictionary<string, string>? more = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Import:MaxNoteTextBytes"] = TextLimit.ToString(),
            ["Import:MaxNoteUpdateBytes"] = UpdateLimit.ToString(),
            ["Sync:MaxQueuedBytes"] = Queue.ToString(),
        };
        foreach (var (key, value) in more ?? new Dictionary<string, string>())
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>Plain words after <paramref name="start"/>, about <paramref name="bytes"/> bytes of them.</summary>
    public static string Words(string start, int bytes) => start + string.Concat(Enumerable.Repeat("word ", bytes / 5 + 1))[..bytes];

    public static Task<int> LiveUpdatesAsync(InkpactDbContext db, Guid pageId) => db.PageUpdates.CountAsync(u => u.PageId == pageId);

    public static Task<PageSnapshot?> SnapshotOfAsync(InkpactDbContext db, Guid pageId) =>
        db.PageSnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.PageId == pageId);
}
