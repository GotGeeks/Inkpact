using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Inkpact.Api.Attachments;
using Inkpact.Api.Crdt;
using Inkpact.Api.Pages;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

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

        /// <summary>What an earlier import made and is still there: the id in the export to the kind and the id here.</summary>
        public Dictionary<string, (string Kind, Guid InkpactId)> Existing { get; } = new(StringComparer.Ordinal);

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
            .Select(i => new { i.Id, i.ExternalId, i.Kind, i.InkpactId })
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

            run.Existing[item.ExternalId] = (item.Kind, item.InkpactId);
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

        db.ImportItems.Add(NewItem(run, "tag", tag.Id, made.Id, bytes, stateVector: null));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        run.TagOf[tag.Id] = made.Id;
        run.Existing[tag.Id] = ("tag", made.Id);
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
        db.ImportItems.Add(NewItem(run, "notebook", folder.Id, page.Id, bytes, stateVector: null));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        run.PageOf[folder.Id] = page.Id;
        run.Existing[folder.Id] = ("notebook", page.Id);
        run.Counts.Notebooks++;
        if (!reasons.IsEmpty)
        {
            AddEntry(run, new ReportEntry("notebook", folder.Id, ShortTitle(title), ReportStatus.Imperfect, reasons.ToList(), page.Id));
        }
    }

    // ---- notes ---------------------------------------------------------------------------------------------------------

    private async Task ImportNoteAsync(Run run, ImportStep step, CancellationToken cancellationToken)
    {
        var note = run.Index.Notes[step.Id];
        if (AlreadyThere(run, note.Id))
        {
            return;
        }

        if (!TryFindParent(run, step, note.Title, "note", out var parentPage))
        {
            return;
        }

        var bytes = await ReadItemAsync(run, note.Offset, note.Length, cancellationToken);
        var item = JexItem.Parse(Encoding.UTF8.GetString(bytes))
            ?? throw new InvalidDataException($"Note {note.Id} no longer reads as an item.");

        while (true)
        {
            try
            {
                await ImportNoteOnceAsync(run, note, item.Body, bytes, parentPage, cancellationToken);
                run.SidecarDownSince = null;
                return;
            }
            catch (YjsUnavailableException e)
            {
                // Pages are never written without their content: wait for the sidecar, then try this note again.
                await WaitForSidecarAsync(run, e, cancellationToken);
            }
        }
    }

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
        var attachments = scope.ServiceProvider.GetRequiredService<AttachmentService>();

        var reasons = new ConversionNotes();
        var title = CleanTitle(note.Title, reasons);
        var pageId = Guid.CreateVersion7();

        // The page's own copy of each file the note uses, stored before the note is converted: the conversion only ever
        // refers to a file through these. The rows are written with the page, below.
        var resources = new NoteResources();
        var files = new List<Attachment>();
        foreach (var id in ResourceReference().Matches(body).Select(m => m.Groups["id"].Value).Distinct(StringComparer.Ordinal))
        {
            if (!run.Index.Resources.TryGetValue(id, out var resource) || resource.DataOffset < 0)
            {
                continue;
            }

            if (await StoreResourceAsync(run, attachments, pageId, resource, reasons, cancellationToken) is { } file)
            {
                resources.Found[id] = new ImportedResource(file.Id, file.FileName, file.Size, AttachmentFiles.IsInline(file.ContentType));
                files.Add(file);
            }
        }

        var converted = JoplinContent.Convert(body, note.IsHtml, resources);
        var update = await yjs.BuildAsync(converted.Document.ToUtf8Json(), cancellationToken);
        var compaction = await yjs.CompactAsync([update], cancellationToken);
        if (!compaction.StateFingerprint.AsSpan().SequenceEqual(compaction.ReplayFingerprint))
        {
            throw new InvalidOperationException($"The page built for note {note.Id} does not read back as what was built.");
        }

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
            ContentText = compaction.Text.Replace("\0", ""),
        });
        db.Attachments.AddRange(files);
        db.PageUpdates.Add(new PageUpdate { PageId = pageId, UserId = run.UserId, Data = update, CreatedAt = now });

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

        db.ImportItems.Add(NewItem(run, "note", note.Id, pageId, itemBytes, compaction.StateVector));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Only now, when the note is in: what the retry of a note that did not get this far must not count twice.
        run.PageOf[note.Id] = pageId;
        run.Existing[note.Id] = ("note", pageId);
        run.Counts.Notes++;
        run.Counts.Todos += note.IsTodo ? 1 : 0;
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

        // One line for the note, with everything that is to be said about it.
        foreach (var reason in converted.Notes)
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

    private ImportItem NewItem(Run run, string kind, string externalId, Guid inkpactId, byte[] content, byte[]? stateVector) => new()
    {
        WorkspaceId = run.WorkspaceId,
        SourceType = SourceType,
        ExternalId = externalId,
        Kind = kind,
        InkpactId = inkpactId,
        ContentHash = Convert.ToHexStringLower(SHA256.HashData(content)),
        StateVector = stateVector,
        ImportId = run.Id,
        ImportedAt = time.GetUtcNow(),
    };

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
