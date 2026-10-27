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

    private const string ConflictSuffix = " (conflict)";

    /// <summary>How often progress is written to the database.</summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

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

        /// <summary>What an earlier import made: the id in the export to the kind and the id here.</summary>
        public Dictionary<string, (string Kind, Guid InkpactId)> Existing { get; } = new(StringComparer.Ordinal);

        /// <summary>The page of each notebook and note that exists, from before or made now.</summary>
        public Dictionary<string, Guid> PageOf { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Guid> TagOf { get; } = new(StringComparer.Ordinal);

        /// <summary>The keys still to give to the children of a notebook, in order.</summary>
        public Dictionary<string, Queue<string>> Keys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ResourcesCounted { get; } = new(StringComparer.Ordinal);

        public int Total { get; set; }

        public int Processed { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? SidecarDownSince { get; set; }

        public DateTimeOffset LastFlush { get; set; }

        public int EntriesFlushed { get; set; } = -1;
    }

    /// <summary>
    /// Runs the import with this id from start to end and leaves its row in a final state. Returns when it is done;
    /// throws only when <paramref name="cancellationToken"/> is cancelled (the app is stopping), which leaves the row as
    /// it was: the next start of the app marks it as interrupted.
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

        await FinishAsync(run, error);
        DeleteFile(run.Path);
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

    private async Task LoadExistingAsync(Run run, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var items = await db.ImportItems.AsNoTracking()
            .Where(i => i.WorkspaceId == run.WorkspaceId && i.SourceType == SourceType)
            .Select(i => new { i.ExternalId, i.Kind, i.InkpactId })
            .ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            run.Existing[item.ExternalId] = (item.Kind, item.InkpactId);
            if (item.Kind == "tag")
            {
                run.TagOf[item.ExternalId] = item.InkpactId;
            }
            else
            {
                run.PageOf[item.ExternalId] = item.InkpactId;
            }
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

    // ---- tags ----------------------------------------------------------------------------------------------------------

    private async Task ImportTagAsync(Run run, JexTag tag, CancellationToken cancellationToken)
    {
        if (tag.Encrypted)
        {
            run.Counts.Skipped++;
            AddEntry(run, new ReportEntry("tag", tag.Id, ShortTitle(tag.Title), ReportStatus.Skipped, [new ConversionNote(NoteCodes.Encrypted)]));
            return;
        }

        if (run.Existing.ContainsKey(tag.Id))
        {
            run.Counts.AlreadyImported++;
            return;
        }

        var bytes = await ReadItemAsync(run, tag.Offset, tag.Length, cancellationToken);
        var name = ContentRules.CleanText(tag.Title).Replace('\t', ' ').Trim();
        name = name.Length == 0 ? "-" : name[..Math.Min(name.Length, 200)];
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
        if (run.Existing.ContainsKey(folder.Id))
        {
            run.Counts.AlreadyImported++;
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
            SortKey = await PeekKeyAsync(run, db, step.ParentId, parentPage, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
            ExternalSourceType = SourceType,
            ExternalId = folder.Id,
        };
        db.Pages.Add(page);
        db.ImportItems.Add(NewItem(run, "notebook", folder.Id, page.Id, bytes, stateVector: null));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        TakeKey(run, step.ParentId);
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
        if (run.Existing.ContainsKey(note.Id))
        {
            run.Counts.AlreadyImported++;
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
                await ImportNoteOnceAsync(run, step, note, item.Body, bytes, parentPage, cancellationToken);
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

    private async Task ImportNoteOnceAsync(
        Run run, ImportStep step, JexNote note, string body, byte[] itemBytes, Guid? parentPage, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var attachments = scope.ServiceProvider.GetRequiredService<AttachmentService>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(run, db, cancellationToken);

        var reasons = new ConversionNotes();
        var title = CleanTitle(note.Title, reasons);
        if (note.Conflict)
        {
            title = ContentRules.Limit(title + ConflictSuffix, TreeService.MaxTitleLength, reasons, "title");
        }

        var now = time.GetUtcNow();
        var created = note.Created ?? now;
        var page = new Page
        {
            WorkspaceId = run.WorkspaceId,
            ParentId = parentPage,
            Title = title,
            SortKey = await PeekKeyAsync(run, db, step.ParentId, parentPage, cancellationToken),
            CreatedAt = created,
            UpdatedAt = note.Updated ?? created,
            IsTodo = note.IsTodo,
            CompletedAt = note.IsTodo ? note.Completed : null,
            DueAt = note.IsTodo ? note.Due : null,
            ExternalSourceType = SourceType,
            ExternalId = note.Id,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync(cancellationToken);

        // The page's own copy of each file the note uses, made before the note is converted: the conversion only ever
        // refers to a file through these.
        var resources = new NoteResources();
        var newFiles = new List<(string ResourceId, bool Inline)>();
        foreach (var id in ResourceReference().Matches(body).Select(m => m.Groups["id"].Value).Distinct(StringComparer.Ordinal))
        {
            if (!run.Index.Resources.TryGetValue(id, out var resource) || resource.DataOffset < 0)
            {
                continue;
            }

            var added = await AddResourceAsync(run, attachments, page.Id, resource, reasons, cancellationToken);
            if (added is not null)
            {
                resources.Found[id] = new ImportedResource(added.Id, added.FileName, added.Size, added.Inline);
                newFiles.Add((id, added.Inline));
            }
        }

        var converted = JoplinContent.Convert(body, note.IsHtml, resources);
        var update = await yjs.BuildAsync(converted.Document.ToUtf8Json(), cancellationToken);
        var compaction = await yjs.CompactAsync([update], cancellationToken);
        if (!compaction.StateFingerprint.AsSpan().SequenceEqual(compaction.ReplayFingerprint))
        {
            throw new InvalidOperationException($"The page built for note {note.Id} does not read back as what was built.");
        }

        page.ContentText = compaction.Text.Replace("\0", "");
        db.PageUpdates.Add(new PageUpdate { PageId = page.Id, UserId = run.UserId, Data = update, CreatedAt = now });

        foreach (var tagId in run.Index.TagsOfNote.GetValueOrDefault(note.Id, []).Distinct(StringComparer.Ordinal))
        {
            if (run.TagOf.TryGetValue(tagId, out var tag))
            {
                db.PageTags.Add(new PageTag { PageId = page.Id, TagId = tag });
            }
        }

        // To-dos with a due date get a reminder for the person who imports; Joplin's alarms are not in an export.
        if (note.IsTodo && note.Due is { } due && run.UserId is { } userId)
        {
            db.Reminders.Add(new Reminder { PageId = page.Id, UserId = userId, RemindAt = due, CreatedAt = now });
        }

        db.ImportItems.Add(NewItem(run, "note", note.Id, page.Id, itemBytes, compaction.StateVector));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Only now, when the note is in: what the retry of a note that did not get this far must not count twice.
        TakeKey(run, step.ParentId);
        run.PageOf[note.Id] = page.Id;
        run.Existing[note.Id] = ("note", page.Id);
        run.Counts.Notes++;
        run.Counts.Todos += note.IsTodo ? 1 : 0;
        foreach (var (resourceId, inline) in newFiles.Where(f => run.ResourcesCounted.Add(f.ResourceId)))
        {
            if (inline)
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
            AddEntry(run, new ReportEntry("note", note.Id, ShortTitle(title), ReportStatus.Conflict, reasons.ToList(), page.Id));
        }
        else if (!reasons.IsEmpty)
        {
            AddEntry(run, new ReportEntry("note", note.Id, ShortTitle(title), ReportStatus.Imperfect, reasons.ToList(), page.Id));
        }
    }

    /// <summary>The page's copy of a file of the export. Null, with the reason in <paramref name="reasons"/>, when it cannot be one.</summary>
    private async Task<AttachmentDto?> AddResourceAsync(
        Run run, AttachmentService attachments, Guid pageId, JexResource resource, ConversionNotes reasons, CancellationToken cancellationToken)
    {
        var name = ResourceFileName(resource);
        try
        {
            await using var content = JexArchive.OpenSlice(run.Path, resource.DataOffset, resource.DataLength);
            var added = await attachments.AddAsync(pageId, run.UserId, name, content, cancellationToken, SourceType, resource.Id);
            if (added is null)
            {
                reasons.Add(NoteCodes.ResourceEmpty, name);
            }

            return added;
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
    /// The page a step goes under. False, with the item in the report, when its notebook was not imported: it has no place.
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
    /// The key for the next page under a notebook. The children of a notebook get their keys in one go (the notebook is
    /// new, so nobody else is adding to it); at the top level every page takes the key after the last one there is, as a
    /// person's page would, because a person can be adding pages there while the import runs. The key stays on offer until
    /// <see cref="TakeKey"/>, so a page that is retried gets the same one.
    /// </summary>
    private static async Task<string> PeekKeyAsync(Run run, InkpactDbContext db, string? parentExternalId, Guid? parentPage, CancellationToken cancellationToken)
    {
        if (parentExternalId is null)
        {
            return FractionalIndex.KeyBetween(await LastKeyAsync(run, db, null, cancellationToken), null);
        }

        if (!run.Keys.TryGetValue(parentExternalId, out var keys) || keys.Count == 0)
        {
            var count = Math.Max(1, run.Plan.Children[parentExternalId].Count(s => !run.Existing.ContainsKey(s.Id)));
            keys = new Queue<string>(FractionalIndex.KeysBetween(await LastKeyAsync(run, db, parentPage, cancellationToken), null, count));
            run.Keys[parentExternalId] = keys;
        }

        return keys.Peek();
    }

    private static void TakeKey(Run run, string? parentExternalId)
    {
        if (parentExternalId is not null && run.Keys.TryGetValue(parentExternalId, out var keys) && keys.Count > 0)
        {
            keys.Dequeue();
        }
    }

    private static Task<string?> LastKeyAsync(Run run, InkpactDbContext db, Guid? parentPage, CancellationToken cancellationToken) =>
        db.Pages.AsNoTracking()
            .Where(p => p.WorkspaceId == run.WorkspaceId && p.ParentId == parentPage && p.DeletedAt == null)
            .OrderByDescending(p => p.SortKey)
            .Select(p => p.SortKey)
            .FirstOrDefaultAsync(cancellationToken);

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
    private static string ShortTitle(string title)
    {
        var clean = ContentRules.CleanText(title).Replace('\t', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 200 ? clean : clean[..(char.IsHighSurrogate(clean[199]) ? 199 : 200)];
    }

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

    private async Task FlushAsync(Run run, CancellationToken cancellationToken)
    {
        run.LastFlush = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var counts = ImportJson.Write(run.Counts);
        var query = db.Imports.Where(i => i.Id == run.Id);
        if (run.Entries.Count != run.EntriesFlushed)
        {
            var report = ImportJson.Write(run.Entries);
            run.EntriesFlushed = run.Entries.Count;
            await query.ExecuteUpdateAsync(
                set => set.SetProperty(i => i.TotalItems, run.Total).SetProperty(i => i.ProcessedItems, run.Processed)
                    .SetProperty(i => i.Counts, counts).SetProperty(i => i.Report, report),
                cancellationToken);
        }
        else
        {
            await query.ExecuteUpdateAsync(
                set => set.SetProperty(i => i.TotalItems, run.Total).SetProperty(i => i.ProcessedItems, run.Processed)
                    .SetProperty(i => i.Counts, counts),
                cancellationToken);
        }
    }

    private async Task FinishAsync(Run run, string? error)
    {
        // Not cancellable: whatever stopped the job, the row has to say how it ended.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var counts = ImportJson.Write(run.Counts);
        var report = ImportJson.Write(run.Entries);
        var state = error is null ? ImportStates.Completed : ImportStates.Failed;
        var finished = time.GetUtcNow();
        await db.Imports.Where(i => i.Id == run.Id).ExecuteUpdateAsync(
            set => set.SetProperty(i => i.State, state)
                .SetProperty(i => i.ErrorCode, error)
                .SetProperty(i => i.TotalItems, run.Total)
                .SetProperty(i => i.ProcessedItems, run.Processed)
                .SetProperty(i => i.FinishedAt, finished)
                .SetProperty(i => i.Counts, counts)
                .SetProperty(i => i.Report, report));
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
