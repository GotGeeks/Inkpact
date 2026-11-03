using Inkpact.Api.Pages;
using Inkpact.Api.Search;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Import;

/// <summary>
/// The fields of an item an earlier import made (run 1.16b): title, place, to-do fields and tags of a note, title and place of
/// a notebook, the name of a tag, and the notes Joplin has put in its trash. Each field follows <see cref="ThreeWay"/>: what
/// Joplin had when it was last imported (the item's base), what it has now, and what is here.
/// </summary>
public sealed partial class ImportRunner
{
    /// <summary>A value taken over from Joplin; a nullable one is a field that is to change (the value itself can be null).</summary>
    private readonly record struct Take<T>(T Value);

    /// <summary>What Joplin has of an item now, for the comparison of its fields.</summary>
    private sealed record JoplinFields(
        string Title,
        string ParentId,
        Guid? Target,
        string? TargetRefusal,
        bool IsNote,
        bool IsTodo,
        long? Due,
        long? Completed,
        IReadOnlyList<string> TagIds);

    /// <summary>What the comparison of an item's fields decided: what is taken, what is kept (and why), and the base to keep.</summary>
    private sealed class FieldPlan
    {
        public Take<string>? Title { get; set; }

        public Take<Guid?>? Parent { get; set; }

        public Take<bool>? IsTodo { get; set; }

        public Take<long?>? Completed { get; set; }

        public Take<long?>? Due { get; set; }

        public HashSet<Guid> AddTags { get; set; } = [];

        public HashSet<Guid> RemoveTags { get; set; } = [];

        public List<ConversionNote> Reasons { get; } = [];

        /// <summary>A move was refused, which may become possible: the base's notebook and the item's hash stay, so the next import tries again.</summary>
        public bool Retry { get; set; }

        public ImportBase Base { get; set; } = new();

        /// <summary>Whether the page itself has to be written.</summary>
        public bool Taken => Title is not null || Parent is not null || IsTodo is not null || Completed is not null || Due is not null
            || AddTags.Count > 0 || RemoveTags.Count > 0;
    }

    private sealed record FieldResult(ImportBase Base, bool Taken, IReadOnlyList<ConversionNote> Reasons, bool Retry, bool InTrash);

    private static long? Milliseconds(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();

    private static DateTimeOffset? FromMilliseconds(long? value) => value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    /// <summary>The page a Joplin notebook has here, or why it has none.</summary>
    private static (Guid? Target, string? Refusal) PlaceOf(Run run, string? notebookId)
    {
        if (string.IsNullOrEmpty(notebookId))
        {
            return (null, null);
        }

        if (run.PageOf.TryGetValue(notebookId, out var page))
        {
            return (page, null);
        }

        return (null, run.InTrash.Contains(notebookId) ? NoteCodes.MoveTargetInTrash : NoteCodes.MoveTargetMissing);
    }

    /// <summary>
    /// Whether a page is still where the base put it: under the page of the base's notebook, or at the top level for none. A
    /// notebook that is in the trash here or gone has no live page under it, so a live page that was in it has been moved here.
    /// </summary>
    private static bool StillUnder(Run run, string baseParentId, Guid? parentHere)
    {
        var (target, refusal) = PlaceOf(run, baseParentId);
        return refusal is null && target == parentHere;
    }

    /// <summary>The fields of a note as Joplin has them now.</summary>
    private JoplinFields FieldsOf(Run run, JexNote note)
    {
        var (target, refusal) = PlaceOf(run, note.ParentId);
        return new JoplinFields(
            note.Title,
            note.ParentId,
            target,
            refusal,
            IsNote: true,
            note.IsTodo,
            note.IsTodo ? Milliseconds(note.Due) : null,
            note.IsTodo ? Milliseconds(note.Completed) : null,
            run.Index.TagsOfNote.GetValueOrDefault(note.Id, []));
    }

    /// <summary>The fields of a notebook as Joplin has them now; its place is the one the plan gave it.</summary>
    private static JoplinFields FieldsOf(Run run, JexFolder folder, ImportStep step)
    {
        var (target, refusal) = PlaceOf(run, step.ParentId);
        return new JoplinFields(folder.Title, folder.ParentId, target, refusal, IsNote: false, false, null, null, []);
    }

    /// <summary>The Joplin ids of the tags that are here, sorted: what the base keeps, since a tag that is not here yet is still to be added.</summary>
    private static List<string> TagIdsHere(Run run, IEnumerable<string> tagIds) =>
        [.. tagIds.Where(run.TagOf.ContainsKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string? MoveRefusalCode(TreeError? error) => error?.Code switch
    {
        null => null,
        "parent_in_trash" => NoteCodes.MoveTargetInTrash,
        "move_cycle" => NoteCodes.MoveCycle,
        _ => NoteCodes.MoveTargetMissing,
    };

    /// <summary>
    /// Compares every field of one item three ways and says what to do, without writing. Null when the page is not in the tree
    /// (put in the trash, or gone). <paramref name="contentKey"/> goes into the base when the content is settled by this
    /// comparison; null leaves the base's content key as it is.
    /// </summary>
    private async Task<FieldPlan?> DecideFieldsAsync(
        Run run,
        InkpactDbContext db,
        TreeService tree,
        ExistingItem existing,
        JoplinFields joplin,
        ImportBase? baseInfo,
        string? contentKey,
        CancellationToken cancellationToken)
    {
        var page = await db.Pages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == existing.InkpactId, cancellationToken);
        if (page is null || !await tree.IsLiveAsync(page.Id, cancellationToken))
        {
            return null;
        }

        var plan = new FieldPlan();
        var unknown = false;

        var titleNotes = new ConversionNotes();
        var title = CleanTitle(joplin.Title, titleNotes);
        var titleKnown = baseInfo?.Title is not null;
        switch (ThreeWay.Decide<string?>(baseInfo?.Title, title, page.Title, titleKnown))
        {
            case FieldOutcome.Take:
                plan.Title = new Take<string>(title);
                plan.Reasons.AddRange(titleNotes.ToList());
                break;
            case FieldOutcome.Keep when titleKnown:
                plan.Reasons.Add(new ConversionNote(NoteCodes.TitleKept));
                break;
            case FieldOutcome.Keep:
                unknown = true;
                break;
        }

        var parentKnown = baseInfo?.ParentId is not null;
        var retry = false;
        if (!parentKnown || joplin.ParentId != baseInfo!.ParentId)
        {
            // Joplin moved it (or nothing is known): the page moves only while it still is under the page of the old notebook.
            var outcome = joplin.TargetRefusal is null && joplin.Target == page.ParentId ? FieldOutcome.Same
                : !parentKnown ? FieldOutcome.Keep
                : StillUnder(run, baseInfo!.ParentId!, page.ParentId) ? FieldOutcome.Take
                : FieldOutcome.Keep;
            switch (outcome)
            {
                case FieldOutcome.Take:
                    var refusal = joplin.TargetRefusal
                        ?? MoveRefusalCode(await tree.CheckMoveAsync(page.Id, page.WorkspaceId, joplin.Target, cancellationToken));
                    if (refusal is null)
                    {
                        plan.Parent = new Take<Guid?>(joplin.Target);
                    }
                    else
                    {
                        plan.Reasons.Add(new ConversionNote(refusal));
                        retry = true;
                    }

                    break;
                case FieldOutcome.Keep when parentKnown:
                    plan.Reasons.Add(new ConversionNote(NoteCodes.NotebookKept));
                    break;
                case FieldOutcome.Keep:
                    unknown = true;
                    break;
            }
        }

        var tagIds = joplin.IsNote ? TagIdsHere(run, joplin.TagIds) : null;
        if (joplin.IsNote)
        {
            var todoKnown = baseInfo?.IsTodo is not null;
            switch (ThreeWay.Decide<bool?>(baseInfo?.IsTodo, joplin.IsTodo, page.IsTodo, todoKnown))
            {
                case FieldOutcome.Take:
                    plan.IsTodo = new Take<bool>(joplin.IsTodo);
                    break;
                case FieldOutcome.Keep when todoKnown:
                    plan.Reasons.Add(new ConversionNote(NoteCodes.TodoKept));
                    break;
                case FieldOutcome.Keep:
                    unknown = true;
                    break;
            }

            switch (ThreeWay.Decide(baseInfo?.TodoCompleted, joplin.Completed, Milliseconds(page.CompletedAt), todoKnown))
            {
                case FieldOutcome.Take:
                    plan.Completed = new Take<long?>(joplin.Completed);
                    break;
                case FieldOutcome.Keep when todoKnown:
                    plan.Reasons.Add(new ConversionNote(NoteCodes.CompletedKept));
                    break;
                case FieldOutcome.Keep:
                    unknown = true;
                    break;
            }

            switch (ThreeWay.Decide(baseInfo?.TodoDue, joplin.Due, Milliseconds(page.DueAt), todoKnown))
            {
                case FieldOutcome.Take:
                    plan.Due = new Take<long?>(joplin.Due);
                    break;
                case FieldOutcome.Keep when todoKnown:
                    plan.Reasons.Add(new ConversionNote(NoteCodes.DueKept));
                    break;
                case FieldOutcome.Keep:
                    unknown = true;
                    break;
            }

            // A page that is not a to-do has no due date and no completion. When taking some of the three and keeping the
            // others would make one, the to-do changed in both places as a whole: it stays as it is here.
            var isTodo = plan.IsTodo is { } todo ? todo.Value : page.IsTodo;
            var due = plan.Due is { } newDue ? newDue.Value : Milliseconds(page.DueAt);
            var completed = plan.Completed is { } newCompleted ? newCompleted.Value : Milliseconds(page.CompletedAt);
            if ((plan.IsTodo is not null || plan.Due is not null || plan.Completed is not null) && !isTodo && (due is not null || completed is not null))
            {
                (plan.IsTodo, plan.Due, plan.Completed) = (null, null, null);
                plan.Reasons.RemoveAll(r => r.Code is NoteCodes.TodoKept or NoteCodes.CompletedKept or NoteCodes.DueKept);
                plan.Reasons.Add(new ConversionNote(NoteCodes.TodoKept));
            }

            // Tags as a set, in the ids they have here: two tags of the export can be one tag here.
            var now = tagIds!.Select(id => run.TagOf[id]).ToHashSet();
            var before = baseInfo?.TagIds is { } known
                ? TagIdsHere(run, known).Select(id => run.TagOf[id]).ToHashSet()
                : null;
            var here = (await db.PageTags.AsNoTracking().Where(t => t.PageId == page.Id).Select(t => t.TagId).ToListAsync(cancellationToken)).ToHashSet();
            (plan.AddTags, plan.RemoveTags) = ThreeWay.Tags(before, now, here);
            unknown |= before is null && !now.SetEquals(here);
        }

        if (unknown)
        {
            plan.Reasons.Add(new ConversionNote(NoteCodes.BaseUnknown));
        }

        plan.Retry = retry;
        plan.Base = new ImportBase
        {
            Title = title,
            ParentId = retry ? baseInfo?.ParentId : joplin.ParentId,
            ContentKey = contentKey ?? baseInfo?.ContentKey,
            IsTodo = joplin.IsNote ? joplin.IsTodo : null,
            TodoDue = joplin.Due,
            TodoCompleted = joplin.Completed,
            TagIds = tagIds,
        };
        return plan;
    }

    /// <summary>Writes what the comparison took over to the page, in the transaction that holds the workspace lock.</summary>
    private async Task WriteFieldsAsync(Run run, InkpactDbContext db, ExistingItem existing, FieldPlan plan, CancellationToken cancellationToken)
    {
        var page = await db.Pages.SingleAsync(p => p.Id == existing.InkpactId, cancellationToken);
        var now = time.GetUtcNow();
        var oldDue = page.DueAt;

        if (plan.Title is { } title)
        {
            page.Title = title.Value;
        }

        if (plan.Parent is { } parent)
        {
            // To the end of the new notebook, as a person's move does; the lock is the tree's own.
            page.ParentId = parent.Value;
            page.SortKey = await NextKeyAsync(run, db, parent.Value, cancellationToken);
        }

        if (plan.Title is not null || plan.Parent is not null)
        {
            page.Version++;
        }

        if (plan.IsTodo is { } isTodo)
        {
            page.IsTodo = isTodo.Value;
        }

        if (plan.Completed is { } completed)
        {
            page.CompletedAt = FromMilliseconds(completed.Value);
        }

        if (plan.Due is { } due)
        {
            page.DueAt = FromMilliseconds(due.Value);
        }

        page.UpdatedAt = now;
        foreach (var tag in plan.AddTags)
        {
            db.PageTags.Add(new PageTag { PageId = page.Id, TagId = tag });
        }

        await db.SaveChangesAsync(cancellationToken);
        if (plan.RemoveTags.Count > 0)
        {
            var remove = plan.RemoveTags.ToArray();
            await db.PageTags.Where(t => t.PageId == page.Id && remove.Contains(t.TagId)).ExecuteDeleteAsync(cancellationToken);
        }

        if (plan.AddTags.Count > 0 || plan.RemoveTags.Count > 0)
        {
            await PageSearchText.RefreshAsync(db, page.Id, cancellationToken);
        }

        if (plan.Due is not null && page.DueAt != oldDue && run.UserId is { } userId)
        {
            // The reminder follows the due date: only the importing person's, and only one that has not been delivered.
            var reminder = oldDue is { } old
                ? await db.Reminders.FirstOrDefaultAsync(
                    r => r.PageId == page.Id && r.UserId == userId && r.DeliveredAt == null && r.RemindAt == old, cancellationToken)
                : null;
            if (reminder is not null)
            {
                if (page.DueAt is { } moved)
                {
                    reminder.RemindAt = moved;
                }
                else
                {
                    db.Reminders.Remove(reminder);
                }
            }
            else if (page.DueAt is { } added)
            {
                db.Reminders.Add(new Reminder { PageId = page.Id, UserId = userId, RemindAt = added, CreatedAt = now });
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Keeps the item's record in step: the base always, the hash unless a move has to be tried again.</summary>
    private async Task WriteItemAsync(InkpactDbContext db, Run run, ExistingItem existing, FieldPlan plan, string? hash, CancellationToken cancellationToken)
    {
        var baseJson = plan.Base.ToJson();
        var moveHash = hash is not null && !plan.Retry;
        var taken = plan.Taken;
        var now = time.GetUtcNow();
        await db.ImportItems.Where(i => i.Id == existing.Id).ExecuteUpdateAsync(
            set =>
            {
                set.SetProperty(i => i.Base, baseJson);
                if (moveHash)
                {
                    set.SetProperty(i => i.ContentHash, hash!);
                }

                if (taken)
                {
                    set.SetProperty(i => i.ImportId, run.Id).SetProperty(i => i.ImportedAt, now);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Brings the fields of one item up to date with Joplin's, as far as the rule allows, and keeps its base in step. The page
    /// is written in one transaction under the tree's lock, with the base and the hash, so a crash leaves either all of it or
    /// none. <paramref name="hash"/> is the hash to store when this settles the item (null when the content step still has to
    /// run, which stores it last: the hash is what lets the next import skip the item).
    /// </summary>
    private async Task<FieldResult> ApplyFieldsAsync(
        Run run,
        ExistingItem existing,
        JoplinFields joplin,
        ImportBase? baseInfo,
        string? contentKey,
        string? hash,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        var tree = scope.ServiceProvider.GetRequiredService<TreeService>();

        // Looked at without the lock first: most items have nothing to write to the page, and the lock is everyone's.
        var plan = await DecideFieldsAsync(run, db, tree, existing, joplin, baseInfo, contentKey, cancellationToken);
        if (plan is null)
        {
            return new FieldResult(baseInfo ?? new ImportBase(), false, [], false, InTrash: true);
        }

        if (!plan.Taken)
        {
            await WriteItemAsync(db, run, existing, plan, hash, cancellationToken);
            return new FieldResult(plan.Base, false, plan.Reasons, plan.Retry, false);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(run, db, cancellationToken);
        // Again, with what the page is now that nobody else can change the tree.
        plan = await DecideFieldsAsync(run, db, tree, existing, joplin, baseInfo, contentKey, cancellationToken);
        if (plan is null)
        {
            return new FieldResult(baseInfo ?? new ImportBase(), false, [], false, InTrash: true);
        }

        if (plan.Taken)
        {
            await WriteFieldsAsync(run, db, existing, plan, cancellationToken);
        }

        await WriteItemAsync(db, run, existing, plan, hash, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FieldResult(plan.Base, plan.Taken, plan.Reasons, plan.Retry, false);
    }

    /// <summary>
    /// Counts and lists an item whose fields were compared and whose content was not written. <paramref name="contentKept"/>:
    /// a newer converter would have converted the content again, but it was changed here, so it was left as it is.
    /// </summary>
    private static void ReportFields(Run run, string kind, string id, string title, Guid? pageId, FieldResult fields, bool contentKept = false)
    {
        if (fields.InTrash)
        {
            run.Counts.AlreadyInTrash++;
            return;
        }

        if (fields.Taken)
        {
            run.Counts.Updated++;
        }
        else if (fields.Reasons.Count > 0 || contentKept)
        {
            run.Counts.KeptInInkpact++;
        }
        else
        {
            run.Counts.AlreadyImported++;
        }

        if (fields.Reasons.Count > 0)
        {
            AddEntry(run, new ReportEntry(kind, id, ShortTitle(title), fields.Taken ? ReportStatus.Updated : ReportStatus.Kept, fields.Reasons, pageId));
        }
    }

    // ---- notebooks imported before ---------------------------------------------------------------------------------------

    private async Task ReimportNotebookAsync(Run run, ImportStep step, JexFolder folder, ExistingItem existing, CancellationToken cancellationToken)
    {
        var bytes = await ReadItemAsync(run, folder.Offset, folder.Length, cancellationToken);
        var hash = HashOf(bytes);
        if (existing.HasBase && hash == existing.ContentHash)
        {
            run.Counts.AlreadyImported++;
            return;
        }

        var baseInfo = ImportBase.FromJson((await ReadItemRowAsync(existing.Id, cancellationToken)).Base);
        if (!existing.HasBase && hash == existing.ContentHash)
        {
            // Run 1.15 imported this very version of the file: what it holds is the base.
            baseInfo = new ImportBase { Title = CleanTitle(folder.Title, new ConversionNotes()), ParentId = folder.ParentId };
        }

        var fields = await ApplyFieldsAsync(run, existing, FieldsOf(run, folder, step), baseInfo, null, hash, cancellationToken);
        if (!fields.InTrash)
        {
            existing.HasBase = true;
            if (!fields.Retry)
            {
                existing.ContentHash = hash;
            }
        }

        ReportFields(run, "notebook", folder.Id, folder.Title, existing.InkpactId, fields);
    }

    // ---- tags imported before --------------------------------------------------------------------------------------------

    /// <summary>A tag's name as it is kept here: no control characters or tabs, at most 200 characters.</summary>
    private static string TagNameOf(JexTag tag)
    {
        var name = ContentRules.Cut(ContentRules.CleanText(tag.Title).Replace('\t', ' ').Trim(), 200);
        return name.Length == 0 ? "-" : name;
    }

    private async Task ReimportTagAsync(Run run, JexTag tag, ExistingItem existing, CancellationToken cancellationToken)
    {
        var bytes = await ReadItemAsync(run, tag.Offset, tag.Length, cancellationToken);
        var hash = HashOf(bytes);
        if (existing.HasBase && hash == existing.ContentHash)
        {
            run.Counts.AlreadyImported++;
            return;
        }

        var name = TagNameOf(tag);
        var baseInfo = ImportBase.FromJson((await ReadItemRowAsync(existing.Id, cancellationToken)).Base);
        if (!existing.HasBase && hash == existing.ContentHash)
        {
            baseInfo = new ImportBase { Title = name };
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.Tags.SingleOrDefaultAsync(t => t.Id == existing.InkpactId, cancellationToken);
        var reasons = new List<ConversionNote>();
        var taken = false;
        if (row is not null)
        {
            var known = baseInfo?.Title is not null;
            switch (ThreeWay.Decide<string?>(baseInfo?.Title, name, row.Name, known))
            {
                case FieldOutcome.Take:
                    var normalized = name.ToLowerInvariant();
                    // Only a tag made for this one: a tag of the same name that was there before (made by hand) is not Joplin's to rename.
                    var madeForIt = row.ExternalSourceType == SourceType && row.ExternalId == tag.Id;
                    var nameTaken = await db.Tags.AnyAsync(
                        t => t.WorkspaceId == row.WorkspaceId && t.NormalizedName == normalized && t.Id != row.Id, cancellationToken);
                    if (madeForIt && !nameTaken)
                    {
                        row.Name = name;
                        row.NormalizedName = normalized;
                        taken = true;
                    }
                    else
                    {
                        reasons.Add(new ConversionNote(NoteCodes.TagNameKept, name));
                    }

                    break;
                case FieldOutcome.Keep when known:
                    reasons.Add(new ConversionNote(NoteCodes.TagNameKept, name));
                    break;
                case FieldOutcome.Keep:
                    reasons.Add(new ConversionNote(NoteCodes.BaseUnknown));
                    break;
            }
        }

        var now = time.GetUtcNow();
        var baseJson = new ImportBase { Title = name }.ToJson();
        await db.SaveChangesAsync(cancellationToken);
        if (taken)
        {
            // The pages that have the tag are found by its new name.
            await PageSearchText.RefreshPagesOfTagAsync(db, existing.InkpactId, cancellationToken);
        }

        await db.ImportItems.Where(i => i.Id == existing.Id).ExecuteUpdateAsync(
            set =>
            {
                set.SetProperty(i => i.Base, baseJson).SetProperty(i => i.ContentHash, hash);
                if (taken)
                {
                    set.SetProperty(i => i.ImportId, run.Id).SetProperty(i => i.ImportedAt, now);
                }
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        existing.ContentHash = hash;
        existing.HasBase = true;
        ReportFields(run, "tag", tag.Id, name, null, new FieldResult(new ImportBase { Title = name }, taken, reasons, false, false));
    }

    // ---- notes that Joplin has put in its trash --------------------------------------------------------------------------

    private sealed record DeletedItem(StepKind Kind, string Id, string Title);

    /// <summary>What the look at one page that Joplin has in its trash found, before what is below it counts.</summary>
    private enum Unchanged
    {
        Yes,
        No,

        /// <summary>It could not be told (the sidecar is down, say): no reason to put a page in the trash, so it stays.</summary>
        Unknown,
    }

    /// <summary>
    /// The pages of notes and notebooks that an earlier import made and that Joplin has in its trash now. A page goes to the
    /// trash here too (where it can be restored) when it is unchanged here: its fields still are the base's and nothing was
    /// written to its content. A notebook goes only when everything below it goes, and only the highest page is put in the
    /// trash, so restoring it restores everything under it. Whatever stays is listed. The tree is walked without recursion:
    /// a file can nest notebooks as deep as it likes.
    /// </summary>
    private async Task TrashDeletedAsync(Run run, CancellationToken cancellationToken)
    {
        if (run.Deleted.Count == 0)
        {
            return;
        }

        var candidates = run.Deleted.ToDictionary(d => run.PageOf[d.Id], d => d);
        Dictionary<Guid, Page> snapshot;
        Dictionary<Guid, HashSet<Guid>> tagsOf;
        IReadOnlyList<PageDto> live;
        var unchanged = new Dictionary<Guid, Unchanged>();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            var items = run.Deleted.Select(d => run.Existing[d.Id].Id).ToList();
            var rows = await db.ImportItems.AsNoTracking().Where(i => items.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
            var pageIds = candidates.Keys.ToList();
            snapshot = await db.Pages.AsNoTracking().Where(p => pageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
            tagsOf = (await db.PageTags.AsNoTracking().Where(t => pageIds.Contains(t.PageId)).ToListAsync(cancellationToken))
                .GroupBy(t => t.PageId).ToDictionary(g => g.Key, g => g.Select(t => t.TagId).ToHashSet());
            live = (await scope.ServiceProvider.GetRequiredService<TreeService>().GetTreeAsync(run.WorkspaceId, cancellationToken)).Pages;

            foreach (var (pageId, item) in candidates)
            {
                try
                {
                    unchanged[pageId] = await UnchangedHereAsync(run, item, rows[run.Existing[item.Id].Id], snapshot[pageId], tagsOf, cancellationToken)
                        ? Unchanged.Yes
                        : Unchanged.No;
                }
                catch (Exception e) when (e is not OperationCanceledException and not ImportAbortedException)
                {
                    logger.LogWarning(e, "Could not tell whether {ExternalId} was changed here; it stays", item.Id);
                    unchanged[pageId] = Unchanged.Unknown;
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not ImportAbortedException)
        {
            // Everything was imported; only these could not be looked at, and they stay where they are.
            logger.LogError(e, "Could not compare the pages of what Joplin has put in its trash");
            foreach (var (pageId, item) in candidates)
            {
                FailDeleted(run, item, pageId);
            }

            return;
        }

        // A page that stays keeps every page above it: walked up from each, stopping at a page that was reached before
        // (everything above that one is in already).
        var parents = live.ToDictionary(p => p.Id, p => p.ParentId);
        var stays = new HashSet<Guid>();
        foreach (var page in live)
        {
            if (unchanged.GetValueOrDefault(page.Id, Unchanged.No) == Unchanged.Yes)
            {
                continue;
            }

            var id = (Guid?)page.Id;
            while (id is { } at && stays.Add(at))
            {
                id = parents.GetValueOrDefault(at);
            }
        }

        bool Goes(Guid id) => unchanged.GetValueOrDefault(id, Unchanged.No) == Unchanged.Yes && !stays.Contains(id);

        foreach (var (pageId, item) in candidates)
        {
            switch (unchanged[pageId])
            {
                case Unchanged.Unknown:
                    FailDeleted(run, item, pageId);
                    break;
                case Unchanged.No:
                    Keep(run, item, pageId, NoteCodes.DeletedInJoplinKept);
                    break;
                case Unchanged.Yes when !Goes(pageId):
                    Keep(run, item, pageId, NoteCodes.DeletedInJoplinNotEmpty);
                    break;
            }
        }

        // The highest page that goes: its parent stays, or is not one of these. Everything below it goes.
        var children = live.ToLookup(p => p.ParentId);
        foreach (var top in candidates.Keys.Where(id => Goes(id) && !(snapshot[id].ParentId is { } parent && Goes(parent))))
        {
            var group = new List<Guid>();
            var below = new Stack<Guid>([top]);
            while (below.TryPop(out var id))
            {
                group.Add(id);
                foreach (var child in children[id])
                {
                    below.Push(child.Id);
                }
            }

            try
            {
                if (await TrashGroupAsync(run, top, group, snapshot, tagsOf, cancellationToken))
                {
                    run.Counts.MovedToTrash += group.Count;
                }
                else
                {
                    foreach (var id in group)
                    {
                        Keep(run, candidates[id], id, NoteCodes.DeletedInJoplinKept);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException and not ImportAbortedException)
            {
                logger.LogError(e, "Putting {ExternalId} in the trash after Joplin did failed", candidates[top].Id);
                foreach (var id in group)
                {
                    FailDeleted(run, candidates[id], id);
                }
            }
        }
    }

    /// <summary>Whether a page that Joplin has in its trash is as the import left it: its fields the base's, and its content not written to.</summary>
    private async Task<bool> UnchangedHereAsync(
        Run run, DeletedItem item, ImportItem row, Page page, Dictionary<Guid, HashSet<Guid>> tagsOf, CancellationToken cancellationToken)
    {
        var baseInfo = ImportBase.FromJson(row.Base);
        if (baseInfo?.Title is null || baseInfo.ParentId is null || page.Title != baseInfo.Title || !StillUnder(run, baseInfo.ParentId, page.ParentId))
        {
            return false;
        }

        if (item.Kind != StepKind.Note)
        {
            return true;
        }

        if (baseInfo.IsTodo is null
            || baseInfo.TagIds is null
            || page.IsTodo != baseInfo.IsTodo
            || Milliseconds(page.CompletedAt) != baseInfo.TodoCompleted
            || Milliseconds(page.DueAt) != baseInfo.TodoDue
            || !(tagsOf.GetValueOrDefault(page.Id) ?? []).SetEquals(TagIdsHere(run, baseInfo.TagIds).Select(id => run.TagOf[id])))
        {
            return false;
        }

        // Nothing in the page's log after the import's own update: nobody wrote to it. Otherwise its fingerprint tells.
        var content = await pages.LoadAsync(page.Id, cancellationToken);
        return (row.PageSeq is { } seq && seq == content.LastSeq)
            || !ChangedHere(row, await yjs.FingerprintAsync(content.Inputs, cancellationToken));
    }

    private static string KindOf(DeletedItem item) => item.Kind == StepKind.Note ? "note" : "notebook";

    private static void Keep(Run run, DeletedItem item, Guid pageId, string code)
    {
        run.Counts.KeptInInkpact++;
        AddEntry(run, new ReportEntry(KindOf(item), item.Id, ShortTitle(item.Title), ReportStatus.Kept, [new ConversionNote(code)], pageId));
    }

    /// <summary>A page that Joplin has in its trash and that could not be looked at or put in the trash: it stays, and says so.</summary>
    private static void FailDeleted(Run run, DeletedItem item, Guid pageId)
    {
        run.Counts.Failed++;
        AddEntry(run, new ReportEntry(KindOf(item), item.Id, ShortTitle(item.Title), ReportStatus.Failed, [new ConversionNote(NoteCodes.ItemFailed)], pageId));
    }

    /// <summary>
    /// Puts the top page of a group in the trash, when the group still is what it was when it was judged: read again under the
    /// tree's lock, since a person's change to a title, a place or a to-do field does not wait for the import.
    /// </summary>
    private async Task<bool> TrashGroupAsync(
        Run run, Guid top, List<Guid> group, Dictionary<Guid, Page> snapshot, Dictionary<Guid, HashSet<Guid>> tagsOf, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(run, db, cancellationToken);

        var now = await db.Pages.AsNoTracking().Where(p => group.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var tags = (await db.PageTags.AsNoTracking().Where(t => group.Contains(t.PageId)).ToListAsync(cancellationToken))
            .GroupBy(t => t.PageId).ToDictionary(g => g.Key, g => g.Select(t => t.TagId).ToHashSet());
        var unchanged = now.Count == group.Count && group.All(id =>
        {
            var (before, after) = (snapshot[id], now[id]);
            return after.DeletedAt is null
                && after.Version == before.Version
                && after.ParentId == before.ParentId
                && after.Title == before.Title
                && after.IsTodo == before.IsTodo
                && after.CompletedAt == before.CompletedAt
                && after.DueAt == before.DueAt
                && (tags.GetValueOrDefault(id) ?? []).SetEquals(tagsOf.GetValueOrDefault(id) ?? []);
        });

        // A page made below it meanwhile is not one of the group: the notebook stays.
        if (!unchanged || await db.Pages.AnyAsync(p => p.ParentId != null && group.Contains(p.ParentId.Value) && !group.Contains(p.Id) && p.DeletedAt == null, cancellationToken))
        {
            return false;
        }

        var stamp = time.GetUtcNow();
        await db.Pages.Where(p => p.Id == top).ExecuteUpdateAsync(
            set => set.SetProperty(p => p.DeletedAt, stamp).SetProperty(p => p.Version, p => p.Version + 1).SetProperty(p => p.UpdatedAt, stamp),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
