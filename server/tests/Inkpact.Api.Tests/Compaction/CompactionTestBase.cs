using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Compaction;

/// <summary>
/// Compaction against the real Postgres and the real Yjs sidecar. Classes derived from this need
/// <c>IClassFixture&lt;YjsSidecarFixture&gt;</c> themselves, so that each gets a sidecar of its own (a class can be in
/// only one collection, and that one is Postgres').
/// </summary>
public abstract class CompactionTestBase(PostgresFixture postgres, YjsSidecarFixture yjs) : DatabaseTestBase(postgres)
{
    protected static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    protected MutableTimeProvider Clock { get; } = new(Start);

    protected IYjsCodec Codec => yjs.Sidecar;

    protected TestHooks Hooks { get; } = new();

    protected CapturingLogger<Compactor> CompactorLog { get; } = new();

    protected CompactionOptions Options { get; } = new();

    protected Guid UserId { get; private set; }

    protected Guid WorkspaceId { get; private set; }

    protected TestDbContextFactory Factory => new(ConnectionString);

    protected PageUpdateStore Store => new(Factory, Clock);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var db = CreateContext();
        var user = new User { Email = "owner@example.com", DisplayName = "Owner", CreatedAt = Start };
        var workspace = new Workspace { Name = "Workspace", OwnerId = user.Id, CreatedAt = Start };
        db.AddRange(user, workspace);
        await db.SaveChangesAsync();
        (UserId, WorkspaceId) = (user.Id, workspace.Id);
    }

    protected Compactor CreateCompactor(IYjsCodec? codec = null, ICompactionHooks? hooks = null) =>
        new(Factory, codec ?? Codec, hooks ?? Hooks, Options, Clock, CompactorLog);

    private int _pages;

    protected async Task<Guid> AddPageAsync()
    {
        await using var db = CreateContext();
        var page = new Page
        {
            WorkspaceId = WorkspaceId,
            Title = "Page",
            SortKey = $"a{_pages++:D4}",
            CreatedAt = Start,
            UpdatedAt = Start,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    protected async Task AppendAsync(Guid pageId, IEnumerable<byte[]> updates)
    {
        foreach (var update in updates)
        {
            await Store.AppendAsync(pageId, UserId, update, default);
        }
    }

    /// <summary>Appends many updates in one go, in order. Fast, for the setup of a test that does not test the append.</summary>
    protected async Task AppendBulkAsync(Guid pageId, IEnumerable<byte[]> updates)
    {
        await using var db = CreateContext();
        db.PageUpdates.AddRange(updates.Select(data => new PageUpdate { PageId = pageId, UserId = UserId, Data = data, CreatedAt = Start }));
        await db.SaveChangesAsync();
    }

    /// <summary>The fingerprint of what the page is as stored: its snapshot and its live updates.</summary>
    protected async Task<byte[]> StoredFingerprintAsync(Guid pageId) =>
        await Codec.FingerprintAsync((await Store.LoadAsync(pageId, default)).Inputs, default);

    /// <summary>Everything a compaction can change, to compare before and after.</summary>
    protected async Task<StoredPage> ReadStoredAsync(Guid pageId)
    {
        await using var db = CreateContext();
        var snapshot = await db.PageSnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.PageId == pageId);
        return new StoredPage(
            snapshot,
            [.. await db.PageUpdates.AsNoTracking().Where(u => u.PageId == pageId).OrderBy(u => u.Seq).Select(u => u.Seq).ToListAsync()],
            [.. await db.ArchivedPageUpdates.AsNoTracking().Where(u => u.PageId == pageId).OrderBy(u => u.Seq).Select(u => u.Seq).ToListAsync()],
            await db.Pages.Where(p => p.Id == pageId).Select(p => p.ContentText).SingleAsync());
    }

    /// <summary>A page as stored, with the bytes of the snapshot in hex so that two of them compare by value.</summary>
    protected sealed record StoredPage(PageSnapshot? Snapshot, long[] LiveSeqs, long[] ArchivedSeqs, string Text)
    {
        public string Summary => string.Join(
            "|",
            Snapshot is null ? "no snapshot" : $"{Convert.ToHexString(Snapshot.State)}/{Snapshot.Watermark}/{Snapshot.Version}/{Snapshot.UpdateCount}/{Snapshot.HistoryComplete}",
            string.Join(",", LiveSeqs),
            string.Join(",", ArchivedSeqs),
            Text);
    }

    /// <summary>Throws or waits at a stage of a compaction, as a test says.</summary>
    protected sealed class TestHooks : ICompactionHooks
    {
        public Func<CompactionStage, Task>? OnStage { get; set; }

        public Task AfterStageAsync(Guid pageId, CompactionStage stage, CancellationToken cancellationToken) =>
            OnStage?.Invoke(stage) ?? Task.CompletedTask;
    }

    /// <summary>The exception a test throws to stand for a crash.</summary>
    protected sealed class SimulatedCrash(CompactionStage stage) : Exception($"Simulated crash after {stage}");
}
