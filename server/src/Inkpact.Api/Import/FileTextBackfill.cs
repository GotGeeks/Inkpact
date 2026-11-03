using System.Text;
using Inkpact.Api.Attachments;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Import;

/// <summary>
/// Gives the pages that the import kept as files before run 1.17c their text for search, once, when the app starts. Such a
/// page has a Joplin body file (<c>.md</c> or <c>.html</c>, no external id) and no text of its own; the newest file is the note as
/// last imported. It does nothing when every page has its text, and a page whose file is gone from the store is
/// logged and left as it is. Later imports write the text themselves.
/// </summary>
public sealed class FileTextBackfill(IServiceScopeFactory scopes, ILogger<FileTextBackfill> logger) : BackgroundService
{
    private const int MaxBlankLength = 32;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Not before the app has started answering: this is a thing to catch up on, not to wait for.
            await Task.Yield();
            var filled = await RunAsync(stoppingToken);
            if (filled > 0)
            {
                logger.LogInformation("Search text read from the files of {Count} imported pages", filled);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reading search text from the files of imported pages failed; it is tried again at the next start");
        }
    }

    /// <summary>One pass. Returns how many pages got their text.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IAttachmentStore>();

        var candidates = await db.Pages.AsNoTracking()
            // A page stored as a file has no text of its own (a file node has attributes, no text), so its content is blank; a short
            // bound keeps the long content of a page that became content again out of this query.
            .Where(p => p.FileText == "" && p.ContentText.Length <= MaxBlankLength
                && db.Attachments.Any(a => a.PageId == p.Id && a.ExternalSourceType == ImportRunner.SourceType && a.ExternalId == null
                    && (a.FileName.EndsWith(".md") || a.FileName.EndsWith(".html"))))
            .Select(p => new { p.Id, p.ContentText })
            .ToListAsync(cancellationToken);

        var filled = 0;
        foreach (var page in candidates)
        {
            var file = await db.Attachments.AsNoTracking()
                .Where(a => a.PageId == page.Id && a.ExternalSourceType == ImportRunner.SourceType && a.ExternalId == null
                    && (a.FileName.EndsWith(".md") || a.FileName.EndsWith(".html")))
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .FirstAsync(cancellationToken);

            // A page with text of its own is not stored as a file now (an old body file stays when a note becomes content again).
            if (!string.IsNullOrWhiteSpace(page.ContentText))
            {
                continue;
            }

            await using var content = await store.OpenReadAsync(file.Sha256, cancellationToken);
            if (content is null)
            {
                logger.LogWarning("The file {File} of page {PageId} is not in the attachment store; its text cannot be searched", file.FileName, page.Id);
                continue;
            }

            using var reader = new StreamReader(content, Encoding.UTF8);
            var text = NoteFileText.Of(await reader.ReadToEndAsync(cancellationToken), file.FileName.EndsWith(".html", StringComparison.Ordinal));
            if (text.Length == 0)
            {
                continue;
            }

            // Only while it is still empty: the import may have written it meanwhile.
            filled += await db.Pages.Where(p => p.Id == page.Id && p.FileText == "")
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.FileText, text), cancellationToken);
        }

        return filled;
    }
}
