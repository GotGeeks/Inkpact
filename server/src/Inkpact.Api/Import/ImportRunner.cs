using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Inkpact.Api.Attachments;
using Inkpact.Api.Crdt;
using Inkpact.Api.Pages;
using Inkpact.Api.Sync;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Inkpact.Api.Import;

/// <summary>Where an uploaded export file waits for the job.</summary>
public static class ImportFiles
{
    public static string PathOf(ImportOptions options, Guid importId) => Path.Combine(options.Directory, $"{importId:N}.jex");
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
    ImportOptions options,
    TimeProvider time,
    ILogger<ImportRunner> logger)
{
    public const string SourceType = "joplin";

    /// <summary>How often progress is written to the database.</summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    /// <summary>How often the end of an import is tried to be written, while the database does not answer.</summary>
    private const int FinishAttempts = 4;

    [GeneratedRegex(@":/(?<id>[0-9a-f]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceReference();

    private sealed class Run(Guid id, Guid workspaceId, Guid? userId, string path)
    {
        public Guid Id { get; } = id;

        public Guid WorkspaceId { get; } = workspaceId;

        public Guid? UserId { get; } = userId;

        public string Path { get; } = path;

        public JexIndex Index { get; set; } = null!;

        public ImportPlan Plan { get; set; } = null!;

        /// <summary>The notes whose notebook is not there, for a look-up per note.</summary>
        public HashSet<string> NoNotebook { get; set; } = [];

        public ImportCounts Counts { get; } = new();

        public List<ReportEntry> Entries { get; } = [];

        /// <summary>
        /// What an earlier import made and is still there, by the id in the export: its record, read once at the start and
        /// kept up to date as this import changes it.
        /// </summary>
        public Dictionary<string, ImportItem> Existing { get; } = new(StringComparer.Ordinal);

        /// <summary>The live page of each notebook and note, from before or made now.</summary>
        public Dictionary<string, Guid> PageOf { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Notebooks and notes whose page an earlier import made and the person has since put in the trash (itself or with
        /// what is above it), and notebooks skipped for being under one. Nothing new is put under them.
        /// </summary>
        public HashSet<string> InTrash { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Guid> TagOf { get; } = new(StringComparer.Ordinal);

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

        try
        {
            await FinishAsync(run, error);
        }
        finally
        {
            // The file is of no more use, whether or not the end could be written.
            DeleteFile(run.Path);
        }
    }

    /// <summary>
    /// Gives up on an import whose job ended without a word (the database went away under it): marks it failed if it can,
    /// and removes its file, so it neither blocks the workspace nor fills the disk. Never throws.
    /// </summary>
    public async Task AbandonAsync(Guid importId)
    {
        DeleteFile(ImportFiles.PathOf(options, importId));
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
        return new Run(row.Id, row.WorkspaceId, row.UserId, ImportFiles.PathOf(options, row.Id)) { LastFlush = time.GetUtcNow() };
    }

    private async Task ImportAsync(Run run, CancellationToken cancellationToken)
    {
        run.Index = await JexArchive.ReadIndexAsync(run.Path, cancellationToken);
        run.Plan = ImportPlan.Build(run.Index);
        run.NoNotebook = [.. run.Plan.NotesWithoutNotebook];
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

            run.Existing[item.ExternalId] = item;
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
        catch (YjsRejectedException e)
        {
            // The editor's own schema refused what the conversion made: a fault in the conversion, which a later version fixes.
            logger.LogError(e, "The content of {Kind} {ExternalId} was refused by the Yjs builder", kind, id);
            run.Counts.Failed++;
            AddEntry(run, new ReportEntry(kind, id, ShortTitle(title), ReportStatus.Failed, [new ConversionNote(NoteCodes.ConversionFailed)]));
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

    /// <summary>
    /// Whether an earlier import made this item: counted as already imported (or as in the trash, when the person has put
    /// it there), and left alone.
    /// </summary>
    private static bool AlreadyThere(Run run, string id)
    {
        if (!run.Existing.ContainsKey(id))
        {
            return false;
        }

        if (run.InTrash.Contains(id))
        {
            run.Counts.AlreadyInTrash++;
        }
        else
        {
            run.Counts.AlreadyImported++;
        }

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

        if (AlreadyThere(run, tag.Id))
        {
            return;
        }

        var bytes = await ReadItemAsync(run, tag.Offset, tag.Length, cancellationToken);
        var name = ContentRules.Cut(ContentRules.CleanText(tag.Title).Replace('\t', ' ').Trim(), 200);
        name = name.Length == 0 ? "-" : name;
        var normalized = name.ToLowerInvariant();

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

        var row = NewItem(run, "tag", tag.Id, made.Id, bytes, page: null, new ImportBase { Title = name });
        db.ImportItems.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        run.TagOf[tag.Id] = made.Id;
        run.Existing[tag.Id] = row;
        run.Counts.Tags++;
    }

    // ---- notebooks -----------------------------------------------------------------------------------------------------

    private async Task ImportNotebookAsync(Run run, ImportStep step, CancellationToken cancellationToken)
    {
        var folder = run.Index.Folders[step.Id];
        if (AlreadyThere(run, folder.Id))
        {
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
        var row = NewItem(run, "notebook", folder.Id, page.Id, bytes, page: null, new ImportBase { Title = title, ParentId = folder.ParentId });
        db.ImportItems.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        run.PageOf[folder.Id] = page.Id;
        run.Existing[folder.Id] = row;
        run.Counts.Notebooks++;
        if (!reasons.IsEmpty)
        {
            AddEntry(run, new ReportEntry("notebook", folder.Id, ShortTitle(title), ReportStatus.Imperfect, reasons.ToList(), page.Id));
        }
    }

    // ---- notes ---------------------------------------------------------------------------------------------------------

    /// <summary>What a conflict copy's title ends with. A product's name, not a word of a language, so the server may write it.</summary>
    private const string ConflictCopySuffix = " (Joplin)";

    /// <summary>How often a note is compared again because the page changed while its new content was being made.</summary>
    private const int MaxReimportAttempts = 3;

    private async Task ImportNoteAsync(Run run, ImportStep step, CancellationToken cancellationToken)
    {
        var note = run.Index.Notes[step.Id];
        if (run.Existing.TryGetValue(note.Id, out var existing))
        {
            if (run.InTrash.Contains(note.Id))
            {
                run.Counts.AlreadyInTrash++;
                return;
            }

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
    /// fingerprint, text), and the files: rows to add, and earlier rows that give up their external id.
    /// </summary>
    private sealed record BuiltNote(
        ConvertedContent Converted,
        byte[] Update,
        byte[] StateVector,
        byte[] Fingerprint,
        string Text,
        IReadOnlyList<Attachment> NewFiles,
        IReadOnlyList<Guid> RetiredFiles);

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

        // The page's own copy of each file the note uses, stored before the note is converted: the conversion only ever
        // refers to a file through these. The rows are written with the page.
        var resources = new NoteResources();
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

        var converted = JoplinContent.Convert(body, note.IsHtml, resources);
        var json = converted.Document.ToUtf8Json();
        if (current is not null)
        {
            // The sidecar checks a replacement itself, and answers with the page after it.
            var replacement = await yjs.ReplaceAsync(current, json, cancellationToken);
            return new BuiltNote(converted, replacement.Update, replacement.StateVector, replacement.Fingerprint, replacement.Text, added, retired);
        }

        var update = await yjs.BuildAsync(json, cancellationToken);
        var after = await yjs.CompactAsync([update], cancellationToken);
        if (!after.StateFingerprint.AsSpan().SequenceEqual(after.ReplayFingerprint))
        {
            throw new InvalidOperationException($"The page built for note {note.Id} does not read back as what was built.");
        }

        return new BuiltNote(converted, update, after.StateVector, after.ReplayFingerprint, after.Text, added, retired);
    }

    /// <summary>Whether a file of the export has the bytes of the page's earlier row for it: hashed from the export, not stored again.</summary>
    private static async Task<bool> HasBytesAsync(Run run, JexResource resource, Attachment earlier, CancellationToken cancellationToken)
    {
        if (earlier.Size != resource.DataLength)
        {
            return false;
        }

        await using var content = JexArchive.OpenSlice(run.Path, resource.DataOffset, resource.DataLength);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken)) == earlier.Sha256;
    }

    /// <summary>Counts the files a note brought, once per file of the export.</summary>
    private static void CountFiles(Run run, IEnumerable<Attachment> files)
    {
        foreach (var file in files.Where(f => run.ResourcesCounted.Add(f.ExternalId!)))
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

    /// <summary>The base of a note as it is now in the export: what the next import compares against.</summary>
    private static ImportBase BaseOf(Run run, JexNote note, string title, string bodyHash) => new()
    {
        Title = title,
        ParentId = note.ParentId,
        BodyHash = bodyHash,
        IsTodo = note.IsTodo,
        TodoDue = note.IsTodo ? note.Due?.ToUnixTimeMilliseconds() : null,
        TodoCompleted = note.IsTodo ? note.Completed?.ToUnixTimeMilliseconds() : null,
        TagIds = [.. run.Index.TagsOfNote.GetValueOrDefault(note.Id, []).Order(StringComparer.Ordinal)],
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
        var pageId = Guid.CreateVersion7();
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
        });
        db.Attachments.AddRange(files);
        db.PageUpdates.Add(new PageUpdate { PageId = pageId, UserId = run.UserId, Data = built.Update, CreatedAt = now });

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

        var row = NewItem(run, "note", note.Id, pageId, itemBytes, built, BaseOf(run, note, title, HashOf(Encoding.UTF8.GetBytes(body))));
        db.ImportItems.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Only now, when the note is in: what the retry of a note that did not get this far must not count twice.
        run.PageOf[note.Id] = pageId;
        run.Existing[note.Id] = row;
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
    /// imported (the body's hash in the item's base), what Joplin has now, and what the page is now (its fingerprint).
    /// Changed only in Joplin: the page's content is replaced, through the hub so that open editors see it. Changed only
    /// here: left alone. Changed in both: the page here is left alone and Joplin's version becomes a page beside it, so
    /// nothing is lost on either side. The note's title, place, to-do fields and tags are not looked at yet (run 1.16b).
    /// </summary>
    private async Task ReimportNoteAsync(Run run, JexNote note, ImportItem existing, CancellationToken cancellationToken)
    {
        var bytes = await ReadItemAsync(run, note.Offset, note.Length, cancellationToken);
        var hash = HashOf(bytes);
        if (hash == existing.ContentHash && existing.ConverterVersion >= ImportOptions.ConverterVersion)
        {
            // Nothing at all changed in Joplin: not even parsed, so a library that did not change is quick.
            run.Counts.AlreadyImported++;
            return;
        }

        var item = JexItem.Parse(Encoding.UTF8.GetString(bytes))
            ?? throw new InvalidDataException($"Note {note.Id} no longer reads as an item.");
        var bodyHash = HashOf(Encoding.UTF8.GetBytes(item.Body));
        var baseInfo = ImportBase.FromJson(existing.Base);

        // Without a base (an item of run 1.15) the file's hash is all there is: any change counts as a change.
        var changedInJoplin = baseInfo?.BodyHash is { } known ? known != bodyHash : hash != existing.ContentHash;
        var outdated = existing.ConverterVersion < ImportOptions.ConverterVersion;
        if (!changedInJoplin && !outdated)
        {
            // Only Joplin's bookkeeping or the note's fields changed, which this run does not take over (run 1.16b). The
            // file's hash moves on so the next import takes the fast path again; the base keeps the fields as they were.
            await UpdateItemAsync(existing.Id, set => set.SetProperty(i => i.ContentHash, hash), cancellationToken);
            existing.ContentHash = hash;
            run.Counts.AlreadyImported++;
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            var content = await pages.LoadAsync(existing.InkpactId, cancellationToken);
            var changedHere = await ChangedHereAsync(existing, content.Inputs, cancellationToken);

            if (changedInJoplin && changedHere)
            {
                await MakeConflictCopyAsync(run, note, item.Body, hash, bodyHash, existing, baseInfo, cancellationToken);
                return;
            }

            if (changedHere)
            {
                // Only the converter is newer, and the page is not the import's any more. Its version is noted, so it is not
                // looked at again.
                await UpdateItemAsync(existing.Id, set => set.SetProperty(i => i.ConverterVersion, ImportOptions.ConverterVersion), cancellationToken);
                existing.ConverterVersion = ImportOptions.ConverterVersion;
                run.Counts.KeptInInkpact++;
                return;
            }

            var replaced = await ReplaceContentAsync(run, note, item.Body, hash, bodyHash, existing, baseInfo, content, changedInJoplin, cancellationToken);
            if (replaced != Replaced.PageChanged)
            {
                return;
            }

            if (attempt == MaxReimportAttempts)
            {
                throw new InvalidOperationException($"The page of note {note.Id} kept changing while the import replaced its content.");
            }
        }
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
    /// Whether the page has been written to since the import: its fingerprint (the state vector and the delete set) differs
    /// from the one stored. A deletion alone moves no state vector, so for an item without a fingerprint (run 1.15) the
    /// state vector is all there is, and a page where someone only deleted text counts as unchanged. Anything that cannot
    /// be told counts as changed: a conflict copy is the worse outcome only for noise, an overwrite would lose text.
    /// </summary>
    private async Task<bool> ChangedHereAsync(ImportItem item, IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken)
    {
        if (item.PageFingerprint is { } fingerprint)
        {
            return !HashBytes(await yjs.FingerprintAsync(inputs, cancellationToken)).AsSpan().SequenceEqual(fingerprint);
        }

        return item.StateVector is null
            || StateVectors.HasGonePast((await yjs.CompactAsync(inputs, cancellationToken)).StateVector, item.StateVector);
    }

    /// <summary>
    /// Replaces the content of a page that only Joplin changed (or that a newer converter converts again). The update is made
    /// from <paramref name="content"/>, and written only while the page still is exactly that: the hub holds the page's
    /// lock, so no client's update can be stored meanwhile, and one stored since the page was read means the replacement
    /// would delete what was typed into the old blocks. Then nothing is written, and the note is compared again.
    /// </summary>
    private async Task<Replaced> ReplaceContentAsync(
        Run run,
        JexNote note,
        string body,
        string fileHash,
        string bodyHash,
        ImportItem existing,
        ImportBase? baseInfo,
        PageContent content,
        bool changedInJoplin,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var tree = scope.ServiceProvider.GetRequiredService<TreeService>();
        var pageId = existing.InkpactId;

        var reasons = new ConversionNotes();
        var title = CleanTitle(note.Title, reasons);
        var files = await db.Attachments.AsNoTracking()
            .Where(a => a.PageId == pageId && a.ExternalSourceType == SourceType && a.ExternalId != null)
            .ToListAsync(cancellationToken);
        var built = await BuildNoteAsync(run, scope, note, body, pageId, content.Inputs, files, reasons, cancellationToken);

        var next = ((baseInfo ?? new ImportBase()) with { BodyHash = bodyHash }).ToJson();
        var fingerprint = HashBytes(built.Fingerprint);
        var text = built.Text.Replace("\0", "");
        var now = time.GetUtcNow();
        var updatedAt = note.Updated ?? now;
        Guid[] retired = [.. built.RetiredFiles];
        var outcome = Replaced.Written;
        await hub.PublishAsync(pageId, built.Update, async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            // The tree's own lock, so the page cannot be put in the trash between this look and the write.
            await LockAsync(run, db, token);
            if (!await tree.IsLiveAsync(pageId, token))
            {
                outcome = Replaced.InTrash;
                return false;
            }

            if (await db.PageUpdates.AnyAsync(u => u.PageId == pageId && u.Seq > content.LastSeq, token))
            {
                outcome = Replaced.PageChanged;
                return false;
            }

            if (retired.Length > 0)
            {
                // Before the new rows: a page has a file of the other app once.
                await db.Attachments.Where(a => retired.Contains(a.Id)).ExecuteUpdateAsync(
                    set => set.SetProperty(a => a.ExternalId, (string?)null), token);
            }

            db.Attachments.AddRange(built.NewFiles);
            db.PageUpdates.Add(new PageUpdate { PageId = pageId, UserId = run.UserId, Data = built.Update, CreatedAt = now });
            await db.SaveChangesAsync(token);

            await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(
                set => set.SetProperty(p => p.ContentText, text).SetProperty(p => p.UpdatedAt, updatedAt), token);
            await db.ImportItems.Where(i => i.Id == existing.Id).ExecuteUpdateAsync(
                set => set.SetProperty(i => i.ContentHash, fileHash)
                    .SetProperty(i => i.Base, next)
                    .SetProperty(i => i.ConverterVersion, ImportOptions.ConverterVersion)
                    .SetProperty(i => i.StateVector, built.StateVector)
                    .SetProperty(i => i.PageFingerprint, fingerprint)
                    .SetProperty(i => i.ImportId, run.Id)
                    .SetProperty(i => i.ImportedAt, now),
                token);
            await transaction.CommitAsync(token);
            return true;
        }, cancellationToken);

        if (outcome == Replaced.InTrash)
        {
            run.Counts.AlreadyInTrash++;
            return outcome;
        }

        if (outcome == Replaced.PageChanged)
        {
            return outcome;
        }

        existing.ContentHash = fileHash;
        existing.Base = next;
        existing.ConverterVersion = ImportOptions.ConverterVersion;
        existing.StateVector = built.StateVector;
        existing.PageFingerprint = fingerprint;
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
            AddEntry(run, new ReportEntry("note", note.Id, ShortTitle(title), ReportStatus.Updated, reasons.ToList(), pageId));
        }

        return outcome;
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
        string bodyHash,
        ImportItem existing,
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
        var copyTitle = $"{ContentRules.Cut(title, TreeService.MaxTitleLength - ConflictCopySuffix.Length)}{ConflictCopySuffix}".Trim();

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
        });
        db.Attachments.AddRange(built.NewFiles);
        db.PageUpdates.Add(new PageUpdate { PageId = copyId, UserId = run.UserId, Data = built.Update, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);

        var next = ((baseInfo ?? new ImportBase()) with { BodyHash = bodyHash }).ToJson();
        await db.ImportItems.Where(i => i.Id == existing.Id).ExecuteUpdateAsync(
            set => set.SetProperty(i => i.ContentHash, fileHash)
                .SetProperty(i => i.Base, next)
                .SetProperty(i => i.ConverterVersion, ImportOptions.ConverterVersion)
                .SetProperty(i => i.ImportId, run.Id)
                .SetProperty(i => i.ImportedAt, now),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        existing.ContentHash = fileHash;
        existing.Base = next;
        existing.ConverterVersion = ImportOptions.ConverterVersion;
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

    private sealed class NoteResources : IResourceResolver
    {
        public Dictionary<string, ImportedResource> Found { get; } = new(StringComparer.Ordinal);

        public ImportedResource? Find(string joplinResourceId) => Found.GetValueOrDefault(joplinResourceId);
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
    private ImportItem NewItem(Run run, string kind, string externalId, Guid inkpactId, byte[] content, BuiltNote? page, ImportBase itemBase) => new()
    {
        WorkspaceId = run.WorkspaceId,
        SourceType = SourceType,
        ExternalId = externalId,
        Kind = kind,
        InkpactId = inkpactId,
        ContentHash = HashOf(content),
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

    private void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "The import file {Path} could not be deleted", path);
        }
    }
}
