using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Crdt;
using Inkpact.Api.Attachments;
using Inkpact.Api.Import;
using Inkpact.Api.Pages;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// Importing an export again (run 1.16a): the content of a note is compared three ways, between what Joplin had when it was
/// last imported, what it has now and what the page is now. The real database, hub and Yjs sidecar, with people editing the
/// pages the way a client does: by appending a Yjs update.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReimportContentTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] OtherPng = [.. Png, 0x01];

    private InkpactFactory _factory = null!;
    private YjsSidecar? _sidecar;
    private SyncUser _jos = null!;
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

        NpgsqlConnection.ClearAllPools();
    }

    private async Task StartAsync(Func<IYjsCodec, IYjsCodec>? wrapCodec = null)
    {
        IYjsCodec? codec = null;
        if (wrapCodec is not null)
        {
            _sidecar = await YjsSidecars.StartAsync(YjsSidecars.Create());
            codec = wrapCodec(_sidecar);
        }

        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(pooled: true), sidecar: codec is null, codec: codec);
        _jos = await SyncUser.SetUpAdministratorAsync(_factory);
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

    private IYjsCodec Codec => _factory.Services.GetRequiredService<IYjsCodec>();

    private async Task<ImportRun> ImportAsync(JexBuilder jex)
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

        await _factory.Services.GetRequiredService<ImportRunner>().RunAsync(id, default);
        return await UseDbAsync(db => db.Imports.AsNoTracking().SingleAsync(i => i.Id == id));
    }

    private static ImportCounts CountsOf(ImportRun run) => ImportJson.Read<ImportCounts>(run.Counts);

    private static List<ReportEntry> EntriesOf(ImportRun run) => ImportJson.Read<List<ReportEntry>>(run.Report);

    private Task<Page> PageOfAsync(string name) =>
        UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.ExternalId == JexBuilder.Id(name)));

    private Task<ImportItem> ItemOfAsync(string name) =>
        UseDbAsync(db => db.ImportItems.AsNoTracking().SingleAsync(i => i.ExternalId == JexBuilder.Id(name)));

    private Task<int> UpdateCountAsync() => UseDbAsync(db => db.PageUpdates.CountAsync());

    private async Task<IReadOnlyList<byte[]>> InputsAsync(Guid pageId) =>
        (await _factory.Services.GetRequiredService<PageUpdateStore>().LoadAsync(pageId, default)).Inputs;

    /// <summary>The text of a page as the search extract reads it, from what is stored.</summary>
    private async Task<string> TextOfAsync(Guid pageId) =>
        (await Codec.CompactAsync(await InputsAsync(pageId), default)).Text.Trim();

    private static byte[] Document(string text) =>
        Encoding.UTF8.GetBytes($$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{text}}"}]}]}""");

    /// <summary>A person types: the page's content becomes this text, as an update of a client of their own.</summary>
    private async Task PersonEditsAsync(Guid pageId, string text)
    {
        var replacement = await Codec.ReplaceAsync(await InputsAsync(pageId), Document(text), default);
        await AppendAsync(pageId, replacement.Update);
    }

    private Task AppendAsync(Guid pageId, byte[] update) =>
        _factory.Services.GetRequiredService<PageUpdateStore>().AppendAsync(pageId, _userId, update, default);

    /// <summary>An update that only deletes the first block the import wrote: no item is made, so no state vector moves.</summary>
    private async Task<byte[]> DeleteOnlyUpdateAsync(Guid pageId)
    {
        var stateVector = (await Codec.CompactAsync(await InputsAsync(pageId), default)).StateVector;
        var writer = new Lib0Writer();
        writer.WriteVarUint(0);
        writer.WriteVarUint(1);
        writer.WriteVarUint(StateVectors.TryDecode(stateVector)!.Keys.Single(client => client != 1));
        writer.WriteVarUint(1);
        writer.WriteVarUint(0);
        writer.WriteVarUint(1);
        return writer.ToArray();
    }

    private static string BodyHash(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    /// <summary>A notebook with two notes. "Aplan" sorts before "Bother", so a copy of it has a note after it to go before.</summary>
    private static JexBuilder Library(string plan = "Original text", string? updatedTime = null) => new JexBuilder()
        .Folder("work")
        .Note("plan", "work", body: plan, title: "Aplan", extra: updatedTime is null ? null : new() { ["updated_time"] = updatedTime })
        .Note("other", "work", body: "Another note", title: "Bother");

    private Task<List<Page>> PagesAsync() => UseDbAsync(db => db.Pages.AsNoTracking().ToListAsync());

    // ---- changed only in Joplin ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_note_that_changed_only_in_Joplin_gets_its_new_content_and_the_same_export_again_changes_nothing()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        var itemBefore = await ItemOfAsync("plan");

        var import = await ImportAsync(Library("Changed in Joplin"));

        var counts = CountsOf(import);
        (counts.Updated, counts.AlreadyImported, counts.Conflicts, counts.Failed, counts.Notes).ShouldBe((1, 2, 0, 0, 0));
        (await TextOfAsync(plan.Id)).ShouldBe("Changed in Joplin");
        (await PageOfAsync("plan")).ContentText.Trim().ShouldBe("Changed in Joplin");
        EntriesOf(import).ShouldBeEmpty();
        var item = await ItemOfAsync("plan");
        ImportBase.FromJson(item.Base)!.BodyHash.ShouldBe(BodyHash("Changed in Joplin"));
        item.ContentHash.ShouldNotBe(itemBefore.ContentHash);
        item.PageFingerprint.ShouldNotBe(itemBefore.PageFingerprint);
        item.ImportId.ShouldBe(import.Id);

        var updates = await UpdateCountAsync();
        var again = await ImportAsync(Library("Changed in Joplin"));
        (CountsOf(again).AlreadyImported, CountsOf(again).Updated).ShouldBe((3, 0));
        (await UpdateCountAsync()).ShouldBe(updates);
    }

    [Fact]
    public async Task A_page_that_is_open_gets_the_new_content_without_reloading()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        await using var editor = await _jos.ConnectAsync();
        var synced = await editor.SyncAsync(plan.Id);

        await ImportAsync(Library("Changed in Joplin"));

        var update = await editor.ReceiveAsync(MessageType.Update);
        update.PageId.ShouldBe(plan.Id);
        // What the editor holds with the update applied.
        (await Codec.CompactAsync([.. synced.Updates, update.Payload], default)).Text.Trim().ShouldBe("Changed in Joplin");
        await editor.ExpectSilenceAsync();
    }

    [Fact]
    public async Task An_edit_stored_while_the_new_content_was_made_is_not_overwritten_and_Joplins_version_becomes_a_copy()
    {
        HookCodec? codec = null;
        await StartAsync(inner => codec = new HookCodec(inner));
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        // Someone types while the import makes the replacement from the page as it read it.
        codec!.WhileReplacing = () => PersonEditsAsync(plan.Id, "typed meanwhile");

        var import = await ImportAsync(Library("Changed in Joplin"));

        (CountsOf(import).Updated, CountsOf(import).Conflicts, CountsOf(import).Failed).ShouldBe((0, 1, 0));
        (await TextOfAsync(plan.Id)).ShouldBe("typed meanwhile");
        var copy = (await PagesAsync()).Single(p => p.Title == "Aplan (Joplin)");
        (await TextOfAsync(copy.Id)).ShouldBe("Changed in Joplin");
    }

    [Fact]
    public async Task A_page_put_in_the_trash_while_its_new_content_was_made_is_left_there_as_it_was()
    {
        HookCodec? codec = null;
        await StartAsync(inner => codec = new HookCodec(inner));
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        var before = await ItemOfAsync("plan");
        var updates = await UpdateCountAsync();
        codec!.WhileReplacing = () => TrashAsync(plan.Id);

        var import = await ImportAsync(Library("Changed in Joplin"));

        (CountsOf(import).Updated, CountsOf(import).AlreadyInTrash, CountsOf(import).Failed).ShouldBe((0, 1, 0));
        (await TextOfAsync(plan.Id)).ShouldBe("Original text");
        (await UpdateCountAsync()).ShouldBe(updates);
        (await ItemOfAsync("plan")).ContentHash.ShouldBe(before.ContentHash);
    }

    private async Task TrashAsync(Guid pageId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<TreeService>().TrashAsync(pageId, null)).Succeeded.ShouldBeTrue();
    }

    // ---- changed only here -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_note_that_changed_only_in_Inkpact_is_left_alone_also_when_Joplin_only_touched_its_file()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        await PersonEditsAsync(plan.Id, "my own words");
        var updates = await UpdateCountAsync();
        var item = await ItemOfAsync("plan");

        var same = await ImportAsync(Library());
        // Joplin rewrites updated_time and other lines without a change a person can see.
        var touched = await ImportAsync(Library(updatedTime: "2026-05-05T01:02:03.000Z"));

        CountsOf(same).AlreadyImported.ShouldBe(3);
        CountsOf(touched).AlreadyImported.ShouldBe(3);
        (CountsOf(touched).Updated, CountsOf(touched).Conflicts).ShouldBe((0, 0));
        (await TextOfAsync(plan.Id)).ShouldBe("my own words");
        (await UpdateCountAsync()).ShouldBe(updates);
        (await PagesAsync()).Count.ShouldBe(3);

        // The file's hash moved on, so the next import passes over it unparsed; the base is what Joplin had, untouched.
        var after = await ItemOfAsync("plan");
        after.ContentHash.ShouldNotBe(item.ContentHash);
        after.Base.ShouldBe(item.Base);
    }

    // ---- changed in both -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_note_that_changed_in_both_keeps_the_version_here_and_gets_Joplins_as_a_copy_beside_it()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        var other = await PageOfAsync("other");
        await PersonEditsAsync(plan.Id, "my own words");

        var import = await ImportAsync(Library("Changed in Joplin"));

        var counts = CountsOf(import);
        (counts.Conflicts, counts.Updated, counts.AlreadyImported, counts.Failed).ShouldBe((1, 0, 2, 0));
        (await TextOfAsync(plan.Id)).ShouldBe("my own words");

        var copy = (await PagesAsync()).Single(p => p.Title == "Aplan (Joplin)");
        (await TextOfAsync(copy.Id)).ShouldBe("Changed in Joplin");
        copy.ParentId.ShouldBe(plan.ParentId);
        copy.ExternalId.ShouldBeNull();
        copy.ExternalSourceType.ShouldBeNull();
        (await UseDbAsync(db => db.ImportItems.CountAsync(i => i.InkpactId == copy.Id))).ShouldBe(0);
        (await PageOfAsync("plan")).Title.ShouldBe("Aplan");

        // Right after the original, before the page that came after it.
        string.CompareOrdinal(plan.SortKey, copy.SortKey).ShouldBeLessThan(0);
        string.CompareOrdinal(copy.SortKey, other.SortKey).ShouldBeLessThan(0);

        var entry = EntriesOf(import).Single();
        (entry.Status, entry.PageId, entry.OriginalPageId, entry.Kind).ShouldBe((ReportStatus.ConflictCopy, copy.Id, plan.Id, "note"));
        entry.Reasons.Select(r => r.Code).ShouldContain(NoteCodes.ContentConflict);
    }

    [Fact]
    public async Task The_same_export_after_a_conflict_makes_no_second_copy_and_a_later_change_in_Joplin_makes_another_and_never_overwrites()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        await PersonEditsAsync(plan.Id, "my own words");
        await ImportAsync(Library("Changed in Joplin"));
        var pages = (await PagesAsync()).Count;
        var updates = await UpdateCountAsync();

        var again = await ImportAsync(Library("Changed in Joplin"));

        CountsOf(again).AlreadyImported.ShouldBe(3);
        (await PagesAsync()).Count.ShouldBe(pages);
        (await UpdateCountAsync()).ShouldBe(updates);

        var later = await ImportAsync(Library("Changed in Joplin again"));

        CountsOf(later).Conflicts.ShouldBe(1);
        (await PagesAsync()).Count.ShouldBe(pages + 1);
        (await TextOfAsync(plan.Id)).ShouldBe("my own words");
        var texts = new List<string>();
        foreach (var copy in (await PagesAsync()).Where(p => p.Title == "Aplan (Joplin)"))
        {
            texts.Add(await TextOfAsync(copy.Id));
        }

        texts.Order().ShouldBe(["Changed in Joplin", "Changed in Joplin again"]);
    }

    [Fact]
    public async Task No_copy_is_made_beside_a_page_whose_notebook_was_put_in_the_trash_while_the_copy_was_made()
    {
        HookCodec? codec = null;
        await StartAsync(inner => codec = new HookCodec(inner));
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        await PersonEditsAsync(plan.Id, "my own words");
        var work = await PageOfAsync("work");
        codec!.WhileBuilding = () => TrashAsync(work.Id);

        var import = await ImportAsync(Library("Changed in Joplin"));

        (CountsOf(import).Conflicts, CountsOf(import).AlreadyInTrash).ShouldBe((0, 1));
        (await PagesAsync()).ShouldNotContain(p => p.Title == "Aplan (Joplin)");
        (await TextOfAsync(plan.Id)).ShouldBe("my own words");
    }

    [Fact]
    public async Task A_deletion_alone_counts_as_a_change_here()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        var item = await ItemOfAsync("plan");
        await AppendAsync(plan.Id, await DeleteOnlyUpdateAsync(plan.Id));
        var current = await Codec.CompactAsync(await InputsAsync(plan.Id), default);
        // The premise: deleting moves no state vector, so the vector alone cannot see it.
        StateVectors.HasGonePast(current.StateVector, item.StateVector!).ShouldBeFalse();
        (await TextOfAsync(plan.Id)).ShouldBeEmpty();

        var import = await ImportAsync(Library("Changed in Joplin"));

        (CountsOf(import).Conflicts, CountsOf(import).Updated).ShouldBe((1, 0));
        (await TextOfAsync(plan.Id)).ShouldBeEmpty();
    }

    // ---- a newer converter -----------------------------------------------------------------------------------------------

    private Task SetConverterVersionAsync(string name, int version) =>
        UseDbAsync(db => db.ImportItems.Where(i => i.ExternalId == JexBuilder.Id(name)).ExecuteUpdateAsync(set => set.SetProperty(i => i.ConverterVersion, version)));

    [Fact]
    public async Task A_page_made_by_an_older_converter_that_nobody_touched_is_converted_again()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        await SetConverterVersionAsync("plan", ImportOptions.ConverterVersion - 1);
        var updates = await UpdateCountAsync();

        var import = await ImportAsync(Library());

        var counts = CountsOf(import);
        (counts.Reconverted, counts.Updated, counts.AlreadyImported, counts.KeptInInkpact).ShouldBe((1, 0, 2, 0));
        (await UpdateCountAsync()).ShouldBe(updates + 1);
        (await TextOfAsync(plan.Id)).ShouldBe("Original text");
        (await ItemOfAsync("plan")).ConverterVersion.ShouldBe(ImportOptions.ConverterVersion);

        (CountsOf(await ImportAsync(Library())).AlreadyImported).ShouldBe(3);
    }

    [Fact]
    public async Task A_page_made_by_an_older_converter_that_someone_changed_is_left_alone()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        await PersonEditsAsync(plan.Id, "my own words");
        await SetConverterVersionAsync("plan", ImportOptions.ConverterVersion - 1);
        var updates = await UpdateCountAsync();

        var import = await ImportAsync(Library());

        var counts = CountsOf(import);
        (counts.KeptInInkpact, counts.Reconverted, counts.Conflicts).ShouldBe((1, 0, 0));
        (await TextOfAsync(plan.Id)).ShouldBe("my own words");
        (await UpdateCountAsync()).ShouldBe(updates);
        (await ItemOfAsync("plan")).ConverterVersion.ShouldBe(ImportOptions.ConverterVersion);
        CountsOf(await ImportAsync(Library())).KeptInInkpact.ShouldBe(0);
    }

    // ---- items of run 1.15 -----------------------------------------------------------------------------------------------

    private Task MakeItLikeRun115Async(string name) =>
        UseDbAsync(db => db.ImportItems.Where(i => i.ExternalId == JexBuilder.Id(name)).ExecuteUpdateAsync(
            set => set.SetProperty(i => i.Base, (string?)null).SetProperty(i => i.PageFingerprint, (byte[]?)null)));

    [Fact]
    public async Task An_item_without_a_base_is_compared_by_its_files_hash_and_the_state_vector()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        var other = await PageOfAsync("other");
        await MakeItLikeRun115Async("plan");
        await MakeItLikeRun115Async("other");
        await PersonEditsAsync(other.Id, "my own words");

        var import = await ImportAsync(new JexBuilder()
            .Folder("work")
            .Note("plan", "work", body: "Changed in Joplin", title: "Aplan")
            .Note("other", "work", body: "Also changed in Joplin", title: "Bother"));

        var counts = CountsOf(import);
        (counts.Updated, counts.Conflicts, counts.AlreadyImported).ShouldBe((1, 1, 1));
        (await TextOfAsync(plan.Id)).ShouldBe("Changed in Joplin");
        (await TextOfAsync(other.Id)).ShouldBe("my own words");
        // The base is there from now on.
        ImportBase.FromJson((await ItemOfAsync("plan")).Base)!.BodyHash.ShouldBe(BodyHash("Changed in Joplin"));
    }

    // ---- files -----------------------------------------------------------------------------------------------------------

    private static JexBuilder WithPicture(string text, byte[] picture) => new JexBuilder()
        .Folder("work")
        .Note("plan", "work", body: $"{text}\n\n![a picture](:/{JexBuilder.Id("pic")})", title: "Aplan")
        .Resource("pic", "picture.png", picture, "image/png");

    [Fact]
    public async Task A_file_with_the_same_bytes_is_reused_and_one_that_changed_gets_a_new_row_while_the_old_one_stays()
    {
        await StartAsync();
        await ImportAsync(WithPicture("First", Png));
        var plan = await PageOfAsync("plan");
        var first = await UseDbAsync(db => db.Attachments.AsNoTracking().SingleAsync(a => a.PageId == plan.Id));

        // Gone from the store: if the import stored the same bytes again, the file would be back.
        var store = (LocalAttachmentStore)_factory.Services.GetRequiredService<IAttachmentStore>();
        File.Delete(store.PathFor(first.Sha256));

        var sameFile = await ImportAsync(WithPicture("Second", Png));

        CountsOf(sameFile).Updated.ShouldBe(1);
        CountsOf(sameFile).Images.ShouldBe(0);
        (await UseDbAsync(db => db.Attachments.AsNoTracking().Where(a => a.PageId == plan.Id).Select(a => a.Id).ToListAsync())).ShouldBe([first.Id]);
        File.Exists(store.PathFor(first.Sha256)).ShouldBeFalse();

        var newBytes = await ImportAsync(WithPicture("Third", OtherPng));

        CountsOf(newBytes).Images.ShouldBe(1);
        var rows = await UseDbAsync(db => db.Attachments.AsNoTracking().Where(a => a.PageId == plan.Id).ToListAsync());
        rows.Count.ShouldBe(2);
        rows.Single(a => a.Id == first.Id).ExternalId.ShouldBeNull();
        var current = rows.Single(a => a.Id != first.Id);
        (current.ExternalId, current.Sha256).ShouldBe((JexBuilder.Id("pic"), Convert.ToHexStringLower(SHA256.HashData(OtherPng))));
        (await TextOfAsync(plan.Id)).ShouldBe("Third");
    }

    // ---- a note that fails -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The real sidecar, with what a test makes happen at the moment the import builds or replaces a page's content: a
    /// failure, or something a person does meanwhile (each hook runs once).
    /// </summary>
    private sealed class HookCodec(IYjsCodec inner) : IYjsCodec
    {
        public bool Fail { get; set; }

        public Func<Task>? WhileReplacing { get; set; }

        public Func<Task>? WhileBuilding { get; set; }

        public Task<YjsDiff> DiffAsync(ReadOnlyMemory<byte> stateVector, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken) =>
            inner.DiffAsync(stateVector, updates, cancellationToken);

        public Task<YjsCompaction> CompactAsync(IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken) =>
            inner.CompactAsync(inputs, cancellationToken);

        public Task<byte[]> FingerprintAsync(IReadOnlyList<byte[]> updates, CancellationToken cancellationToken) =>
            inner.FingerprintAsync(updates, cancellationToken);

        public async Task<byte[]> BuildAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            await RunOnceAsync(() => WhileBuilding, () => WhileBuilding = null);
            return await inner.BuildAsync(content, cancellationToken);
        }

        public async Task<YjsReplacement> ReplaceAsync(IReadOnlyList<byte[]> inputs, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("the sidecar broke");
            }

            var replacement = await inner.ReplaceAsync(inputs, content, cancellationToken);
            // After the replacement is made from the inputs, as a person's edit that lands while the import works would.
            await RunOnceAsync(() => WhileReplacing, () => WhileReplacing = null);
            return replacement;
        }

        private static async Task RunOnceAsync(Func<Func<Task>?> get, Action clear)
        {
            if (get() is { } hook)
            {
                clear();
                await hook();
            }
        }
    }

    [Fact]
    public async Task A_note_whose_replacement_fails_stays_as_it_was_with_its_base_and_the_next_import_takes_the_change()
    {
        HookCodec? codec = null;
        await StartAsync(inner => codec = new HookCodec(inner));
        await ImportAsync(Library());
        var plan = await PageOfAsync("plan");
        var before = await ItemOfAsync("plan");
        codec!.Fail = true;

        var failed = await ImportAsync(Library("Changed in Joplin"));

        var counts = CountsOf(failed);
        (counts.Failed, counts.Updated).ShouldBe((1, 0));
        EntriesOf(failed).Single().Status.ShouldBe(ReportStatus.Failed);
        (await TextOfAsync(plan.Id)).ShouldBe("Original text");
        var after = await ItemOfAsync("plan");
        (after.ContentHash, after.Base, after.ImportId).ShouldBe((before.ContentHash, before.Base, before.ImportId));

        codec.Fail = false;
        var retried = await ImportAsync(Library("Changed in Joplin"));

        CountsOf(retried).Updated.ShouldBe(1);
        (await TextOfAsync(plan.Id)).ShouldBe("Changed in Joplin");
    }

    // ---- whatever happens, in any order ----------------------------------------------------------------------------------

    private enum Step { EditInJoplin, EditInInkpact, ImportAgain }

    /// <summary>
    /// For any sequence of "edit in Joplin", "edit in Inkpact" and "import again": no edit made in Inkpact is ever gone from
    /// the page, a page nobody edited here has what Joplin has, and the Joplin version of the last import is on the page
    /// (now or once) or in a copy.
    /// </summary>
    [Fact]
    public async Task Whatever_the_order_of_edits_and_imports_no_edit_here_is_lost_and_every_Joplin_version_is_somewhere()
    {
        await StartAsync();
        var run = 0;
        var sequences = Arb.From(Gen.Choose(1, 9).SelectMany(length => Gen.ArrayOf(Gen.Elements(Step.EditInJoplin, Step.EditInInkpact, Step.ImportAgain), length)), _ => []);
        var config = Config.QuickThrowOnFailure.WithMaxTest(15).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(sequences, steps => CheckSequenceAsync(++run, steps).GetAwaiter().GetResult()));
    }

    private async Task<bool> CheckSequenceAsync(int run, Step[] steps)
    {
        var name = $"seq{run}";
        JexBuilder Export(string body) => new JexBuilder().Folder("work").Note(name, "work", body: body, title: name);

        var joplin = "J0";
        await ImportAsync(Export(joplin));
        var page = await PageOfAsync(name);

        // What the page held at some point, and the last text a person gave it.
        var held = new HashSet<string> { "J0" };
        string? mine = null;
        var edits = 0;
        var versions = 0;
        var ok = true;

        foreach (var step in steps)
        {
            switch (step)
            {
                case Step.EditInJoplin:
                    joplin = $"J{++versions}";
                    break;

                case Step.EditInInkpact:
                    mine = $"I{++edits}";
                    await PersonEditsAsync(page.Id, mine);
                    held.Add(mine);
                    break;

                case Step.ImportAgain:
                    var import = await ImportAsync(Export(joplin));
                    ok &= CountsOf(import).Failed == 0;
                    var text = await TextOfAsync(page.Id);
                    held.Add(text);
                    var copies = new List<string>();
                    foreach (var copy in (await PagesAsync()).Where(p => p.ExternalId == null && p.Title == $"{name} (Joplin)"))
                    {
                        copies.Add(await TextOfAsync(copy.Id));
                    }

                    // No edit here is gone: once a person typed, the page is theirs.
                    ok &= mine is null || text == mine;
                    // The Joplin version of this import is on the page now or once, or in a copy.
                    ok &= held.Contains(joplin) || copies.Contains(joplin);
                    // A page nobody edited has what Joplin has.
                    ok &= mine is not null || text == joplin;
                    break;
            }
        }

        return ok;
    }
}
