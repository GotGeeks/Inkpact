using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Inkpact.Api.Attachments;
using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Pages;
using Inkpact.Api.Sync;
using Inkpact.Api.Tags;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Inkpact.Api.Import;

/// <summary>Where an uploaded export file waits for the job.</summary>
public static class ImportFiles
{
    public static string PathOf(ImportOptions options, Guid importId) => Path.Combine(options.Directory, $"{importId:N}.jex");

    /// <summary>
    /// Removes a file, best effort: whatever goes wrong is logged and never stops what comes after. A file that stays is
    /// removed at the next start (<see cref="ImportWorker.RecoverAsync"/>).
    /// </summary>
    public static bool TryDelete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The import file {Path} could not be deleted", path);
            return false;
        }
    }
}

/// <summary>Ends the whole import, as opposed to one item of it.</summary>
internal sealed class ImportAbortedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Carries out one import of a Joplin export (the file is already on disk): reads the archive, makes the tree, and goes
/// through tags, notebooks and notes. Every item is one transaction, so a job that stops leaves whole notes only, and what
/// it did not get to the next import of the same file does: an item that is already there is skipped. Progress and the
/// report are written to the import's row as it goes.
/// </summary>
public sealed partial class ImportRunner(
    IServiceScopeFactory scopes,
    IYjsCodec yjs,
    SyncHub hub,
    PageUpdateStore pages,
    Compactor compactor,
    SyncOptions syncOptions,
    ImportOptions options,
    TimeProvider time,
    ILogger<ImportRunner> logger)
{
    public const string SourceType = "joplin";

    /// <summary>How often progress is written to the database.</summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    /// <summary>How often the end of an import is tried to be written, while the database does not answer.</summary>
    private const int FinishAttempts = 4;

    /// <summary>How often the import's file is tried to be deleted before the end is written, and how long apart.</summary>
    private const int DeleteAttempts = 5;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(200);

    [GeneratedRegex(@":/(?<id>[0-9a-f]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceReference();

    /// <summary>
    /// An item an earlier import made, as the start of a run reads it: what the fast path needs. The rest of its record is
    /// read only for a note that changed.
    /// </summary>
    private sealed class ExistingItem(Guid id, string kind, Guid inkpactId, string contentHash, int converterVersion, bool hasBase)
    {
        public Guid Id { get; } = id;

        public string Kind { get; } = kind;

        public Guid InkpactId { get; } = inkpactId;

        public string ContentHash { get; set; } = contentHash;

        public int ConverterVersion { get; set; } = converterVersion;

        /// <summary>Whether the item has a base: made by run 1.16 or later, or brought up to it. Without one, the hash is of the note's file alone.</summary>
        public bool HasBase { get; set; } = hasBase;

        public static ExistingItem Of(ImportItem row) =>
            new(row.Id, row.Kind, row.InkpactId, row.ContentHash, row.ConverterVersion, row.Base is not null);
    }

    private sealed class Run(Guid id, Guid workspaceId, Guid? userId, string path, string? copyTitle)
    {
        public Guid Id { get; } = id;

        public Guid WorkspaceId { get; } = workspaceId;

        public Guid? UserId { get; } = userId;

        public string Path { get; } = path;

        /// <summary>The title of a conflict copy, with <c>{title}</c> for the note's (see <see cref="ImportRun.CopyTitle"/>).</summary>
        public string? CopyTitle { get; } = copyTitle;

        /// <summary>
        /// Each file of the export this run has stored, as the row it made: another page that uses the same file gets a row
        /// of its own without the bytes being read and stored again. (Stored files are never removed while their rows may
        /// still come: a cleanup of unreferenced files has to leave recent ones alone.)
        /// </summary>
        public Dictionary<string, Attachment> Stored { get; } = new(StringComparer.Ordinal);

        public JexIndex Index { get; set; } = null!;

        public ImportPlan Plan { get; set; } = null!;

        /// <summary>The notes whose notebook is not there, for a look-up per note.</summary>
        public HashSet<string> NoNotebook { get; set; } = [];

        public ImportCounts Counts { get; } = new();

        public List<ReportEntry> Entries { get; } = [];

        /// <summary>
        /// What an earlier import made and is still there, by the id in the export: what the fast path needs of its record,
        /// read once at the start and kept up to date as this import changes it.
        /// </summary>
        public Dictionary<string, ExistingItem> Existing { get; } = new(StringComparer.Ordinal);

        /// <summary>The live page of each notebook and note, from before or made now.</summary>
        public Dictionary<string, Guid> PageOf { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Notebooks and notes whose page an earlier import made and the person has since put in the trash (itself or with
        /// what is above it), and notebooks skipped for being under one. Nothing new is put under them.
        /// </summary>
        public HashSet<string> InTrash { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Guid> TagOf { get; } = new(StringComparer.Ordinal);

        /// <summary>Notes and notebooks of an earlier import that Joplin has in its trash now and whose page is live here: see <see cref="TrashDeletedAsync"/>.</summary>
        public List<DeletedItem> Deleted { get; } = [];

        /// <summary>Notes and notebooks of the export that this import skips (encrypted, in Joplin's trash, too large to read): a link to one gets no page now.</summary>
        public HashSet<string> NotImported { get; set; } = new(StringComparer.Ordinal);

        public HashSet<string> ResourcesCounted { get; } = new(StringComparer.Ordinal);

        public int Total { get; set; }

        public int Processed { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? SidecarDownSince { get; set; }

        public DateTimeOffset LastFlush { get; set; }
    }

    /// <summary>
    /// Runs the import with this id from start to end and leaves its row in a final state. Returns when it is done;
    /// throws only when <paramref name="cancellationToken"/> is cancelled (the app is stopping), which leaves the row as
    /// it was: the next start of the app marks it as interrupted. When the row cannot be written at the end (the database
    /// is gone), it throws too, and the worker gives up on it (<see cref="AbandonAsync"/>).
    /// </summary>
    public async Task RunAsync(Guid importId, CancellationToken cancellationToken)
    {
        var run = await StartAsync(importId, cancellationToken);
        if (run is null)
        {
            return;
        }

        string? error = null;
        try
        {
            await ImportAsync(run, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JexException e)
        {
            error = e.Code;
            logger.LogWarning("Import {ImportId} cannot be read: {Message}", importId, e.Message);
        }
        catch (ImportAbortedException e)
        {
            error = e.Code;
            logger.LogError("Import {ImportId} was stopped: {Code}", importId, e.Code);
        }
        catch (Exception e)
        {
            error = "import_failed";
            logger.LogError(e, "Import {ImportId} failed", importId);
        }

        // The file must be gone before the end is written: the end is what the person and the tests read as "done".
        await DeleteFileAsync(run.Path);
        await FinishAsync(run, error);
    }

    /// <summary>
    /// Gives up on an import whose job ended without a word (the database went away under it): marks it failed if it can,
    /// and removes its file, so it neither blocks the workspace nor fills the disk. Never throws.
    /// </summary>
    public async Task AbandonAsync(Guid importId)
    {
        ImportFiles.TryDelete(ImportFiles.PathOf(options, importId), logger);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            var now = time.GetUtcNow();
            await db.Imports
                .Where(i => i.Id == importId && (i.State == ImportStates.Queued || i.State == ImportStates.Running))
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.State, ImportStates.Failed)
                    .SetProperty(i => i.ErrorCode, "import_failed")
                    .SetProperty(i => i.FinishedAt, now));
        }
        catch (Exception e)
        {
            // The next upload to the workspace finds the row without a job and marks it (ImportEndpoints).
            logger.LogError(e, "Import {ImportId} could not be marked as failed", importId);
        }
    }

    // ---- the job -------------------------------------------------------------------------------------------------------

    private async Task<Run?> StartAsync(Guid importId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var row = await db.Imports.SingleOrDefaultAsync(i => i.Id == importId, cancellationToken);
        if (row is null || row.State != ImportStates.Queued)
        {
            return null;
        }

        row.State = ImportStates.Running;
        row.StartedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return new Run(row.Id, row.WorkspaceId, row.UserId, ImportFiles.PathOf(options, row.Id), row.CopyTitle) { LastFlush = time.GetUtcNow() };
    }

    private async Task ImportAsync(Run run, CancellationToken cancellationToken)
    {
        run.Index = await JexArchive.ReadIndexAsync(run.Path, cancellationToken);
        run.Plan = ImportPlan.Build(run.Index);
        run.NoNotebook = [.. run.Plan.NotesWithoutNotebook];
        run.NotImported = [.. run.Plan.Skipped.Select(s => s.Id), .. run.Index.Oversized.Select(o => o.Id)];
        await LoadExistingAsync(run, cancellationToken);

        run.Total = run.Index.Tags.Count + run.Plan.Steps.Count + run.Plan.Skipped.Count + run.Index.Oversized.Count;
        await FlushAsync(run, cancellationToken);

        ReportSkipped(run);

        foreach (var tag in run.Index.Tags.Values.OrderBy(t => t.Title, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            await GuardedAsync(run, "tag", tag.Id, tag.Title, () => ImportTagAsync(run, tag, cancellationToken), cancellationToken);
        }

        foreach (var step in run.Plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (kind, title) = step.Kind == StepKind.Notebook
                ? ("notebook", run.Index.Folders[step.Id].Title)
                : ("note", run.Index.Notes[step.Id].Title);
            await GuardedAsync(
                run,
                kind,
                step.Id,
                title,
                () => step.Kind == StepKind.Notebook ? ImportNotebookAsync(run, step, cancellationToken) : ImportNoteAsync(run, step, cancellationToken),
                cancellationToken);
        }

        await TrashDeletedAsync(run, cancellationToken);
    }

    /// <summary>
    /// What earlier imports made, as it is now. A page that is live is where new children go; a page in the trash (itself,
    /// or through what is above it) is left alone and gets no new children; an item whose page or tag is gone altogether
    /// is imported again, so its record is removed.
    /// </summary>
    private async Task LoadExistingAsync(Run run, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var items = await db.ImportItems.AsNoTracking()
            .Where(i => i.WorkspaceId == run.WorkspaceId && i.SourceType == SourceType)
            .Select(i => new { i.Id, i.ExternalId, i.Kind, i.InkpactId, i.ContentHash, i.ConverterVersion, HasBase = i.Base != null })
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return;
        }

        var live = (await scope.ServiceProvider.GetRequiredService<TreeService>().GetTreeAsync(run.WorkspaceId, cancellationToken))
            .Pages.Select(p => p.Id).ToHashSet();
        var pageIds = items.Where(i => i.Kind != "tag").Select(i => i.InkpactId).ToList();
        var pages = (await db.Pages.AsNoTracking().Where(p => pageIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(cancellationToken)).ToHashSet();
        var tagIds = items.Where(i => i.Kind == "tag").Select(i => i.InkpactId).ToList();
        var tags = (await db.Tags.AsNoTracking().Where(t => tagIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();

        var gone = new List<Guid>();
        foreach (var item in items)
        {
            if (item.Kind == "tag")
            {
                if (!tags.Contains(item.InkpactId))
                {
                    gone.Add(item.Id);
                    continue;
                }

                run.TagOf[item.ExternalId] = item.InkpactId;
            }
            else if (live.Contains(item.InkpactId))
            {
                run.PageOf[item.ExternalId] = item.InkpactId;
            }
            else if (pages.Contains(item.InkpactId))
            {
                run.InTrash.Add(item.ExternalId);
            }
            else
            {
                gone.Add(item.Id);
                continue;
            }

            run.Existing[item.ExternalId] = new ExistingItem(item.Id, item.Kind, item.InkpactId, item.ContentHash, item.ConverterVersion, item.HasBase);
        }

        if (gone.Count > 0)
        {
            await db.ImportItems.Where(i => gone.Contains(i.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>The items that are left out on purpose, and the files too large to read, said once at the start.</summary>
    private void ReportSkipped(Run run)
    {
        foreach (var (kind, id, title, reason) in run.Plan.Skipped)
        {
            run.Processed++;
            if (reason == SkipReason.Trashed)
            {
                if (run.Existing.ContainsKey(id) && run.PageOf.ContainsKey(id))
                {
                    // An earlier import made it, and it is live here: whether it follows Joplin into the trash is decided after the
                    // rest of the import, when its fields are compared.
                    run.Deleted.Add(new DeletedItem(kind, id, title));
                    continue;
                }

                // Counted, not listed: the trash of a long-used Joplin is hundreds of notes nobody wants a line for.
                run.Counts.Trashed++;
                continue;
            }

            run.Counts.Skipped++;
            AddEntry(run, new ReportEntry(kind == StepKind.Note ? "note" : "notebook", id, ShortTitle(title), ReportStatus.Skipped, [new ConversionNote(NoteCodes.Encrypted)]));
        }

        foreach (var item in run.Index.Oversized)
        {
            run.Processed++;
            run.Counts.Skipped++;
            AddEntry(run, new ReportEntry("note", item.Id, ShortTitle(item.Title), ReportStatus.Skipped, [new ConversionNote(NoteCodes.ItemTooLarge)]));
        }
    }

    /// <summary>
    /// Imports one item and turns what goes wrong into a line of the report, so one bad item never ends the import. An
    /// unexpected error is counted: too many in a row means the cause is not the items (a database that is gone), and the
    /// job stops.
    /// </summary>
    private async Task GuardedAsync(Run run, string kind, string id, string title, Func<Task> import, CancellationToken cancellationToken)
    {
        try
        {
            await import();
            run.ConsecutiveFailures = 0;
        }
        catch (ImportAbortedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NoteTooLargeException)
        {
            // Larger than a file may be: there is nothing to keep it in. The item stays as it was, and says why.
            run.Counts.Skipped++;
            AddEntry(run, new ReportEntry(kind, id, ShortTitle(title), ReportStatus.Skipped, [new ConversionNote(NoteCodes.ItemTooLarge)]));
            run.ConsecutiveFailures = 0;
        }
        catch (YjsRejectedException e)
        {
            // The sidecar answered, so nothing is wrong around the item: it never counts towards stopping the import. Content
            // the editor's schema refused is a fault in the conversion, which a later version fixes; anything else it refused
            // (a page's log that does not decode, a request too large) is this item's own failure.
            logger.LogError(e, "The Yjs sidecar refused {Kind} {ExternalId}", kind, id);
            run.Counts.Failed++;
            var code = e.IsInvalidContent ? NoteCodes.ConversionFailed : NoteCodes.ItemFailed;
            AddEntry(run, new ReportEntry(kind, id, ShortTitle(title), ReportStatus.Failed, [new ConversionNote(code)]));
            run.ConsecutiveFailures = 0;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Importing {Kind} {ExternalId} failed", kind, id);
            run.Counts.Failed++;
            AddEntry(run, new ReportEntry(kind, id, ShortTitle(title), ReportStatus.Failed, [new ConversionNote(NoteCodes.ItemFailed)]));
            if (++run.ConsecutiveFailures >= options.MaxConsecutiveFailures)
            {
                throw new ImportAbortedException("import_failed");
            }
        }

        run.Processed++;
        await MaybeFlushAsync(run, cancellationToken);
    }

    /// <summary>Whether an earlier import made this item and the person has put its page in the trash since: counted, and left there.</summary>
    private static bool LeftInTrash(Run run, string id)
    {
        if (!run.Existing.ContainsKey(id) || !run.InTrash.Contains(id))
        {
            return false;
        }

        run.Counts.AlreadyInTrash++;
        return true;
    }

    // ---- tags ----------------------------------------------------------------------------------------------------------

    private async Task ImportTagAsync(Run run, JexTag tag, CancellationToken cancellationToken)
    {
        if (tag.Encrypted)
        {
            run.Counts.Skipped++;
            AddEntry(run, new ReportEntry("tag", tag.Id, ShortTitle(tag.Title), ReportStatus.Skipped, [new ConversionNote(NoteCodes.Encrypted)]));
            return;
        }

        if (run.Existing.TryGetValue(tag.Id, out var earlier))
        {
            await ReimportTagAsync(run, tag, earlier, cancellationToken);
            return;
        }

        var bytes = await ReadItemAsync(run, tag.Offset, tag.Length, cancellationToken);
        var name = TagNameOf(tag);
        var normalized = TagNames.Normalize(name);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // A tag of the same name that is already there (made by an earlier import, or by hand) is the one to use.
        var existing = await db.Tags.SingleOrDefaultAsync(t => t.WorkspaceId == run.WorkspaceId && t.NormalizedName == normalized, cancellationToken);
        var made = existing ?? new Tag
        {
            WorkspaceId = run.WorkspaceId,
            Name = name,
            NormalizedName = normalized,
            ExternalSourceType = SourceType,
            ExternalId = tag.Id,
            CreatedAt = time.GetUtcNow(),
        };
        if (existing is null)
        {
            db.Tags.Add(made);
        }

        var row = NewItem(run, "tag", tag.Id, made.Id, HashOf(bytes), page: null, new ImportBase { Title = name });
        db.ImportItems.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        run.TagOf[tag.Id] = made.Id;
        run.Existing[tag.Id] = ExistingItem.Of(row);
        run.Counts.Tags++;
    }

    // ---- notebooks -----------------------------------------------------------------------------------------------------

    private async Task ImportNotebookAsync(Run run, ImportStep step, CancellationToken cancellationToken)
    {
        var folder = run.Index.Folders[step.Id];
        if (LeftInTrash(run, folder.Id))
        {
            return;
        }

        if (run.Existing.TryGetValue(folder.Id, out var existing))
        {
            await ReimportNotebookAsync(run, step, folder, existing, cancellationToken);
            return;
        }

        if (!TryFindParent(run, step, folder.Title, "notebook", out var parentPage))
        {
            return;
        }

        var bytes = await ReadItemAsync(run, folder.Offset, folder.Length, cancellationToken);
        var reasons = new ConversionNotes();
        var title = CleanTitle(folder.Title, reasons);
        if (run.Plan.BrokenCycles.Contains(folder.Id))
        {
            reasons.Add(NoteCodes.NotebookCycle);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(run, db, cancellationToken);
        var now = time.GetUtcNow();
        var page = new Page
        {
            Id = await NewPageIdAsync(run, db, folder.Id, cancellationToken),
            WorkspaceId = run.WorkspaceId,
            ParentId = parentPage,
            Title = title,
            SortKey = await NextKeyAsync(run, db, parentPage, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
            ExternalSourceType = SourceType,
            ExternalId = folder.Id,
        };
        db.Pages.Add(page);
        var row = NewItem(run, "notebook", folder.Id, page.Id, HashOf(bytes), page: null, new ImportBase { Title = title, ParentId = folder.ParentId });
        db.ImportItems.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        run.PageOf[folder.Id] = page.Id;
        run.Existing[folder.Id] = ExistingItem.Of(row);
        run.Counts.Notebooks++;
        if (!reasons.IsEmpty)
        {
            AddEntry(run, new ReportEntry("notebook", folder.Id, ShortTitle(title), ReportStatus.Imperfect, reasons.ToList(), page.Id));
        }
    }

    // ---- notes ---------------------------------------------------------------------------------------------------------

    /// <summary>Where the note's title goes in <see cref="ImportRun.CopyTitle"/>.</summary>
    private const string TitlePlaceholder = "{title}";

    /// <summary>The longest <see cref="ImportRun.CopyTitle"/>: a few words around the note's title.</summary>
    public const int MaxCopyTitleLength = 100;

    /// <summary>Whether a conflict copy's title, as a client sends it, can be used: the note's title once, and plain text.</summary>
    public static bool IsCopyTitle(string copyTitle) =>
        copyTitle.Length <= MaxCopyTitleLength
        && copyTitle.IndexOf(TitlePlaceholder, StringComparison.Ordinal) is var at and >= 0
        && copyTitle.IndexOf(TitlePlaceholder, at + 1, StringComparison.Ordinal) < 0
        && ContentRules.CleanText(copyTitle).Replace('\t', ' ').Replace('\n', ' ') == copyTitle;

    /// <summary>The title of a conflict copy: the note's, in the words the uploader's language gives, within a title's length.</summary>
    private static string CopyTitleOf(Run run, string title)
    {
        if (run.CopyTitle is not { } template)
        {
            return title;
        }

        var room = TreeService.MaxTitleLength - (template.Length - TitlePlaceholder.Length);
        return template.Replace(TitlePlaceholder, ContentRules.Cut(title, room), StringComparison.Ordinal).Trim();
    }

    /// <summary>How often a note is compared again because the page changed while its new content was being made.</summary>
    private const int MaxReimportAttempts = 3;

    private async Task ImportNoteAsync(Run run, ImportStep step, CancellationToken cancellationToken)
    {
        var note = run.Index.Notes[step.Id];
        if (LeftInTrash(run, note.Id))
        {
            return;
        }

        if (run.Existing.TryGetValue(note.Id, out var existing))
        {
            await WithSidecarAsync(run, () => ReimportNoteAsync(run, note, existing, cancellationToken), cancellationToken);
            return;
        }

        if (!TryFindParent(run, step, note.Title, "note", out var parentPage))
        {
            return;
        }

        var bytes = await ReadItemAsync(run, note.Offset, note.Length, cancellationToken);
        var item = JexItem.Parse(Encoding.UTF8.GetString(bytes))
            ?? throw new InvalidDataException($"Note {note.Id} no longer reads as an item.");

        await WithSidecarAsync(run, () => ImportNoteOnceAsync(run, note, item.Body, bytes, parentPage, cancellationToken), cancellationToken);
    }

    /// <summary>Pages are never written without their content: when the sidecar is down, waits for it, then tries again.</summary>
    private async Task WithSidecarAsync(Run run, Func<Task> action, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await action();
                run.SidecarDownSince = null;
                return;
            }
            catch (YjsUnavailableException e)
            {
                await WaitForSidecarAsync(run, e, cancellationToken);
            }
        }
    }

    /// <summary>
    /// What was made of one note's content: the document, the page's update, the page right after it (state vector,
    /// fingerprint, text), and the files: rows to add, and earlier rows that give up their external id. For a replacement,
    /// also the page's fingerprint before it.
    /// </summary>
    private sealed record BuiltNote(
        ConvertedContent Converted,
        byte[] Update,
        byte[] StateVector,
        byte[] Fingerprint,
        string Text,
        IReadOnlyList<Attachment> NewFiles,
        IReadOnlyList<Guid> RetiredFiles,
        byte[]? Before = null,
        /// <summary>The note's text when the page keeps it as a file; null when the page holds it as content.</summary>
        string? FileText = null,
        /// <summary>
        /// A replacement of a large page: the page is compacted right after the replacement is written (run 1.22), so a client
        /// that opens it meanwhile is never sent the old content with the new.
        /// </summary>
        bool CompactAtOnce = false);

    /// <summary>
    /// The slow part of writing a note's content, done outside any transaction: the files are stored, the content is
    /// converted, and the sidecar makes the update and checks it by reading the page back. With <paramref name="current"/>
    /// (the page's snapshot and updates) the update replaces the content of that page, otherwise it is a new page's first.
    /// Of the page's files (<paramref name="files"/>), one with the same bytes is reused without storing it again, and one
    /// whose bytes changed is replaced by a new row: the old row stays, since an earlier state of the page may show it, but
    /// gives up its external id.
    /// </summary>
    private async Task<BuiltNote> BuildNoteAsync(
        Run run,
        IServiceScope scope,
        JexNote note,
        string body,
        Guid pageId,
        IReadOnlyList<byte[]>? current,
        IReadOnlyList<Attachment>? files,
        ConversionNotes reasons,
        CancellationToken cancellationToken)
    {
        var attachments = scope.ServiceProvider.GetRequiredService<AttachmentService>();
        var bodyBytes = Encoding.UTF8.GetByteCount(body);

        // A body larger than a file may be cannot be kept even as a file: skipped before any of its files are stored, so none
        // is left in the store without a row.
        if (bodyBytes > options.MaxNoteTextBytes && bodyBytes > scope.ServiceProvider.GetRequiredService<AttachmentOptions>().MaxFileSize)
        {
            throw new NoteTooLargeException();
        }

        // The page's own copy of each file the note uses, stored before the note is converted: the conversion only ever
        // refers to a file through these. The rows are written with the page.
        var resources = new NoteResources(id => PageLinkTo(run, id));
        var added = new List<Attachment>();
        var retired = new List<Guid>();
        foreach (var id in ResourceReference().Matches(body).Select(m => m.Groups["id"].Value).Distinct(StringComparer.Ordinal))
        {
            if (!run.Index.Resources.TryGetValue(id, out var resource) || resource.DataOffset < 0)
            {
                continue;
            }

            var earlier = files?.FirstOrDefault(f => f.ExternalId == id);
            Attachment file;
            if (earlier is not null && await HasBytesAsync(run, resource, earlier, cancellationToken))
            {
                file = earlier;
            }
            else if (await StoreResourceAsync(run, attachments, pageId, resource, reasons, cancellationToken) is { } stored)
            {
                file = stored;
                added.Add(file);
                if (earlier is not null)
                {
                    retired.Add(earlier.Id);
                }
            }
            else
            {
                continue;
            }

            resources.Found[id] = new ImportedResource(file.Id, file.FileName, file.Size, AttachmentFiles.IsInline(file.ContentType));
        }

        // A note too large to be a page's content is the page's file instead. Its text is not converted at all.
        if (bodyBytes > options.MaxNoteTextBytes)
        {
            return await BuildAsFileAsync(run, scope, attachments, note, body, pageId, current, resources, added, retired, cancellationToken);
        }

        var converted = JoplinContent.Convert(body, note.IsHtml, resources);
        return await MakeUpdateAsync(note, converted, current, added, retired, cancellationToken)
            ?? await BuildAsFileAsync(run, scope, attachments, note, body, pageId, current, resources, added, retired, cancellationToken);
    }

    /// <summary>
    /// The sidecar's update for a converted note: a replacement of <paramref name="current"/>, or a new page's first update.
    /// Null when the page it makes is larger than <see cref="ImportOptions.MaxNoteUpdateBytes"/>. For a replacement that is the
    /// update alone. What is stored of the page with the update added may be more than that: such a page is compacted right
    /// after the write (run 1.22), and the compacted page is never larger than its inputs. Until it is compacted, a client that
    /// opens the page is sent all of it, so the stored updates and the new one must fit three quarters of
    /// <c>Sync:MaxQueuedBytes</c>: the reply and the traffic around it fit the queue. The size is looked at before the new page
    /// is read back, which for a large one is the slow part.
    /// </summary>
    private async Task<BuiltNote?> MakeUpdateAsync(
        JexNote note,
        ConvertedContent converted,
        IReadOnlyList<byte[]>? current,
        List<Attachment> added,
        List<Guid> retired,
        CancellationToken cancellationToken)
    {
        var json = converted.Document.ToUtf8Json();
        if (current is not null)
        {
            // The sidecar checks a replacement itself, and answers with the page before and after it.
            var replacement = await yjs.ReplaceAsync(current, json, cancellationToken);
            var together = current.Sum(input => (long)input.Length) + replacement.Update.Length;
            var compactAtOnce = together > options.MaxNoteUpdateBytes;
            if (replacement.Update.Length > options.MaxNoteUpdateBytes || together > MaxOldAndNewBytes)
            {
                return null;
            }

            return new BuiltNote(
                converted, replacement.Update, replacement.StateVector, replacement.Fingerprint, replacement.Text, added, retired, replacement.Before,
                CompactAtOnce: compactAtOnce);
        }

        var update = await yjs.BuildAsync(json, cancellationToken);
        if (update.Length > options.MaxNoteUpdateBytes)
        {
            return null;
        }

        var after = await yjs.CompactAsync([update], cancellationToken);
        if (!after.StateFingerprint.AsSpan().SequenceEqual(after.ReplayFingerprint))
        {
            throw new InvalidOperationException($"The page built for note {note.Id} does not read back as what was built.");
        }

        return new BuiltNote(converted, update, after.StateVector, after.ReplayFingerprint, after.Text, added, retired);
    }

    /// <summary>
    /// The most a page's stored updates and a replacement may be together: three quarters of the sync queue. A client that
    /// opens the page before it is compacted is sent all of it, and the reply and the traffic around it fit the queue.
    /// </summary>
    private long MaxOldAndNewBytes => syncOptions.MaxQueuedBytes / 4 * 3;

    /// <summary>
    /// The name of the file a note is kept in: its title as the page shows it, with what a file name cannot hold (a line
    /// break, a slash, which would cut the name at the folder it seems to name) made a space or a dash.
    /// </summary>
    internal static string BodyFileName(string title, bool isHtml)
    {
        var name = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            name.Append(c switch
            {
                '/' or '\\' => '-',
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }

        var clean = ContentRules.CleanText(name.ToString()).Trim();
        return $"{(clean.Length > 0 ? clean : "note")}{(isHtml ? ".html" : ".md")}";
    }

    /// <summary>
    /// A note too large to open as a page's content (run 1.16d) is kept whole as a file on the page: the body as it came, named
    /// after the note (<c>.md</c>, or <c>.html</c>), and then every file of the export that the note uses, since the body can no
    /// longer show them. The body's file has no external id: it is not one of the export's resources. A body file the page
    /// already has with the same name and bytes (the same note again, or converted again) is used instead of a new one. When
    /// even this page would be too large to open (a note that uses thousands of files), the note is skipped.
    /// </summary>
    private async Task<BuiltNote> BuildAsFileAsync(
        Run run,
        IServiceScope scope,
        AttachmentService attachments,
        JexNote note,
        string body,
        Guid pageId,
        IReadOnlyList<byte[]>? current,
        NoteResources resources,
        List<Attachment> added,
        List<Guid> retired,
        CancellationToken cancellationToken)
    {
        var name = AttachmentFiles.CleanFileName(BodyFileName(note.Title, note.IsHtml));
        var bytes = Encoding.UTF8.GetBytes(body);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var bodyFile = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(
            a => a.PageId == pageId && a.ExternalSourceType == SourceType && a.ExternalId == null && a.FileName == name && a.Sha256 == sha && a.Size == bytes.Length,
            cancellationToken);
        if (bodyFile is null)
        {
            try
            {
                await using var content = new MemoryStream(bytes, writable: false);
                bodyFile = await attachments.StoreAsync(pageId, run.UserId, name, content, cancellationToken)
                    ?? throw new InvalidOperationException($"The body of note {note.Id} is empty and cannot be stored as a file.");
            }
            catch (FileTooLargeException)
            {
                throw new NoteTooLargeException();
            }

            bodyFile.ExternalSourceType = SourceType;
            added.Add(bodyFile);
        }

        var context = new ConversionContext(resources, new ConversionNotes());
        var blocks = new List<PmNode> { PmNode.Element("paragraph", [ConversionContext.File(new ImportedResource(bodyFile.Id, bodyFile.FileName, bodyFile.Size, IsInlineImage: false))]) };
        foreach (var id in resources.Found.Keys.Order(StringComparer.Ordinal))
        {
            if (context.ResourceNode(id, null, out var isBlock) is { } node)
            {
                blocks.Add(isBlock ? node : PmNode.Element("paragraph", [node]));
            }
        }

        var converted = new ConvertedContent(PmNode.Doc(blocks), [new ConversionNote(NoteCodes.StoredAsFile)]);
        var built = await MakeUpdateAsync(note, converted, current, added, retired, cancellationToken)
            ?? throw new NoteTooLargeException();
        return built with { FileText = NoteFileText.Of(body, note.IsHtml) };
    }

    /// <summary>A note that cannot be kept even as a file (its body is larger than a file may be, or the page of files would be too large to open): skipped.</summary>
    private sealed class NoteTooLargeException : Exception;

    /// <summary>
    /// Whether a file of the export has the bytes of the page's earlier row for it: known when this run stored it already,
    /// otherwise hashed from the export, and not stored again either way.
    /// </summary>
    private static async Task<bool> HasBytesAsync(Run run, JexResource resource, Attachment earlier, CancellationToken cancellationToken)
    {
        if (earlier.Size != resource.DataLength)
        {
            return false;
        }

        if (run.Stored.TryGetValue(resource.Id, out var stored))
        {
            return stored.Sha256 == earlier.Sha256;
        }

        await using var content = JexArchive.OpenSlice(run.Path, resource.DataOffset, resource.DataLength);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken)) == earlier.Sha256;
    }

    /// <summary>Counts the files a note brought, once per file of the export.</summary>
    private static void CountFiles(Run run, IEnumerable<Attachment> files)
    {
        foreach (var file in files.Where(f => f.ExternalId is not null && run.ResourcesCounted.Add(f.ExternalId)))
        {
            if (AttachmentFiles.IsInline(file.ContentType))
            {
                run.Counts.Images++;
            }
            else
            {
                run.Counts.Files++;
            }
        }
    }

    /// <summary>The files of the export that a note's text uses, by id, each once.</summary>
    private static List<JexResource> ResourcesOf(Run run, string text) =>
        [.. ResourceReference().Matches(text)
            .Select(m => m.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(id => run.Index.Resources.GetValueOrDefault(id))
            .OfType<JexResource>()];

    /// <summary>
    /// The hash the fast path compares (<c>import_items.content_hash</c>): the note's file, and the version of each file of
    /// the export it uses, since a file whose bytes changed leaves the note's own file as it was, and its tags, which a JEX keeps
    /// as items of their own. A note that uses no files and has no tags hashes as its file alone, as run 1.15 hashed it.
    /// </summary>
    private static string ContentHashOf(Run run, JexNote note, byte[] bytes, string text)
    {
        var resources = ResourcesOf(run, text);
        var tags = TagIdsHere(run, run.Index.TagsOfNote.GetValueOrDefault(note.Id, []));
        if (resources.Count == 0 && tags.Count == 0)
        {
            return HashOf(bytes);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes);
        foreach (var resource in resources)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\n{resource.Id}:{resource.Version}"));
        }

        foreach (var tag in tags)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\ntag:{tag}"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The note's <see cref="ImportBase.ContentKey"/>: its body, whether it is HTML, and the version of each file it uses.</summary>
    private static string ContentKeyOf(Run run, JexNote note, string body)
    {
        var key = new StringBuilder(note.IsHtml ? "html" : "markdown");
        foreach (var resource in ResourcesOf(run, body))
        {
            key.Append('\n').Append(resource.Id).Append(':').Append(resource.Version);
        }

        key.Append("\n\n").Append(body);
        return HashOf(Encoding.UTF8.GetBytes(key.ToString()));
    }

    /// <summary>The base of a note as it is now in the export: what the next import compares against.</summary>
    private static ImportBase BaseOf(Run run, JexNote note, string title, string contentKey) => new()
    {
        Title = title,
        ParentId = note.ParentId,
        ContentKey = contentKey,
        IsTodo = note.IsTodo,
        TodoDue = note.IsTodo ? note.Due?.ToUnixTimeMilliseconds() : null,
        TodoCompleted = note.IsTodo ? note.Completed?.ToUnixTimeMilliseconds() : null,
        TagIds = TagIdsHere(run, run.Index.TagsOfNote.GetValueOrDefault(note.Id, [])),
    };

    /// <summary>
    /// Makes the page of one note. The slow part comes first, outside any transaction: the files are stored, the
    /// content is converted, and the sidecar builds the page's first update. Only then is the workspace locked, for the
    /// key and the rows, so a person's changes to the tree never wait for a large file or a slow sidecar.
    /// </summary>
    private async Task ImportNoteOnceAsync(
        Run run, JexNote note, string body, byte[] itemBytes, Guid? parentPage, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();

        var reasons = new ConversionNotes();
        var title = CleanTitle(note.Title, reasons);
        var pageId = await NewPageIdAsync(run, db, note.Id, cancellationToken);
        var built = await BuildNoteAsync(run, scope, note, body, pageId, null, null, reasons, cancellationToken);
        var files = built.NewFiles;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(run, db, cancellationToken);
        var now = time.GetUtcNow();
        var created = note.Created ?? now;
        db.Pages.Add(new Page
        {
            Id = pageId,
            WorkspaceId = run.WorkspaceId,
            ParentId = parentPage,
            Title = title,
            SortKey = await NextKeyAsync(run, db, parentPage, cancellationToken),
            CreatedAt = created,
            UpdatedAt = note.Updated ?? created,
            IsTodo = note.IsTodo,
            CompletedAt = note.IsTodo ? note.Completed : null,
            DueAt = note.IsTodo ? note.Due : null,
            ExternalSourceType = SourceType,
            ExternalId = note.Id,
            ContentText = built.Text.Replace("\0", ""),
            FileText = built.FileText ?? "",
        });
        db.Attachments.AddRange(files);
        var first = new PageUpdate { PageId = pageId, UserId = run.UserId, Data = built.Update, CreatedAt = now };
        db.PageUpdates.Add(first);

        // Two tags of the export can be one tag here (the same name in another case), and a page has a tag once.
        var tags = run.Index.TagsOfNote.GetValueOrDefault(note.Id, [])
            .Select(tagId => run.TagOf.TryGetValue(tagId, out var tag) ? tag : (Guid?)null)
            .OfType<Guid>()
            .Distinct();
        foreach (var tag in tags)
        {
            db.PageTags.Add(new PageTag { PageId = pageId, TagId = tag });
        }

        // To-dos with a due date get a reminder for the person who imports; Joplin's alarms are not in an export.
        if (note.IsTodo && note.Due is { } due && run.UserId is { } userId)
        {
            db.Reminders.Add(new Reminder { PageId = pageId, UserId = userId, RemindAt = due, CreatedAt = now });
        }

        var row = NewItem(
            run, "note", note.Id, pageId, ContentHashOf(run, note, itemBytes, Encoding.UTF8.GetString(itemBytes)), built, BaseOf(run, note, title, ContentKeyOf(run, note, body)));
        db.ImportItems.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        // The page's update has its number now: nothing after it in the log means nobody wrote to the page since.
        row.PageSeq = first.Seq;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Only now, when the note is in: what the retry of a note that did not get this far must not count twice.
        run.PageOf[note.Id] = pageId;
        run.Existing[note.Id] = ExistingItem.Of(row);
        run.Counts.Notes++;
        run.Counts.Todos += note.IsTodo ? 1 : 0;
        CountFiles(run, files);

        // One line for the note, with everything that is to be said about it.
        foreach (var reason in built.Converted.Notes)
        {
            reasons.Add(reason);
        }

        if (run.NoNotebook.Contains(note.Id))
        {
            reasons.Add(NoteCodes.NotebookMissing);
        }

        if (note.Conflict)
        {
            // Listed, so the person can find both versions; its title stays the one Joplin gave it.
            AddEntry(run, new ReportEntry("note", note.Id, ShortTitle(title), ReportStatus.Conflict, reasons.ToList(), pageId));
        }
        else if (!reasons.IsEmpty)
        {
            AddEntry(run, new ReportEntry("note", note.Id, ShortTitle(title), ReportStatus.Imperfect, reasons.ToList(), pageId));
        }
    }

    // ---- notes imported before ------------------------------------------------------------------------------------------

    /// <summary>What became of a replacement that was ready to be written.</summary>
    private enum Replaced
    {
        Written,

        /// <summary>The page was put in the trash, or deleted, while the replacement was made: it is left as it is.</summary>
        InTrash,

        /// <summary>Someone wrote to the page while the replacement was made: the note has to be compared again.</summary>
        PageChanged,
    }

    /// <summary>
    /// A note that an earlier import made, met again. Its content is compared three ways: what Joplin had when it was last
    /// imported (the content key in the item's base), what Joplin has now, and what the page is now (its fingerprint).
    /// Changed only in Joplin: the page's content is replaced, through the hub so that open editors see it. Changed only
    /// here: left alone. Changed in both: the page here is left alone and Joplin's version becomes a page beside it, so
    /// nothing is lost on either side. The note's title, place, to-do fields and tags are compared first, each on its own
    /// (<see cref="ApplyFieldsAsync"/>).
    /// </summary>
    private async Task ReimportNoteAsync(Run run, JexNote note, ExistingItem existing, CancellationToken cancellationToken)
    {
        var bytes = await ReadItemAsync(run, note.Offset, note.Length, cancellationToken);
        var text = Encoding.UTF8.GetString(bytes);
        var hash = ContentHashOf(run, note, bytes, text);
        var outdated = existing.ConverterVersion < ImportOptions.ConverterVersion;
        if (existing.HasBase && hash == existing.ContentHash && !outdated)
        {
            // Nothing at all changed in Joplin, neither the note nor a file it uses: not even parsed, so a library that did
            // not change is quick.
            run.Counts.AlreadyImported++;
            return;
        }

        var item = JexItem.Parse(text) ?? throw new InvalidDataException($"Note {note.Id} no longer reads as an item.");
        var contentKey = ContentKeyOf(run, note, item.Body);

        // An item of run 1.15 has no base, and its hash is of the note's file alone (it kept nothing of the files the note
        // uses, nor of its tags). When that file did not change, run 1.15 imported this very version: what the file holds is
        // the base, except for the tags, which are not in it. A file of the note whose bytes changed since cannot be told.
        var fileHash = HashOf(bytes);
        var row = await ReadItemRowAsync(existing.Id, cancellationToken);
        var baseInfo = ImportBase.FromJson(row.Base);
        if (!existing.HasBase && fileHash == existing.ContentHash && !outdated)
        {
            baseInfo = BaseOf(run, note, CleanTitle(note.Title, new ConversionNotes()), contentKey) with { TagIds = null };
        }

        var changedInJoplin = baseInfo?.ContentKey is { } known ? known != contentKey : fileHash != existing.ContentHash;
        var contentToDo = changedInJoplin || outdated;

        // The fields first: title, place, to-do fields and tags. The hash moves only when nothing more is to be done to the
        // item, since it is what lets the next import skip it: with the content still to be written, the content step moves it.
        var fields = await ApplyFieldsAsync(
            run, existing, FieldsOf(run, note), baseInfo, contentToDo ? null : contentKey, contentToDo ? null : hash, cancellationToken);
        if (fields.InTrash)
        {
            run.Counts.AlreadyInTrash++;
            return;
        }

        existing.HasBase = true;
        baseInfo = fields.Base;
        if (!contentToDo)
        {
            if (!fields.Retry)
            {
                existing.ContentHash = hash;
            }

            ReportFields(run, "note", note.Id, note.Title, existing.InkpactId, fields);
            return;
        }

        // A move that was refused leaves the old hash, so the item is looked at again next time.
        var hashToStore = fields.Retry ? existing.ContentHash : hash;

        try
        {
            await ReplaceContentAsync(run, note, item.Body, existing, row, fields, baseInfo, contentKey, hashToStore, changedInJoplin, cancellationToken);
        }
        catch (NoteTooLargeException)
        {
            // Too large to keep even as a file: the page's content stays, what the field step took stays taken, and the line
            // says both. The hash does not move, so the next import tries the content again.
            if (fields.Taken)
            {
                run.Counts.Updated++;
            }
            else
            {
                run.Counts.Skipped++;
            }

            AddEntry(
                run,
                new ReportEntry("note", note.Id, ShortTitle(note.Title), fields.Taken ? ReportStatus.Updated : ReportStatus.Skipped, [.. fields.Reasons, new ConversionNote(NoteCodes.ItemTooLarge)], existing.InkpactId));
        }
    }

    /// <summary>
    /// The content step of a note imported before: compared with the page, then replaced, or a conflict copy made, or left as
    /// it is. Tried again when someone writes to the page meanwhile.
    /// </summary>
    private async Task ReplaceContentAsync(
        Run run,
        JexNote note,
        string body,
        ExistingItem existing,
        ImportItem row,
        FieldResult fields,
        ImportBase? baseInfo,
        string contentKey,
        string hashToStore,
        bool changedInJoplin,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var content = await pages.LoadAsync(existing.InkpactId, cancellationToken);

            // A page whose stored updates are more than a page may be (its last compaction could not run, or clients wrote a lot
            // since) is compacted first, so the replacement is measured against the page and not against its history.
            if (content.Updates.Count > 0 && content.Inputs.Sum(input => (long)input.Length) > options.MaxNoteUpdateBytes)
            {
                await CompactNowAsync(existing.InkpactId, cancellationToken);
                content = await pages.LoadAsync(existing.InkpactId, cancellationToken);
            }

            // Nothing in the page's log after the import's own update: nobody wrote to it, which needs no look at the page.
            // Otherwise the page is read for its fingerprint.
            var changedHere = row.PageSeq is not { } seq || seq != content.LastSeq
                ? ChangedHere(row, await yjs.FingerprintAsync(content.Inputs, cancellationToken))
                : false;

            var reasons = new ConversionNotes();
            foreach (var reason in fields.Reasons)
            {
                reasons.Add(reason);
            }

            BuiltNote? replacement = null;
            if (!changedHere)
            {
                replacement = await PrepareReplacementAsync(run, scope, note, body, existing, content, reasons, cancellationToken);
                // The page as the replacement found it, from the same replay: what the log said is checked once more.
                changedHere = ChangedHere(row, replacement.Before!);
            }

            if (changedInJoplin && changedHere)
            {
                await MakeConflictCopyAsync(run, note, body, hashToStore, contentKey, existing, baseInfo, cancellationToken);
                return;
            }

            if (changedHere)
            {
                // Only the converter is newer, and the page is not the import's any more. Its version is noted, so it is not
                // looked at again.
                await UpdateItemAsync(existing.Id, set => set.SetProperty(i => i.ConverterVersion, ImportOptions.ConverterVersion), cancellationToken);
                existing.ConverterVersion = ImportOptions.ConverterVersion;
                ReportFields(run, "note", note.Id, note.Title, existing.InkpactId, fields, contentKept: true);
                return;
            }

            var replaced = await WriteReplacementAsync(
                run, scope, note, replacement!, reasons, hashToStore, contentKey, existing, baseInfo, content, changedInJoplin, cancellationToken);
            if (replaced != Replaced.PageChanged)
            {
                return;
            }

            if (attempt == MaxReimportAttempts)
            {
                throw new InvalidOperationException($"The page of note {note.Id} kept changing while the import replaced its content.");
            }

            // Compared again with the page as it is now; the item's record has not changed.
        }
    }

    /// <summary>The whole record of an item, read when its note has changed.</summary>
    private async Task<ImportItem> ReadItemRowAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        return await db.ImportItems.AsNoTracking().SingleAsync(i => i.Id == itemId, cancellationToken);
    }

    private async Task UpdateItemAsync(
        Guid itemId,
        Action<UpdateSettersBuilder<ImportItem>> set,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        await db.ImportItems.Where(i => i.Id == itemId).ExecuteUpdateAsync(set, cancellationToken);
    }

    /// <summary>
    /// Whether the page has been written to since the import, from its fingerprint as it is now (the state vector and the
    /// delete set): it differs from the one stored. A deletion alone moves no state vector, so for an item without a
    /// fingerprint (run 1.15) the state vector is all there is, and a page where someone only deleted text counts as
    /// unchanged. Anything that cannot be told counts as changed: a conflict copy is the worse outcome only for noise, an
    /// overwrite would lose text.
    /// </summary>
    private static bool ChangedHere(ImportItem item, byte[] current)
    {
        if (item.PageFingerprint is { } fingerprint)
        {
            return !HashBytes(current).AsSpan().SequenceEqual(fingerprint);
        }

        return item.StateVector is null
            || StateVectors.OfFingerprint(current) is not { } stateVector
            || StateVectors.HasGonePast(stateVector, item.StateVector);
    }

    /// <summary>The most compactions in a row for one page (each folds up to <c>Compaction:MaxUpdatesPerCompaction</c> updates).</summary>
    private const int MaxCompactionRounds = 5;

    /// <summary>
    /// Compacts a page now, until nothing is live, with the compactor's own guarantees (it is called as the worker calls it):
    /// a page whose content was just replaced, or one whose stored updates are too large to replace. It never fails the
    /// import, and never waits: a page it does not compact (the worker has it at this moment, the sidecar is down, the merge
    /// did not verify) is compacted by the worker, which takes a page with a large log at its next round
    /// (<c>Compaction:LiveBytesThreshold</c>). Run 1.22.
    /// </summary>
    private async Task CompactNowAsync(Guid pageId, CancellationToken cancellationToken)
    {
        try
        {
            for (var round = 0; round < MaxCompactionRounds; round++)
            {
                switch (await compactor.CompactAsync(pageId, cancellationToken))
                {
                    case CompactionResult.NothingToDo:
                        return;
                    case CompactionResult.Compacted:
                        break;
                    case CompactionResult.Skipped:
                        // The worker is compacting the page right now.
                        return;
                    default:
                        logger.LogWarning(
                            "Page {PageId} was not compacted by the import; the compaction worker does it later", pageId);
                        return;
                }
            }

            logger.LogWarning("Page {PageId} still has live updates after {Rounds} compactions; the compaction worker does the rest", pageId, MaxCompactionRounds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Compacting page {PageId} for the import failed; the compaction worker does it later", pageId);
        }
    }

    /// <summary>The page's new content, made from <paramref name="content"/> (its files stored, the update made), not yet written.</summary>
    private async Task<BuiltNote> PrepareReplacementAsync(
        Run run,
        IServiceScope scope,
        JexNote note,
        string body,
        ExistingItem existing,
        PageContent content,
        ConversionNotes reasons,
        CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var files = await db.Attachments.AsNoTracking()
            .Where(a => a.PageId == existing.InkpactId && a.ExternalSourceType == SourceType && a.ExternalId != null)
            .ToListAsync(cancellationToken);
        return await BuildNoteAsync(run, scope, note, body, existing.InkpactId, content.Inputs, files, reasons, cancellationToken);
    }

    /// <summary>
    /// Writes the new content of a page that only Joplin changed (or that a newer converter converts again). It was made from
    /// <paramref name="content"/>, and is written only while the page still is exactly that: the hub holds the page's lock,
    /// so no client's update can be stored meanwhile, and one stored since the page was read means the replacement would
    /// delete what was typed into the old blocks. Then nothing is written, and the note is compared again.
    /// </summary>
    private async Task<Replaced> WriteReplacementAsync(
        Run run,
        IServiceScope scope,
        JexNote note,
        BuiltNote built,
        ConversionNotes reasons,
        string hash,
        string contentKey,
        ExistingItem existing,
        ImportBase? baseInfo,
        PageContent content,
        bool changedInJoplin,
        CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var tree = scope.ServiceProvider.GetRequiredService<TreeService>();
        var pageId = existing.InkpactId;
        var next = ((baseInfo ?? new ImportBase()) with { ContentKey = contentKey }).ToJson();
        var fingerprint = HashBytes(built.Fingerprint);
        var text = built.Text.Replace("\0", "");
        var fileText = built.FileText ?? "";
        var now = time.GetUtcNow();
        var updatedAt = note.Updated ?? now;
        Guid[] retired = [.. built.RetiredFiles];

        // Why the commit wrote nothing; whether it wrote is what the hub answers.
        var refused = Replaced.Written;
        var written = await hub.PublishAsync(pageId, built.Update, async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            // The tree's own lock, so the page cannot be put in the trash between this look and the write.
            await LockAsync(run, db, token);
            if (!await tree.IsLiveAsync(pageId, token))
            {
                refused = Replaced.InTrash;
                return false;
            }

            // A client's update stored meanwhile is still in the log, or the compactor has folded it into the snapshot.
            if (await db.PageUpdates.AnyAsync(u => u.PageId == pageId && u.Seq > content.LastSeq, token)
                || await db.PageSnapshots.AnyAsync(s => s.PageId == pageId && s.Watermark > content.LastSeq, token))
            {
                refused = Replaced.PageChanged;
                return false;
            }

            if (retired.Length > 0)
            {
                // Before the new rows: a page has a file of the other app once.
                await db.Attachments.Where(a => retired.Contains(a.Id)).ExecuteUpdateAsync(
                    set => set.SetProperty(a => a.ExternalId, (string?)null), token);
            }

            db.Attachments.AddRange(built.NewFiles);
            var update = new PageUpdate { PageId = pageId, UserId = run.UserId, Data = built.Update, CreatedAt = now };
            db.PageUpdates.Add(update);
            await db.SaveChangesAsync(token);
            var seq = update.Seq;

            await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(
                // Joplin's time, but never back: the field step may have set the time of the import, or a person a later one.
                set => set.SetProperty(p => p.ContentText, text)
                    .SetProperty(p => p.FileText, fileText)
                    .SetProperty(p => p.UpdatedAt, p => p.UpdatedAt > updatedAt ? p.UpdatedAt : updatedAt), token);
            await db.ImportItems.Where(i => i.Id == existing.Id).ExecuteUpdateAsync(
                set => set.SetProperty(i => i.ContentHash, hash)
                    .SetProperty(i => i.Base, next)
                    .SetProperty(i => i.ConverterVersion, ImportOptions.ConverterVersion)
                    .SetProperty(i => i.StateVector, built.StateVector)
                    .SetProperty(i => i.PageFingerprint, fingerprint)
                    .SetProperty(i => i.PageSeq, seq)
                    .SetProperty(i => i.ImportId, run.Id)
                    .SetProperty(i => i.ImportedAt, now),
                token);
            await transaction.CommitAsync(token);
            return true;
        }, cancellationToken);

        if (!written)
        {
            if (refused == Replaced.InTrash)
            {
                run.Counts.AlreadyInTrash++;
            }

            return refused == Replaced.Written
                ? throw new InvalidOperationException($"The new content of note {note.Id} was not written, and nothing said why.")
                : refused;
        }

        existing.ContentHash = hash;
        existing.ConverterVersion = ImportOptions.ConverterVersion;
        existing.HasBase = true;
        if (changedInJoplin)
        {
            run.Counts.Updated++;
        }
        else
        {
            run.Counts.Reconverted++;
        }

        CountFiles(run, built.NewFiles);
        foreach (var reason in built.Converted.Notes)
        {
            reasons.Add(reason);
        }

        if (!reasons.IsEmpty)
        {
            // The field step said how the title was cleaned when it took it (it is in the reasons); here it only names the line.
            AddEntry(run, new ReportEntry("note", note.Id, ShortTitle(CleanTitle(note.Title, new ConversionNotes())), ReportStatus.Updated, reasons.ToList(), pageId));
        }

        // After the page's lock is let go, which every client of the page waits for: a compaction takes seconds for a large
        // page. Until it has run, a client that opens the page is sent the old content and the new (MaxOldAndNewBytes).
        if (built.CompactAtOnce)
        {
            await CompactNowAsync(pageId, cancellationToken);
        }

        return Replaced.Written;
    }

    /// <summary>
    /// Joplin's version of a note that changed in both places becomes a page right after the page here, which is not
    /// touched. The copy has Joplin's title with a mark, its to-do fields, no tags and no reminder (it is a second copy of
    /// the note), and is not linked to the Joplin id, so no later import touches it. The item moves on to this version of
    /// the note, so the same export makes no second copy; its page fingerprint stays, so what was done here still counts as
    /// a change the next time.
    /// </summary>
    private async Task MakeConflictCopyAsync(
        Run run,
        JexNote note,
        string body,
        string fileHash,
        string contentKey,
        ExistingItem existing,
        ImportBase? baseInfo,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var tree = scope.ServiceProvider.GetRequiredService<TreeService>();

        var reasons = new ConversionNotes();
        var title = CleanTitle(note.Title, reasons);
        var copyId = Guid.CreateVersion7();
        var built = await BuildNoteAsync(run, scope, note, body, copyId, null, null, reasons, cancellationToken);
        var copyTitle = CopyTitleOf(run, title);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(run, db, cancellationToken);
        var original = await db.Pages.AsNoTracking()
            .Where(p => p.Id == existing.InkpactId)
            .Select(p => new { p.ParentId, p.SortKey })
            .SingleOrDefaultAsync(cancellationToken);
        if (original is null || !await tree.IsLiveAsync(existing.InkpactId, cancellationToken))
        {
            // Put in the trash while this ran, itself or with what is above it: what an earlier import made and the person
            // has trashed is left alone, and nothing new goes beside it.
            run.Counts.AlreadyInTrash++;
            return;
        }

        var below = await tree.NextKeyAfterAsync(run.WorkspaceId, original.ParentId, null, original.SortKey, cancellationToken);
        var now = time.GetUtcNow();
        var created = note.Created ?? now;
        db.Pages.Add(new Page
        {
            Id = copyId,
            WorkspaceId = run.WorkspaceId,
            ParentId = original.ParentId,
            Title = copyTitle,
            SortKey = FractionalIndex.KeyBetween(original.SortKey, below),
            CreatedAt = created,
            UpdatedAt = note.Updated ?? created,
            IsTodo = note.IsTodo,
            CompletedAt = note.IsTodo ? note.Completed : null,
            DueAt = note.IsTodo ? note.Due : null,
            ContentText = built.Text.Replace("\0", ""),
            FileText = built.FileText ?? "",
        });
        db.Attachments.AddRange(built.NewFiles);
        db.PageUpdates.Add(new PageUpdate { PageId = copyId, UserId = run.UserId, Data = built.Update, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);

        var next = ((baseInfo ?? new ImportBase()) with { ContentKey = contentKey }).ToJson();
        await db.ImportItems.Where(i => i.Id == existing.Id).ExecuteUpdateAsync(
            set => set.SetProperty(i => i.ContentHash, fileHash)
                .SetProperty(i => i.Base, next)
                .SetProperty(i => i.ConverterVersion, ImportOptions.ConverterVersion)
                .SetProperty(i => i.ImportId, run.Id)
                .SetProperty(i => i.ImportedAt, now),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        existing.ContentHash = fileHash;
        existing.ConverterVersion = ImportOptions.ConverterVersion;
        existing.HasBase = true;
        run.Counts.Conflicts++;
        CountFiles(run, built.NewFiles);
        foreach (var reason in built.Converted.Notes)
        {
            reasons.Add(reason);
        }

        reasons.Add(NoteCodes.ContentConflict);
        AddEntry(run, new ReportEntry("note", note.Id, ShortTitle(copyTitle), ReportStatus.ConflictCopy, reasons.ToList(), copyId, existing.InkpactId));
    }

    /// <summary>
    /// Stores the bytes of a file of the export and returns the page's row for it, not yet written. Null, with the reason in
    /// <paramref name="reasons"/>, when it cannot be one.
    /// </summary>
    private async Task<Attachment?> StoreResourceAsync(
        Run run, AttachmentService attachments, Guid pageId, JexResource resource, ConversionNotes reasons, CancellationToken cancellationToken)
    {
        if (run.Stored.TryGetValue(resource.Id, out var known))
        {
            // Stored earlier in this run, for another page: the same file, a row of this page's own.
            return new Attachment
            {
                PageId = pageId,
                FileName = known.FileName,
                ContentType = known.ContentType,
                Size = known.Size,
                Sha256 = known.Sha256,
                CreatedAt = time.GetUtcNow(),
                CreatedBy = run.UserId,
                ExternalSourceType = SourceType,
                ExternalId = resource.Id,
            };
        }

        var name = ResourceFileName(resource);
        try
        {
            await using var content = JexArchive.OpenSlice(run.Path, resource.DataOffset, resource.DataLength);
            var file = await attachments.StoreAsync(pageId, run.UserId, name, content, cancellationToken);
            if (file is null)
            {
                reasons.Add(NoteCodes.ResourceEmpty, name);
                return null;
            }

            file.ExternalSourceType = SourceType;
            file.ExternalId = resource.Id;
            run.Stored[resource.Id] = file;
            return file;
        }
        catch (FileTooLargeException)
        {
            reasons.Add(NoteCodes.ResourceTooLarge, name);
            return null;
        }
    }

    /// <summary>A file's name as Joplin shows it: the export has it in the resource's title; the extension is added when the title lacks one.</summary>
    internal static string ResourceFileName(JexResource resource)
    {
        var name = !string.IsNullOrWhiteSpace(resource.FileName) ? resource.FileName : resource.Title;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "file";
        }

        var extension = resource.Extension.Trim().TrimStart('.');
        return extension.Length > 0 && !Path.HasExtension(name) ? $"{name}.{extension}" : name;
    }

    private sealed class NoteResources(Func<string, PageLink?> pages) : IResourceResolver
    {
        public Dictionary<string, ImportedResource> Found { get; } = new(StringComparer.Ordinal);

        public ImportedResource? Find(string joplinResourceId) => Found.GetValueOrDefault(joplinResourceId);

        public PageLink? FindPage(string joplinId) => pages(joplinId);
    }

    /// <summary>
    /// The id of a new page: the one its Joplin id gives it (<see cref="PageIds"/>), so that links written before it was imported
    /// reach it. Should a page already have that id (one made through the API, which refuses such ids, or a collision), the page
    /// gets a new id of its own instead: the links to it then do not work, but the note is not lost.
    /// </summary>
    private async Task<Guid> NewPageIdAsync(Run run, InkpactDbContext db, string joplinId, CancellationToken cancellationToken)
    {
        var id = PageIds.For(run.WorkspaceId, joplinId);
        if (!await db.Pages.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return id;
        }

        logger.LogWarning("The page id of {ExternalId} is taken by another page; it gets a new one", joplinId);
        return Guid.CreateVersion7();
    }

    /// <summary>
    /// The page a link to a Joplin id points to: the page an earlier import made, else the page the note or notebook gets
    /// (<see cref="PageIds"/>). Null for the id of a file or a tag, which is no page. An id the export does not have at all
    /// is taken for a note that a later export brings (the decision of run 1.16c): a file that was deleted in Joplin looks
    /// the same, and its link then never works. <see cref="PageLink.Known"/> only for what this import makes or made before.
    /// </summary>
    private static PageLink? PageLinkTo(Run run, string joplinId)
    {
        if (run.Existing.TryGetValue(joplinId, out var existing))
        {
            return existing.Kind == "tag" ? null : new PageLink(existing.InkpactId, Known: true);
        }

        if (run.Index.Resources.ContainsKey(joplinId) || run.Index.Tags.ContainsKey(joplinId))
        {
            return null;
        }

        // A note that is skipped (encrypted, in Joplin's trash, too large to read) gets no page from this import.
        var imported = (run.Index.Notes.ContainsKey(joplinId) || run.Index.Folders.ContainsKey(joplinId)) && !run.NotImported.Contains(joplinId);
        return new PageLink(PageIds.For(run.WorkspaceId, joplinId), Known: imported);
    }

    // ---- the tree ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The page a step goes under. False, with the item in the report, when it has no place: its notebook is in the trash
    /// here (skipped: restore the notebook and import again), or could not be imported (failed).
    /// </summary>
    private bool TryFindParent(Run run, ImportStep step, string title, string kind, out Guid? parentPage)
    {
        parentPage = null;
        if (step.ParentId is null)
        {
            return true;
        }

        if (run.PageOf.TryGetValue(step.ParentId, out var page))
        {
            parentPage = page;
            return true;
        }

        if (run.InTrash.Contains(step.ParentId))
        {
            // Anything under it would be hidden in the trash too. A notebook skipped here takes its own children along.
            run.InTrash.Add(step.Id);
            run.Counts.Skipped++;
            AddEntry(run, new ReportEntry(kind, step.Id, ShortTitle(title), ReportStatus.Skipped, [new ConversionNote(NoteCodes.NotebookInTrash)]));
            return false;
        }

        run.Counts.Failed++;
        AddEntry(run, new ReportEntry(kind, step.Id, ShortTitle(title), ReportStatus.Failed, [new ConversionNote(NoteCodes.ParentFailed)]));
        return false;
    }

    /// <summary>Takes the lock every tree operation takes, so a person's change to the tree and the import's pages are applied one at a time.</summary>
    private static async Task LockAsync(Run run, InkpactDbContext db, CancellationToken cancellationToken)
    {
        if (!await TreeService.LockWorkspaceAsync(db, run.WorkspaceId, cancellationToken))
        {
            throw new ImportAbortedException("workspace_missing");
        }
    }

    /// <summary>
    /// The key after the last live page under the parent, read under the workspace's lock: the same key a person's new
    /// page would get, and never one that a person has taken in the meantime, even under a notebook the import made.
    /// </summary>
    private static async Task<string> NextKeyAsync(Run run, InkpactDbContext db, Guid? parentPage, CancellationToken cancellationToken)
    {
        var last = await db.Pages.AsNoTracking()
            .Where(p => p.WorkspaceId == run.WorkspaceId && p.ParentId == parentPage && p.DeletedAt == null)
            .OrderByDescending(p => p.SortKey)
            .Select(p => p.SortKey)
            .FirstOrDefaultAsync(cancellationToken);
        return FractionalIndex.KeyBetween(last, null);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static async Task<byte[]> ReadItemAsync(Run run, long offset, long length, CancellationToken cancellationToken) =>
        await JexArchive.ReadItemBytesAsync(run.Path, offset, length, cancellationToken)
            ?? throw new InvalidDataException("The file ended before an item it had listed.");

    /// <summary>The record of an item just imported: the hash of its file, its base, and for a page the state of its document.</summary>
    private ImportItem NewItem(Run run, string kind, string externalId, Guid inkpactId, string contentHash, BuiltNote? page, ImportBase itemBase) => new()
    {
        WorkspaceId = run.WorkspaceId,
        SourceType = SourceType,
        ExternalId = externalId,
        Kind = kind,
        InkpactId = inkpactId,
        ContentHash = contentHash,
        StateVector = page?.StateVector,
        PageFingerprint = page is null ? null : HashBytes(page.Fingerprint),
        Base = itemBase.ToJson(),
        ConverterVersion = ImportOptions.ConverterVersion,
        ImportId = run.Id,
        ImportedAt = time.GetUtcNow(),
    };

    /// <summary>SHA-256 as lower-case hex.</summary>
    private static string HashOf(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] HashBytes(byte[] content) => SHA256.HashData(content);

    /// <summary>A page title: no control characters or line breaks, at most as long as a title may be.</summary>
    private static string CleanTitle(string title, ConversionNotes notes)
    {
        var clean = ContentRules.CleanText(title).Replace('\t', ' ').Replace('\n', ' ').Trim();
        return ContentRules.Limit(clean, TreeService.MaxTitleLength, notes, "title");
    }

    /// <summary>A title for a line of the report: short, since a report can have thousands.</summary>
    private static string ShortTitle(string title) =>
        ContentRules.Cut(ContentRules.CleanText(title).Replace('\t', ' ').Replace('\n', ' ').Trim(), 200);

    private static void AddEntry(Run run, ReportEntry entry)
    {
        if (run.Entries.Count >= ImportJson.MaxReportEntries)
        {
            run.Counts.Unlisted++;
            return;
        }

        run.Entries.Add(entry);
    }

    private async Task WaitForSidecarAsync(Run run, YjsUnavailableException cause, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        run.SidecarDownSince ??= now;
        if (now - run.SidecarDownSince > options.SidecarWait)
        {
            throw new ImportAbortedException("sidecar_unavailable");
        }

        logger.LogWarning("The Yjs sidecar is not answering; the import waits ({Reason})", cause.Message);
        await Task.Delay(options.SidecarRetryDelay, time, cancellationToken);
    }

    // ---- progress ------------------------------------------------------------------------------------------------------

    private async Task MaybeFlushAsync(Run run, CancellationToken cancellationToken)
    {
        if (time.GetUtcNow() - run.LastFlush >= FlushEvery)
        {
            await FlushAsync(run, cancellationToken);
        }
    }

    /// <summary>
    /// Writes how far the job is. The report is not written here: nobody reads it before the import has ended
    /// (<c>GET /api/imports/{id}</c> leaves it out until then), and it can be large.
    /// </summary>
    private async Task FlushAsync(Run run, CancellationToken cancellationToken)
    {
        run.LastFlush = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var counts = ImportJson.Write(run.Counts);
        await db.Imports.Where(i => i.Id == run.Id).ExecuteUpdateAsync(
            set => set.SetProperty(i => i.TotalItems, run.Total).SetProperty(i => i.ProcessedItems, run.Processed).SetProperty(i => i.Counts, counts),
            cancellationToken);
    }

    /// <summary>
    /// Writes how the import ended. Not cancellable: whatever stopped the job, the row has to say so. Tried a few times
    /// while the database does not answer; after that it throws, and the worker gives up on the import.
    /// </summary>
    private async Task FinishAsync(Run run, string? error)
    {
        var counts = ImportJson.Write(run.Counts);
        var report = ImportJson.Write(run.Entries);
        var state = error is null ? ImportStates.Completed : ImportStates.Failed;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
                var finished = time.GetUtcNow();
                await db.Imports.Where(i => i.Id == run.Id).ExecuteUpdateAsync(
                    set => set.SetProperty(i => i.State, state)
                        .SetProperty(i => i.ErrorCode, error)
                        .SetProperty(i => i.TotalItems, run.Total)
                        .SetProperty(i => i.ProcessedItems, run.Processed)
                        .SetProperty(i => i.FinishedAt, finished)
                        .SetProperty(i => i.Counts, counts)
                        .SetProperty(i => i.Report, report));
                return;
            }
            catch (Exception e) when (attempt < FinishAttempts)
            {
                logger.LogWarning(e, "The end of import {ImportId} could not be written (attempt {Attempt})", run.Id, attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt), time);
            }
        }
    }

    /// <summary>
    /// Removes the import's file, trying a few times: on Windows a virus scanner or indexer can hold it for a moment. Never
    /// throws, so the end is always written; a file that still stays is removed at the next start.
    /// </summary>
    private async Task DeleteFileAsync(string path)
    {
        for (var attempt = 1; !ImportFiles.TryDelete(path, logger) && attempt < DeleteAttempts; attempt++)
        {
            await Task.Delay(DeleteRetryDelay, time);
        }
    }
}
