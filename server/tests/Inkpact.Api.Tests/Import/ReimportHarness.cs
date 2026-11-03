using System.Text;
using Inkpact.Api.Crdt;
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
/// What the tests of a second import need: the real app (database, hub, Yjs sidecar), an export to import as many times as a
/// test likes, and a way to do to the pages what a person does.
/// </summary>
public abstract class ReimportHarness(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    /// <summary>Milliseconds since 1970 as Joplin writes a due or a completed date.</summary>
    protected const string DueOne = "1790000000000";
    protected const string DueTwo = "1791000000000";
    protected const string DoneOne = "1790500000000";
    protected const string DoneTwo = "1790600000000";

    /// <summary>The conflict copy's title the English client sends with the upload.</summary>
    private const string CopyTitle = "{title} (Joplin)";

    private InkpactFactory _factory = null!;
    protected Guid WorkspaceId { get; private set; }

    protected Guid UserId { get; private set; }

    /// <summary>Where the app stores the files added to pages.</summary>
    protected string AttachmentsDirectory => _factory.AttachmentsDirectory;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
    }

    protected async Task StartAsync(IReadOnlyDictionary<string, string>? settings = null)
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(pooled: true), settings: settings, sidecar: true);
        await SyncUser.SetUpAdministratorAsync(_factory);
        await _factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        (WorkspaceId, UserId) = await UseDbAsync(async db => (
            await db.Workspaces.Select(w => w.Id).SingleAsync(),
            await db.Users.Select(u => u.Id).SingleAsync()));
    }

    /// <summary>A service of the running app, for a test that has to do what the app does in the background.</summary>
    protected T Service<T>()
        where T : notnull => _factory.Services.GetRequiredService<T>();

    protected async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    protected async Task<ImportRun> ImportAsync(JexBuilder jex)
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
                WorkspaceId = WorkspaceId,
                UserId = UserId,
                SourceType = ImportRunner.SourceType,
                FileName = "export.jex",
                CopyTitle = CopyTitle,
                State = ImportStates.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await _factory.Services.GetRequiredService<ImportRunner>().RunAsync(id, default);
        return await UseDbAsync(db => db.Imports.AsNoTracking().SingleAsync(i => i.Id == id));
    }

    protected static ImportCounts CountsOf(ImportRun run) => ImportJson.Read<ImportCounts>(run.Counts);

    protected static List<ReportEntry> EntriesOf(ImportRun run) => ImportJson.Read<List<ReportEntry>>(run.Report);

    protected static string[] CodesOf(ImportRun run, string name) =>
        [.. EntriesOf(run).Where(e => e.ExternalId == JexBuilder.Id(name)).SelectMany(e => e.Reasons).Select(r => r.Code)];

    protected static ReportEntry EntryOf(ImportRun run, string name) =>
        EntriesOf(run).Single(e => e.ExternalId == JexBuilder.Id(name));

    protected Task<Page> PageOfAsync(string name) =>
        UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.ExternalId == JexBuilder.Id(name)));

    protected Task<ImportItem> ItemOfAsync(string name) =>
        UseDbAsync(db => db.ImportItems.AsNoTracking().SingleAsync(i => i.ExternalId == JexBuilder.Id(name)));

    protected Task<Tag> TagOfAsync(string name) =>
        UseDbAsync(db => db.Tags.AsNoTracking().SingleAsync(t => t.ExternalId == JexBuilder.Id(name)));

    /// <summary>What a person does to a page: any change to its row.</summary>
    protected Task EditPageAsync(string name, Action<Page> edit) => UseDbAsync(async db =>
    {
        var page = await db.Pages.SingleAsync(p => p.ExternalId == JexBuilder.Id(name));
        edit(page);
        await db.SaveChangesAsync();
        return 0;
    });

    /// <summary>What a person does to move a page: through the tree, to the end of its new parent. The code of a refusal, or null.</summary>
    protected async Task<string?> PersonMovesAsync(string name, Guid? parentId)
    {
        var page = await PageOfAsync(name);
        await using var scope = _factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<TreeService>().MoveAsync(page.Id, new Position(parentId, null, null), null);
        return result.Error?.Code;
    }

    protected Task<List<string>> TagNamesOfAsync(Guid pageId) => UseDbAsync(db =>
        db.PageTags.Where(t => t.PageId == pageId).Join(db.Tags, t => t.TagId, t => t.Id, (_, tag) => tag.Name).OrderBy(n => n).ToListAsync());

    private IYjsCodec Codec => _factory.Services.GetRequiredService<IYjsCodec>();

    private async Task<IReadOnlyList<byte[]>> InputsAsync(Guid pageId) =>
        (await _factory.Services.GetRequiredService<PageUpdateStore>().LoadAsync(pageId, default)).Inputs;

    /// <summary>The text of a page as the search extract reads it, from what is stored.</summary>
    protected async Task<string> TextOfAsync(Guid pageId) =>
        (await Codec.CompactAsync(await InputsAsync(pageId), default)).Text.Trim();

    /// <summary>A person types: the page's content becomes this text, as an update of a client of their own.</summary>
    protected async Task PersonEditsAsync(Guid pageId, string text)
    {
        var document = Encoding.UTF8.GetBytes($$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{text}}"}]}]}""");
        var replacement = await Codec.ReplaceAsync(await InputsAsync(pageId), document, default);
        await _factory.Services.GetRequiredService<PageUpdateStore>().AppendAsync(pageId, UserId, replacement.Update, default);
    }
}
