using System.Diagnostics;
using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Crdt;
using Inkpact.Api.Import;
using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// The import job against a real database and the real Yjs sidecar: the tree, what each page holds, what is stored beside
/// it, and what happens when the job is run again, stopped half way, or cannot reach the sidecar.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ImportRunnerTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Pdf = "%PDF-1.4\n%fake\n"u8.ToArray();

    private InkpactFactory _factory = null!;
    private YjsSidecar? _sidecar;
    private Guid _workspaceId;
    private Guid _userId;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_sidecar is not null)
        {
            await YjsSidecars.StopAsync(_sidecar);
        }

        // The pool of this test's database would otherwise stay open on the shared server.
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Starts the app on its own database, with the real sidecar (or a codec that wraps it), and a signed-in owner.</summary>
    private async Task StartAsync(Func<IYjsCodec, IYjsCodec>? wrapCodec = null, Dictionary<string, string>? settings = null)
    {
        IYjsCodec? codec = null;
        if (wrapCodec is not null)
        {
            _sidecar = await YjsSidecars.StartAsync(YjsSidecars.Create());
            codec = wrapCodec(_sidecar);
        }

        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(pooled: true), sidecar: codec is null, codec: codec, settings: settings);
        await _factory.SignedInClientAsync();
        if (codec is null)
        {
            await _factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        }

        (_workspaceId, _userId) = await UseDbAsync(async db => (
            await db.Workspaces.Select(w => w.Id).SingleAsync(),
            await db.Users.Select(u => u.Id).SingleAsync()));
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    /// <summary>Puts the file where the job reads it, makes the import's row, and runs the job the way the worker would.</summary>
    private async Task<ImportRun> ImportAsync(JexBuilder jex, CancellationToken cancellationToken = default)
    {
        var options = _factory.Services.GetRequiredService<ImportOptions>();
        var id = Guid.CreateVersion7();
        Directory.CreateDirectory(options.Directory);
        await File.WriteAllBytesAsync(ImportFiles.PathOf(options, id), jex.ToBytes());
        await UseDbAsync(async db =>
        {
            db.Imports.Add(new ImportRun
            {
                Id = id,
                WorkspaceId = _workspaceId,
                UserId = _userId,
                SourceType = ImportRunner.SourceType,
                FileName = "export.jex",
                State = ImportStates.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await _factory.Services.GetRequiredService<ImportRunner>().RunAsync(id, cancellationToken);
        return await GetImportAsync(id);
    }

    private Task<ImportRun> GetImportAsync(Guid id) => UseDbAsync(db => db.Imports.AsNoTracking().SingleAsync(i => i.Id == id));

    private async Task<TreeDto> TreeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TreeService>().GetTreeAsync(_workspaceId);
    }

    private Task<Page> PageOfAsync(string externalName) =>
        UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.ExternalId == JexBuilder.Id(externalName)));

    private static ImportCounts CountsOf(ImportRun run) => ImportJson.Read<ImportCounts>(run.Counts);

    private static List<ReportEntry> EntriesOf(ImportRun run) => ImportJson.Read<List<ReportEntry>>(run.Report);

    /// <summary>The text of a page as the search extract reads it, from the updates the import stored.</summary>
    private async Task<string> TextOfAsync(Guid pageId)
    {
        var updates = await UseDbAsync(db => db.PageUpdates.AsNoTracking().Where(u => u.PageId == pageId).OrderBy(u => u.Seq).Select(u => u.Data).ToListAsync());
        return (await _factory.Services.GetRequiredService<IYjsCodec>().CompactAsync(updates, default)).Text;
    }

    /// <summary>A small library with everything the import has to deal with.</summary>
    private static JexBuilder Library() => new JexBuilder()
        .Folder("work")
        .Folder("projects", parent: "work")
        .Folder("personal")
        .Note(
            "plan",
            "projects",
            body: $"# Plan\n\n![a picture](:/{JexBuilder.Id("pic")})\n\nSee [the manual](:/{JexBuilder.Id("doc")}) and [site](https://example.com).",
            todo: true,
            due: "1793358000000",
            completed: "1793358100000")
        .Note("old", "work", body: "Old notes")
        .Note("clip", "personal", body: "<p>Hello <b>world</b></p>", html: true)
        .Note("clash", "personal", body: "A conflict", extra: new() { ["is_conflict"] = "1" })
        .Note("secret", "personal", body: "ciphertext", extra: new() { ["encryption_applied"] = "1" })
        .Note("gone", "personal", body: "in the trash", extra: new() { ["deleted_time"] = "1793358000000" })
        .Tag("urgent")
        .Tag("later")
        .NoteTag("plan", "urgent")
        .Resource("pic", "picture.png", Png, "image/png")
        .Resource("doc", "manual.pdf", Pdf, "application/pdf");

    // ---- the whole import ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_library_becomes_the_tree_its_pages_and_everything_stored_beside_them()
    {
        await StartAsync();

        var import = await ImportAsync(Library());

        import.State.ShouldBe(ImportStates.Completed);
        import.ErrorCode.ShouldBeNull();
        import.FinishedAt.ShouldNotBeNull();
        import.ProcessedItems.ShouldBe(import.TotalItems);
        var counts = CountsOf(import);
        (counts.Notebooks, counts.Notes, counts.Todos, counts.Tags, counts.Images, counts.Files).ShouldBe((3, 4, 1, 2, 1, 1));
        (counts.Trashed, counts.Skipped, counts.Failed, counts.AlreadyImported).ShouldBe((1, 1, 0, 0));

        // The same nesting, notebooks before notes and each by title, at the end of the top level.
        var tree = await TreeAsync();
        // A conflict note keeps the title Joplin gave it; the report is where it says what it is.
        tree.Pages.Select(p => p.Title).ShouldBe(["personal", "clash", "clip", "work", "projects", "plan", "old"]);
        var plan = await PageOfAsync("plan");
        tree.Pages.Single(p => p.Id == plan.Id).ParentId.ShouldBe((await PageOfAsync("projects")).Id);

        // What Joplin knew about the note.
        plan.IsTodo.ShouldBeTrue();
        plan.DueAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1793358000000));
        plan.CompletedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1793358100000));
        plan.CreatedAt.ShouldBe(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        plan.UpdatedAt.ShouldBe(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
        plan.ExternalSourceType.ShouldBe("joplin");
        (await PageOfAsync("old")).IsTodo.ShouldBeFalse();

        // The content: one update for each note, none for a notebook, and the text is searchable at once.
        var updates = await UseDbAsync(db => db.PageUpdates.AsNoTracking().GroupBy(u => u.PageId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync());
        updates.Count.ShouldBe(4);
        updates.ShouldAllBe(u => u.Count == 1);
        (await TextOfAsync(plan.Id)).ShouldContain("Plan");
        plan.ContentText.ShouldContain("Plan");
        plan.ContentText.ShouldContain("site");
        (await TextOfAsync((await PageOfAsync("clip")).Id)).ShouldContain("Hello world");

        // The files the note uses, as the page's own attachments, named as in Joplin.
        var attachments = await UseDbAsync(db => db.Attachments.AsNoTracking().Where(a => a.PageId == plan.Id).OrderBy(a => a.FileName).ToListAsync());
        attachments.Select(a => (a.FileName, a.ContentType)).ShouldBe([("manual.pdf", "application/pdf"), ("picture.png", "image/png")]);
        attachments.Select(a => a.ExternalId).ShouldBe([JexBuilder.Id("doc"), JexBuilder.Id("pic")]);

        // Tags, and a reminder for the importing person for the to-do with a due date.
        var tags = await UseDbAsync(db => db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync());
        tags.Select(t => t.Name).ShouldBe(["later", "urgent"]);
        (await UseDbAsync(db => db.PageTags.AsNoTracking().Select(t => new { t.PageId, t.TagId }).ToListAsync()))
            .ShouldBe([new { PageId = plan.Id, TagId = tags.Single(t => t.Name == "urgent").Id }]);
        var reminder = (await UseDbAsync(db => db.Reminders.AsNoTracking().ToListAsync())).ShouldHaveSingleItem();
        (reminder.PageId, reminder.UserId, reminder.RemindAt, reminder.DeliveredAt).ShouldBe((plan.Id, _userId, plan.DueAt!.Value, null));

        // What a later import compares with: one row per item, and for a note the state of its document.
        var items = await UseDbAsync(db => db.ImportItems.AsNoTracking().ToListAsync());
        items.Count.ShouldBe(9);
        items.Count(i => i.Kind == "notebook").ShouldBe(3);
        items.Where(i => i.Kind != "note").ShouldAllBe(i => i.StateVector == null);
        var planItem = items.Single(i => i.ExternalId == JexBuilder.Id("plan"));
        planItem.InkpactId.ShouldBe(plan.Id);
        planItem.ContentHash.Length.ShouldBe(64);
        var stored = await UseDbAsync(db => db.PageUpdates.AsNoTracking().Where(u => u.PageId == plan.Id).Select(u => u.Data).ToListAsync());
        planItem.StateVector.ShouldBe((await _factory.Services.GetRequiredService<IYjsCodec>().CompactAsync(stored, default)).StateVector);

        // The report: what was left out, and what is not as it was.
        var entries = EntriesOf(import);
        entries.Single(e => e.ExternalId == JexBuilder.Id("secret")).ShouldSatisfyAllConditions(
            e => e.Status.ShouldBe(ReportStatus.Skipped),
            e => e.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.Encrypted]));
        entries.Single(e => e.ExternalId == JexBuilder.Id("clash")).Status.ShouldBe(ReportStatus.Conflict);
        entries.Single(e => e.ExternalId == JexBuilder.Id("plan")).Reasons.Select(r => r.Code).ShouldBe([NoteCodes.ResourceLinkTextLost]);
        entries.ShouldNotContain(e => e.ExternalId == JexBuilder.Id("gone"));

        // The file has done its job.
        File.Exists(ImportFiles.PathOf(_factory.Services.GetRequiredService<ImportOptions>(), import.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Importing_the_same_export_again_changes_nothing_and_says_so()
    {
        await StartAsync();
        await ImportAsync(Library());
        var before = await UseDbAsync(async db => (
            await db.Pages.CountAsync(),
            await db.Attachments.CountAsync(),
            await db.PageUpdates.CountAsync(),
            await db.Tags.CountAsync(),
            await db.Reminders.CountAsync(),
            await db.ImportItems.CountAsync()));

        var second = await ImportAsync(Library());

        second.State.ShouldBe(ImportStates.Completed);
        var counts = CountsOf(second);
        counts.AlreadyImported.ShouldBe(9);
        (counts.Notebooks, counts.Notes, counts.Tags, counts.Images, counts.Files).ShouldBe((0, 0, 0, 0, 0));
        (await UseDbAsync(async db => (
            await db.Pages.CountAsync(),
            await db.Attachments.CountAsync(),
            await db.PageUpdates.CountAsync(),
            await db.Tags.CountAsync(),
            await db.Reminders.CountAsync(),
            await db.ImportItems.CountAsync()))).ShouldBe(before);
    }

    [Fact]
    public async Task A_note_added_in_a_later_export_goes_under_the_notebook_that_is_already_there_after_its_other_pages()
    {
        await StartAsync();
        await ImportAsync(Library());
        var work = await PageOfAsync("work");

        await ImportAsync(new JexBuilder().Folder("work").Note("extra", "work", body: "Later"));

        var extra = await PageOfAsync("extra");
        extra.ParentId.ShouldBe(work.Id);
        (await TreeAsync()).Pages.Where(p => p.ParentId == work.Id).Select(p => p.Title).ShouldBe(["projects", "old", "extra"]);
    }

    [Fact]
    public async Task A_file_used_by_two_notes_is_stored_once_and_belongs_to_each_page_and_is_counted_once()
    {
        await StartAsync();
        var jex = new JexBuilder()
            .Folder("f")
            .Note("one", "f", body: $"![x](:/{JexBuilder.Id("pic")})")
            .Note("two", "f", body: $"![y](:/{JexBuilder.Id("pic")}) and again ![z](:/{JexBuilder.Id("pic")})")
            .Resource("pic", "picture.png", Png);

        var import = await ImportAsync(jex);

        var rows = await UseDbAsync(db => db.Attachments.AsNoTracking().ToListAsync());
        rows.Count.ShouldBe(2);
        rows.Select(r => r.PageId).Distinct().Count().ShouldBe(2);
        rows.Select(r => r.Sha256).Distinct().Count().ShouldBe(1);
        Directory.EnumerateFiles(_factory.AttachmentsDirectory, "*", SearchOption.AllDirectories).Count().ShouldBe(1);
        CountsOf(import).Images.ShouldBe(1);
    }

    [Fact]
    public async Task A_file_that_is_too_large_is_left_out_and_the_note_says_so()
    {
        await StartAsync(settings: new() { ["Attachments:MaxFileSize"] = "20" });
        var jex = new JexBuilder()
            .Folder("f")
            .Note("n", "f", body: $"![x](:/{JexBuilder.Id("big")}) text")
            .Resource("big", "big.png", [.. Png, .. Png]);

        var import = await ImportAsync(jex);

        import.State.ShouldBe(ImportStates.Completed);
        (await UseDbAsync(db => db.Attachments.CountAsync())).ShouldBe(0);
        EntriesOf(import).Single().Reasons.Select(r => r.Code).ShouldBe([NoteCodes.ResourceMissing, NoteCodes.ResourceTooLarge]);
        (await TextOfAsync((await PageOfAsync("n")).Id)).ShouldContain("text");
    }

    [Fact]
    public async Task A_note_whose_notebook_is_not_in_the_export_is_at_the_top_and_says_so()
    {
        await StartAsync();

        var import = await ImportAsync(new JexBuilder().Note("lonely", "nowhere", body: "x"));

        (await PageOfAsync("lonely")).ParentId.ShouldBeNull();
        EntriesOf(import).Single().Reasons.Select(r => r.Code).ShouldBe([NoteCodes.NotebookMissing]);
    }

    [Fact]
    public async Task A_file_that_is_not_an_export_ends_as_failed_with_a_code()
    {
        await StartAsync();
        var options = _factory.Services.GetRequiredService<ImportOptions>();
        var id = Guid.CreateVersion7();
        Directory.CreateDirectory(options.Directory);
        await File.WriteAllBytesAsync(ImportFiles.PathOf(options, id), new byte[3000]);
        await UseDbAsync(async db =>
        {
            db.Imports.Add(new ImportRun { Id = id, WorkspaceId = _workspaceId, UserId = _userId, SourceType = "joplin", FileName = "x.jex", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        await _factory.Services.GetRequiredService<ImportRunner>().RunAsync(id, default);

        var import = await GetImportAsync(id);
        import.State.ShouldBe(ImportStates.Failed);
        import.ErrorCode.ShouldBe("import_empty");
        File.Exists(ImportFiles.PathOf(options, id)).ShouldBeFalse();
    }

    // ---- stopped, and started again ------------------------------------------------------------------------------------

    /// <summary>
    /// A codec that lets the sidecar answer, except where the test says otherwise (by the number of the call to build),
    /// and that can do something of its own before a build: what a person does while the import runs.
    /// </summary>
    private sealed class FaultyCodec(IYjsCodec inner, Func<int, Exception?> faultOnBuild, Func<int, Task>? beforeBuild = null) : IYjsCodec
    {
        private int _builds;

        public Task<YjsDiff> DiffAsync(ReadOnlyMemory<byte> stateVector, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken) =>
            inner.DiffAsync(stateVector, updates, cancellationToken);

        public Task<YjsCompaction> CompactAsync(IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken) =>
            inner.CompactAsync(inputs, cancellationToken);

        public Task<byte[]> FingerprintAsync(IReadOnlyList<byte[]> updates, CancellationToken cancellationToken) =>
            inner.FingerprintAsync(updates, cancellationToken);

        public Task<YjsReplacement> ReplaceAsync(IReadOnlyList<byte[]> inputs, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            inner.ReplaceAsync(inputs, content, cancellationToken);

        public async Task<byte[]> BuildAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _builds);
            if (beforeBuild is not null)
            {
                await beforeBuild(call);
            }

            if (faultOnBuild(call) is { } fault)
            {
                throw fault;
            }

            return await inner.BuildAsync(content, cancellationToken);
        }
    }

    private static JexBuilder TenNotes()
    {
        var jex = new JexBuilder().Folder("f");
        for (var i = 0; i < 10; i++)
        {
            jex.Note($"n{i}", "f", body: $"Note number {i}", title: $"Note {i:00}");
        }

        return jex;
    }

    [Fact]
    public async Task A_job_that_stops_between_notes_leaves_whole_notes_and_the_next_import_finishes_the_rest()
    {
        using var stop = new CancellationTokenSource();
        await StartAsync(inner => new FaultyCodec(inner, call =>
        {
            if (call != 4)
            {
                return null;
            }

            // The app is stopping while the fourth note is being made.
            stop.Cancel();
            return new OperationCanceledException(stop.Token);
        }));

        await Should.ThrowAsync<OperationCanceledException>(() => ImportAsync(TenNotes(), stop.Token));

        // Three notes are in, each with its content and its import item; the fourth left nothing behind.
        var pages = await UseDbAsync(db => db.Pages.AsNoTracking().Where(p => p.ExternalId != null && p.ParentId != null).ToListAsync());
        pages.Count.ShouldBe(3);
        (await UseDbAsync(db => db.PageUpdates.Select(u => u.PageId).Distinct().ToListAsync())).Order().ShouldBe(pages.Select(p => p.Id).Order());
        (await UseDbAsync(db => db.ImportItems.CountAsync(i => i.Kind == "note"))).ShouldBe(3);
        var interrupted = await UseDbAsync(db => db.Imports.AsNoTracking().SingleAsync());
        interrupted.State.ShouldBe(ImportStates.Running);

        // The app starts again: the import is marked, and the same file imported again completes the tree.
        var worker = _factory.Services.GetServices<IHostedService>().OfType<ImportWorker>().Single();
        await worker.RecoverAsync(default);
        (await GetImportAsync(interrupted.Id)).ErrorCode.ShouldBe("interrupted");
        var finished = await ImportAsync(TenNotes());

        finished.State.ShouldBe(ImportStates.Completed);
        (CountsOf(finished).Notes, CountsOf(finished).AlreadyImported).ShouldBe((7, 4));
        (await UseDbAsync(db => db.Pages.CountAsync(p => p.ExternalId != null))).ShouldBe(11);
        (await UseDbAsync(db => db.PageUpdates.CountAsync())).ShouldBe(10);
        (await TreeAsync()).Pages.Skip(1).Select(p => p.Title).ShouldBe(Enumerable.Range(0, 10).Select(i => $"Note {i:00}"));
    }

    [Fact]
    public async Task While_the_sidecar_is_away_the_job_waits_and_then_imports_the_note()
    {
        await StartAsync(
            inner => new FaultyCodec(inner, call => call <= 3 ? new YjsUnavailableException("restarting") : null),
            new() { ["Import:SidecarRetryDelay"] = "00:00:00.050" });

        var import = await ImportAsync(new JexBuilder().Folder("f").Note("n", "f", body: "Hello"));

        import.State.ShouldBe(ImportStates.Completed);
        CountsOf(import).Notes.ShouldBe(1);
        (await UseDbAsync(db => db.PageUpdates.CountAsync())).ShouldBe(1);
        (await UseDbAsync(db => db.Pages.CountAsync(p => p.ExternalId != null))).ShouldBe(2);
    }

    [Fact]
    public async Task When_the_sidecar_stays_away_the_import_fails_and_no_page_is_made_without_its_content()
    {
        await StartAsync(
            inner => new FaultyCodec(inner, _ => new YjsUnavailableException("gone")),
            new() { ["Import:SidecarWait"] = "00:00:00.300", ["Import:SidecarRetryDelay"] = "00:00:00.050" });

        var import = await ImportAsync(new JexBuilder().Folder("f").Note("n", "f", body: "Hello").Note("m", "f", body: "Again"));

        import.State.ShouldBe(ImportStates.Failed);
        import.ErrorCode.ShouldBe("sidecar_unavailable");
        (await UseDbAsync(db => db.Pages.Where(p => p.ExternalId == JexBuilder.Id("n") || p.ExternalId == JexBuilder.Id("m")).CountAsync())).ShouldBe(0);
        (await UseDbAsync(db => db.PageUpdates.CountAsync())).ShouldBe(0);
        // The notebook, which needs no content, is there; the next import of the file adds the notes.
        (await UseDbAsync(db => db.Pages.CountAsync(p => p.ExternalId == JexBuilder.Id("f")))).ShouldBe(1);
    }

    [Fact]
    public async Task A_note_the_builder_refuses_is_reported_and_the_others_still_come_in()
    {
        await StartAsync(inner => new FaultyCodec(inner, call => call == 2 ? new YjsRejectedException("unknown node") : null));

        var import = await ImportAsync(new JexBuilder().Folder("f").Note("a", "f", title: "A").Note("b", "f", title: "B").Note("c", "f", title: "C"));

        import.State.ShouldBe(ImportStates.Completed);
        var counts = CountsOf(import);
        (counts.Notes, counts.Failed).ShouldBe((2, 1));
        var failed = EntriesOf(import).Single();
        (failed.Title, failed.Status, failed.Reasons.Single().Code).ShouldBe(("B", ReportStatus.Failed, NoteCodes.ConversionFailed));
    }

    [Fact]
    public async Task A_notebook_that_fails_takes_its_notes_into_the_report_and_not_into_the_tree()
    {
        await StartAsync();
        // A page that already has the notebook's id (from nowhere the import knows) makes the database refuse the notebook.
        await UseDbAsync(async db =>
        {
            db.Pages.Add(new Page
            {
                WorkspaceId = _workspaceId,
                Title = "Squatter",
                SortKey = "a0",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ExternalSourceType = "joplin",
                ExternalId = JexBuilder.Id("f"),
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var import = await ImportAsync(new JexBuilder().Folder("f").Note("n", "f", body: "x").Note("m", "f", body: "y"));

        import.State.ShouldBe(ImportStates.Completed);
        CountsOf(import).Failed.ShouldBe(3);
        EntriesOf(import).Select(e => (e.Kind, e.Reasons.Single().Code)).Order().ShouldBe(
            new[] { ("notebook", NoteCodes.ItemFailed), ("note", NoteCodes.ParentFailed), ("note", NoteCodes.ParentFailed) }.Order());
        (await UseDbAsync(db => db.Pages.CountAsync())).ShouldBe(1);
    }

    // ---- a person at work while the import runs, and what they did before it ---------------------------------------------

    [Fact]
    public async Task A_page_a_person_adds_under_a_notebook_during_the_import_neither_waits_nor_stops_the_import()
    {
        await StartAsync(inner => new FaultyCodec(inner, _ => null, async call =>
        {
            if (call != 3)
            {
                return;
            }

            // While the third note is being built, a person adds a page under its notebook, as the API would. The import
            // holds no lock while it builds, so this does not wait for it; and the notes after it go after this page.
            await using var scope = _factory.Services.CreateAsyncScope();
            var notebook = await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().Pages.AsNoTracking().SingleAsync(p => p.ExternalId == JexBuilder.Id("f"));
            var created = await scope.ServiceProvider.GetRequiredService<TreeService>()
                .CreateAsync(_workspaceId, "Mine", new Position(notebook.Id, null, null))
                .WaitAsync(TimeSpan.FromSeconds(10));
            created.Succeeded.ShouldBeTrue();
        }));

        var import = await ImportAsync(TenNotes());

        import.State.ShouldBe(ImportStates.Completed);
        (CountsOf(import).Notes, CountsOf(import).Failed).ShouldBe((10, 0));
        (await TreeAsync()).Pages.Skip(1).Select(p => p.Title).ShouldBe(
            ["Note 00", "Note 01", "Mine", "Note 02", "Note 03", "Note 04", "Note 05", "Note 06", "Note 07", "Note 08", "Note 09"]);
    }

    [Fact]
    public async Task Two_tags_of_the_export_that_are_one_tag_here_tag_the_note_once()
    {
        await StartAsync();
        var jex = new JexBuilder().Folder("f").Note("n", "f").Tag("t1", title: "Work").Tag("t2", title: "work").NoteTag("n", "t1").NoteTag("n", "t2");

        var import = await ImportAsync(jex);

        (CountsOf(import).Notes, CountsOf(import).Failed).ShouldBe((1, 0));
        (await UseDbAsync(db => db.Tags.CountAsync())).ShouldBe(1);
        (await UseDbAsync(db => db.PageTags.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task A_long_tag_name_is_cut_without_cutting_a_character_in_half()
    {
        await StartAsync();

        var import = await ImportAsync(new JexBuilder().Folder("f").Note("n", "f").Tag("t", title: new string('a', 199) + "😀").NoteTag("n", "t"));

        CountsOf(import).Failed.ShouldBe(0);
        (await UseDbAsync(db => db.Tags.Select(t => t.Name).SingleAsync())).ShouldBe(new string('a', 199));
        (await UseDbAsync(db => db.PageTags.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task What_the_person_put_in_the_trash_stays_there_and_gets_nothing_new_under_it()
    {
        await StartAsync();
        await ImportAsync(new JexBuilder().Folder("f").Folder("sub", "f").Note("a", "f"));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<TreeService>().TrashAsync((await PageOfAsync("f")).Id, null)).Succeeded.ShouldBeTrue();
        }

        var again = await ImportAsync(new JexBuilder()
            .Folder("f").Folder("sub", "f").Note("a", "f")
            .Note("b", "f").Folder("later", "f").Note("c", "later"));

        // What is in the trash is counted as such; what would go under it is listed, and not made where nobody sees it.
        var counts = CountsOf(again);
        (counts.AlreadyInTrash, counts.AlreadyImported, counts.Skipped, counts.Failed, counts.Notes).ShouldBe((3, 0, 3, 0, 0));
        EntriesOf(again).Select(e => (e.ExternalId, e.Reasons.Single().Code)).Order().ShouldBe(
            new[] { "b", "later", "c" }.Select(n => (JexBuilder.Id(n), NoteCodes.NotebookInTrash)).Order());
        (await UseDbAsync(db => db.Pages.CountAsync())).ShouldBe(3);
    }

    [Fact]
    public async Task An_item_whose_page_is_gone_altogether_is_imported_again()
    {
        await StartAsync();
        var jex = new JexBuilder().Folder("f").Note("a", "f", body: "Hello");
        await ImportAsync(jex);
        await UseDbAsync(db => db.Pages.Where(p => p.ExternalId == JexBuilder.Id("a")).ExecuteDeleteAsync());

        var again = await ImportAsync(jex);

        (CountsOf(again).Notes, CountsOf(again).AlreadyImported).ShouldBe((1, 1));
        (await TextOfAsync((await PageOfAsync("a")).Id)).ShouldContain("Hello");
        (await UseDbAsync(db => db.ImportItems.CountAsync())).ShouldBe(2);
    }

    [Fact]
    public async Task An_import_given_up_on_is_marked_failed_and_its_file_removed()
    {
        await StartAsync();
        var options = _factory.Services.GetRequiredService<ImportOptions>();
        var id = await UseDbAsync(async db =>
        {
            var run = new ImportRun { WorkspaceId = _workspaceId, UserId = _userId, SourceType = "joplin", FileName = "x.jex", State = ImportStates.Running, CreatedAt = DateTimeOffset.UtcNow };
            db.Imports.Add(run);
            await db.SaveChangesAsync();
            return run.Id;
        });
        Directory.CreateDirectory(options.Directory);
        await File.WriteAllBytesAsync(ImportFiles.PathOf(options, id), [1, 2, 3]);

        await _factory.Services.GetRequiredService<ImportRunner>().AbandonAsync(id);

        var import = await GetImportAsync(id);
        (import.State, import.ErrorCode).ShouldBe((ImportStates.Failed, "import_failed"));
        File.Exists(ImportFiles.PathOf(options, id)).ShouldBeFalse();
    }

    // ---- size ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_thousand_notes_are_imported_and_how_long_it_takes_is_written_down()
    {
        await StartAsync();
        var jex = new JexBuilder();
        for (var folder = 0; folder < 20; folder++)
        {
            jex.Folder($"f{folder}", folder % 4 == 0 ? null : $"f{folder - folder % 4}");
            for (var note = 0; note < 50; note++)
            {
                jex.Note($"n{folder}-{note}", $"f{folder}", body: $"# Note {note}\n\nSome **text** with a [link](https://example.com/{note}).\n\n- one\n- two", title: $"Note {note:00}");
            }
        }

        var clock = Stopwatch.StartNew();
        var import = await ImportAsync(jex);
        clock.Stop();

        output.WriteLine($"1,000 notes and 20 notebooks: {clock.Elapsed.TotalSeconds:0.0} s");
        import.State.ShouldBe(ImportStates.Completed);
        CountsOf(import).Failed.ShouldBe(0, import.Report);
        CountsOf(import).Notes.ShouldBe(1000);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(3));
        (await UseDbAsync(db => db.PageUpdates.CountAsync())).ShouldBe(1000);
        (await TreeAsync()).Pages.Count.ShouldBe(1020);
    }

    // ---- the tree, for any export --------------------------------------------------------------------------------------

    private static readonly string[] Titles = ["Alpha", "alpha", "Äpfel", "Beta", "", "Zebra", "éclair", "Note", "note"];

    [Fact]
    public async Task Whatever_the_export_the_tree_has_every_item_under_its_parent_with_sorted_siblings_and_no_loose_pages()
    {
        await StartAsync();
        var run = 0;
        // Twenty-odd numbers: the first ones say where notebooks go, the rest where notes go, and what they are called.
        var exports = Arb.From(
            Gen.Choose(2, 26).SelectMany(count => Gen.ArrayOf(Gen.Choose(0, 9999), count)),
            _ => []);
        var config = Config.QuickThrowOnFailure.WithMaxTest(12).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(exports, picks => CheckExportAsync(++run, picks).GetAwaiter().GetResult()));
    }

    private async Task<bool> CheckExportAsync(int run, int[] picks)
    {
        var folders = picks.Length / 2;
        var notes = picks.Length - folders;
        string Folder(int i) => $"r{run}f{i}";
        string Note(int i) => $"r{run}n{i}";
        var jex = new JexBuilder();
        for (var i = 0; i < folders; i++)
        {
            // Under a notebook before this one, or at the top level: no loops, which are the plan's own tests.
            var at = picks[i] % (i + 1);
            jex.Folder(Folder(i), at == i ? null : Folder(at), title: Titles[picks[i] / 7 % Titles.Length]);
        }

        for (var i = 0; i < notes; i++)
        {
            var pick = picks[folders + i];
            var at = pick % (folders + 1);
            jex.Note(Note(i), at == folders ? null : Folder(at), body: $"Text {i}", title: Titles[pick / 7 % Titles.Length]);
        }

        var import = await ImportAsync(jex);
        var tree = await TreeAsync();
        var inTree = tree.Pages.ToDictionary(p => p.Id);
        var pages = await UseDbAsync(db => db.Pages.AsNoTracking().Where(p => p.ExternalId != null).ToListAsync());
        var byExternalId = pages.ToDictionary(p => p.ExternalId!);
        var notebooks = Enumerable.Range(0, folders).Select(i => byExternalId.GetValueOrDefault(JexBuilder.Id(Folder(i)))).ToList();
        var written = Enumerable.Range(0, notes).Select(i => byExternalId.GetValueOrDefault(JexBuilder.Id(Note(i)))).ToList();

        // Everything in the file became a page, and every page of the database is in the tree.
        var complete = import.State == ImportStates.Completed && CountsOf(import).Failed == 0 && notebooks.Concat(written).All(p => p is not null);
        var reachable = pages.All(p => inTree.ContainsKey(p.Id));
        // Each notebook is under the page of the notebook the file names, or at the top level.
        var nested = Enumerable.Range(0, folders).All(i =>
        {
            var at = picks[i] % (i + 1);
            return notebooks[i]!.ParentId == (at == i ? null : notebooks[at]!.Id);
        });

        // Siblings: notebooks before notes, and each kind by title, without case or accents.
        var notebookIds = notebooks.Select(p => p!.Id).ToHashSet();
        var mine = notebooks.Concat(written).Select(p => p!.Id).ToHashSet();
        var sorted = tree.Pages.Where(p => mine.Contains(p.Id)).GroupBy(p => p.ParentId).All(group =>
        {
            var children = group.OrderBy(p => p.SortKey, StringComparer.Ordinal).ToList();
            for (var i = 1; i < children.Count; i++)
            {
                var (before, after) = (children[i - 1], children[i]);
                var (beforeKind, afterKind) = (notebookIds.Contains(before.Id) ? 0 : 1, notebookIds.Contains(after.Id) ? 0 : 1);
                if (beforeKind > afterKind
                    || (beforeKind == afterKind && string.CompareOrdinal(ImportPlan.TitleKey(before.Title), ImportPlan.TitleKey(after.Title)) > 0))
                {
                    return false;
                }
            }

            return true;
        });

        return complete && reachable && nested && sorted;
    }
}
