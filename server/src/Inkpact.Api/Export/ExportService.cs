using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Inkpact.Api.Attachments;
using Inkpact.Api.Crdt;
using Inkpact.Api.Sync;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Export;

public enum ExportOutcome
{
    /// <summary>The whole zip was written.</summary>
    Written,

    /// <summary>There is no such workspace. Nothing was written.</summary>
    WorkspaceNotFound,

    /// <summary>The Yjs sidecar cannot read pages, and the first page found it out. Nothing was written.</summary>
    SidecarUnavailable,
}

/// <summary>
/// Everything a person can read in a workspace as one zip of Markdown files and attachments (run 1.18b). The zip is written
/// to the output while it is made: pages are read one at a time, so memory stays the size of the largest page, and nothing is
/// kept on the server. It ends whole or not at all: when something fails after the first byte, the exception goes on to the
/// caller, which must cut the connection, and the zip's index is never written, so no reader takes what came for a whole zip.
/// </summary>
public sealed class ExportService(
    InkpactDbContext db,
    PageUpdateStore updates,
    IYjsCodec codec,
    IAttachmentStore store,
    TimeProvider time,
    ILogger<ExportService> logger)
{
    /// <param name="workspaceId">The workspace to export. The caller has checked that the person may read it.</param>
    /// <param name="language">The language of the README, <c>en</c> or <c>nl</c>; anything else is English.</param>
    /// <param name="start">Called once, with the name of the download, when the first bytes are about to be sent: from here on the
    /// library leaves the server. It gives the stream to write to (and sets the response's headers, and
    /// writes the audit line). Not called when the outcome is not <see cref="ExportOutcome.Written"/>.</param>
    public async Task<ExportOutcome> WriteAsync(
        Guid workspaceId, string? language, Func<string, CancellationToken, Task<Stream>> start, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.Name).SingleOrDefaultAsync(cancellationToken);
        if (workspace is null)
        {
            return ExportOutcome.WorkspaceNotFound;
        }

        var plan = ExportPlan.Build(await LoadPagesAsync(workspaceId, cancellationToken), await LoadFilesAsync(workspaceId, cancellationToken));
        var readmeLanguage = ExportReadme.LanguageOf(language);
        var date = time.GetUtcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var missingFiles = new List<string>();
        var unreadablePages = new List<string>();
        var skipped = 0;
        var fileCount = 0;

        ZipArchive? archive = null;
        ResponseZipStream? output = null;
        try
        {
            async Task<ZipArchive> ArchiveAsync()
            {
                if (archive is null)
                {
                    var fileName = $"inkpact-{ExportNames.DownloadName(workspace)}-{date}.zip";
                    output = new ResponseZipStream(token => start(fileName, token));
                    archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken);
                }

                return archive;
            }

            foreach (var planned in plan.Pages)
            {
                // Read before anything is sent, so that the first page decides whether this can be done at all.
                var (text, pageSkipped, unreadable) = await RenderAsync(planned, plan, cancellationToken);
                skipped += pageSkipped;
                if (unreadable)
                {
                    unreadablePages.Add(planned.Path);
                }

                await AddEntryAsync(await ArchiveAsync(), planned.Path, Encoding.UTF8.GetBytes(text), planned.Page.UpdatedAt, CompressionLevel.Optimal, cancellationToken);
                foreach (var file in plan.TakeNewFiles())
                {
                    if (await AddFileAsync(await ArchiveAsync(), file, cancellationToken))
                    {
                        fileCount++;
                    }
                    else
                    {
                        missingFiles.Add(file.Path);
                    }
                }
            }

            var readme = ExportReadme.Write(readmeLanguage, workspace, date, plan.Pages.Count, fileCount, missingFiles, unreadablePages);
            await AddEntryAsync(await ArchiveAsync(), ExportPlan.ReadmeName, Encoding.UTF8.GetBytes(readme), time.GetUtcNow(), CompressionLevel.Optimal, cancellationToken);

            // The index is written here and only here: a failure above leaves the zip without it.
            await archive!.DisposeAsync();
            await output!.FlushAsync(cancellationToken);
        }
        catch (YjsUnavailableException) when (output is not { HasSent: true })
        {
            // Nothing has left the server (the first pages fit in what is gathered before sending), so this can still be answered.
            return ExportOutcome.SidecarUnavailable;
        }

        if (skipped > 0)
        {
            logger.LogWarning("Export of workspace {WorkspaceId}: {Skipped} things the editor does not have were left out of the pages.", workspaceId, skipped);
        }

        if (missingFiles.Count > 0 || unreadablePages.Count > 0)
        {
            logger.LogWarning(
                "Export of workspace {WorkspaceId}: {Missing} files could not be read from the store and {Unreadable} pages could not be read; the README lists them.",
                workspaceId, missingFiles.Count, unreadablePages.Count);
        }

        return ExportOutcome.Written;
    }

    /// <summary>
    /// Whether an export can be made now: the sidecar answers. The client asks before it starts the download, so that a person
    /// gets a message in their language instead of a download that fails.
    /// </summary>
    public async Task<bool> CanExportAsync(CancellationToken cancellationToken)
    {
        try
        {
            await codec.ReadAsync([], cancellationToken);
            return true;
        }
        catch (YjsUnavailableException)
        {
            return false;
        }
    }

    /// <summary>A page as the file in the zip: its front matter, and its document as Markdown.</summary>
    private async Task<(string Text, int Skipped, bool Unreadable)> RenderAsync(PlannedPage planned, ExportPlan plan, CancellationToken cancellationToken)
    {
        var content = await updates.LoadAsync(planned.Page.Id, cancellationToken);
        var frontMatter = ExportFrontMatter.Of(planned.Page);
        try
        {
            var read = await codec.ReadAsync(content.Inputs, cancellationToken);
            var links = new ExportLinks(
                id => plan.UseFile(id) is { } path ? ExportPlan.PathOfFile(planned.Path, path) : null,
                id => plan.PathOfPage(planned.Path, id));
            var body = new MarkdownWriter(links).Write(read.Json);
            return (body.Length == 0 ? frontMatter : frontMatter + "\n" + body, read.Skipped, false);
        }
        catch (YjsRejectedException e)
        {
            // Stored bytes that are not a page. The rest of the library is still the person's, so this page is there with its
            // title and dates, and the README says which it is. A sidecar that is down is another thing: that fails the export.
            logger.LogWarning(e, "Export: the content of page {PageId} could not be read.", planned.Page.Id);
            return (frontMatter, 0, true);
        }
    }

    private static async Task AddEntryAsync(ZipArchive archive, string path, byte[] bytes, DateTimeOffset modified, CompressionLevel level, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, level);
        entry.LastWriteTime = Clamp(modified);
        var stream = await entry.OpenAsync(cancellationToken);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken);
        }
    }

    /// <summary>
    /// Copies a stored file into the zip as it is (pictures and the like are compressed already). False when it cannot be read
    /// whole and as stored: the store does not have it, it cannot be opened or read (permissions, a bad disk), or its bytes are
    /// not the ones its hash names. The file is read through once before its entry is started, because an entry that is begun
    /// cannot be taken back: a file that fails half way would otherwise fail every export of the library. That costs a second
    /// read of each file, which the disk's cache mostly serves.
    /// </summary>
    private async Task<bool> AddFileAsync(ZipArchive archive, PlannedFile file, CancellationToken cancellationToken)
    {
        if (!await IsReadableAsync(file, cancellationToken))
        {
            return false;
        }

        var source = await store.OpenReadAsync(file.File.Sha256, cancellationToken)
            ?? throw new IOException($"The stored file {file.File.Sha256} went away during the export.");
        await using (source.ConfigureAwait(false))
        {
            var entry = archive.CreateEntry(file.Path, CompressionLevel.NoCompression);
            entry.LastWriteTime = Clamp(file.File.CreatedAt);
            var destination = await entry.OpenAsync(cancellationToken);
            await using (destination.ConfigureAwait(false))
            {
                // By chunk and asynchronously: a file of any size passes through, and none of it is held.
                await source.CopyToAsync(destination, 81920, cancellationToken);
            }
        }

        return true;
    }

    private async Task<bool> IsReadableAsync(PlannedFile file, CancellationToken cancellationToken)
    {
        try
        {
            var source = await store.OpenReadAsync(file.File.Sha256, cancellationToken);
            if (source is null)
            {
                return false;
            }

            await using (source.ConfigureAwait(false))
            {
                var hash = await SHA256.HashDataAsync(source, cancellationToken);
                if (Convert.ToHexStringLower(hash) == file.File.Sha256)
                {
                    return true;
                }

                logger.LogWarning("Export: the stored file {Sha256} does not have the content its hash names.", file.File.Sha256);
                return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Export: the stored file {Sha256} could not be read.", file.File.Sha256);
            return false;
        }
    }

    /// <summary>A zip keeps the time to the nearest two seconds, from 1980 to 2107.</summary>
    private static DateTimeOffset Clamp(DateTimeOffset value)
    {
        var min = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var max = new DateTimeOffset(2107, 12, 31, 0, 0, 0, TimeSpan.Zero);
        return value < min ? min : value > max ? max : value;
    }

    /// <summary>The pages of <c>live_pages</c>, the same function the tree and search read: not the trash, nor anything below it.</summary>
    private async Task<IReadOnlyList<ExportPage>> LoadPagesAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var rows = await db.Pages
            .FromSql($"SELECT p.* FROM live_pages({workspaceId}) live JOIN pages p ON p.id = live.page_id")
            .AsNoTracking()
            .Select(p => new { p.Id, p.ParentId, p.Title, p.SortKey, p.CreatedAt, p.UpdatedAt, p.IsTodo, p.CompletedAt, p.DueAt })
            .ToListAsync(cancellationToken);
        var tags = (await (
                from link in db.PageTags.AsNoTracking()
                join tag in db.Tags.AsNoTracking() on link.TagId equals tag.Id
                where tag.WorkspaceId == workspaceId
                select new { link.PageId, tag.Name })
            .ToListAsync(cancellationToken))
            .ToLookup(t => t.PageId, t => t.Name);

        return rows
            .Select(p => new ExportPage(
                p.Id, p.ParentId, p.Title, p.SortKey, p.CreatedAt, p.UpdatedAt, p.IsTodo, p.CompletedAt, p.DueAt,
                [.. tags[p.Id].Order(StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal)]))
            .ToList();
    }

    /// <summary>
    /// The attachments of pages of this workspace, trash included: whoever reads the workspace may read them. An id of any other
    /// workspace is not among them, so a page that names one cannot make the export carry its file.
    /// </summary>
    private async Task<IReadOnlyList<ExportFile>> LoadFilesAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        (await db.Attachments.AsNoTracking()
            .Where(a => db.Pages.Any(p => p.Id == a.PageId && p.WorkspaceId == workspaceId))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.FileName, a.Sha256, a.Size, a.CreatedAt })
            .ToListAsync(cancellationToken))
        .Select(a => new ExportFile(a.Id, a.FileName, a.Sha256, a.Size, a.CreatedAt))
        .ToList();
}
