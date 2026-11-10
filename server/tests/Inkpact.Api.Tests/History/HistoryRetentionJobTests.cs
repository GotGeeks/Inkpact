using System.Data.Common;
using System.Diagnostics;
using Inkpact.Api.History;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Inkpact.Api.Tests.History;

public class HistoryRetentionJobTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly CapturingLogger<HistoryRetentionJob> _log = new();
    private Guid _workspaceId;
    private Guid _pageId;
    private Guid _ownerId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var db = CreateContext();
        var owner = new User { Email = "owner@example.com", DisplayName = "Owner", CreatedAt = Now };
        var workspace = new Workspace { Name = "Workspace", OwnerId = owner.Id, CreatedAt = Now };
        var page = NewPage(workspace.Id, "a0");
        db.AddRange(owner, workspace, page);
        await db.SaveChangesAsync();
        (_workspaceId, _pageId, _ownerId) = (workspace.Id, page.Id, owner.Id);
    }

    [Fact]
    public async Task Pruning_is_per_page_and_keeps_live_state_the_archive_and_other_workspaces_unchanged()
    {
        var recent = Version(_pageId, Now.AddHours(-1));
        var newest = Version(_pageId, Now.AddDays(-2));
        var duplicate = Version(_pageId, Now.AddDays(-2).AddHours(-1));
        var expired = Version(_pageId, Now.AddDays(-365));
        var protectedVersion = Version(_pageId, Now.AddDays(-400), protectedUntil: Now.AddHours(24));
        var protectionEnded = Version(_pageId, Now.AddDays(-401), protectedUntil: Now);
        var secondPage = NewPage(_workspaceId, "a1");
        var secondPageVersion = Version(secondPage.Id, Now.AddDays(-2));
        var otherWorkspace = new Workspace { Name = "Other", OwnerId = _ownerId, CreatedAt = Now };
        var otherPage = NewPage(otherWorkspace.Id, "a0");
        var otherVersion = Version(otherPage.Id, Now.AddDays(-500));

        await using (var db = CreateContext())
        {
            db.AddRange(secondPage, otherWorkspace, otherPage, recent, newest, duplicate, expired, protectedVersion,
                protectionEnded, secondPageVersion, otherVersion);
            db.PageSnapshots.Add(new PageSnapshot { PageId = _pageId, State = [1, 2], StateVector = [3], Watermark = 9, UpdatedAt = Now });
            db.PageUpdates.Add(new PageUpdate { PageId = _pageId, Data = [4, 5], CreatedAt = Now });
            db.ArchivedPageUpdates.Add(new ArchivedPageUpdate { Seq = 9, PageId = _pageId, Data = [6, 7], CreatedAt = Now, ArchivedAt = Now });
            await db.SaveChangesAsync();
        }

        var result = await Job().PruneAsync(_workspaceId, default);
        result.ShouldBe(new HistoryRetentionResult(3, false));
        (await Job().PruneAsync(_workspaceId, default)).ShouldBe(new HistoryRetentionResult(0, false));

        await using var read = CreateContext();
        var remaining = (await read.PageVersions.AsNoTracking().Select(v => v.Id).ToListAsync()).ToHashSet();
        remaining.SetEquals([recent.Id, newest.Id, protectedVersion.Id, secondPageVersion.Id, otherVersion.Id]).ShouldBeTrue();
        var snapshot = await read.PageSnapshots.AsNoTracking().SingleAsync();
        snapshot.State.ShouldBe([(byte)1, 2]);
        snapshot.StateVector.ShouldBe([(byte)3]);
        snapshot.Watermark.ShouldBe(9);
        (await read.PageUpdates.AsNoTracking().SingleAsync()).Data.ShouldBe([(byte)4, 5]);
        (await read.ArchivedPageUpdates.AsNoTracking().SingleAsync()).Data.ShouldBe([(byte)6, 7]);
    }

    [Fact]
    public async Task A_cap_counts_file_text_and_removes_oldest_unprotected_versions_even_when_recent()
    {
        var fileText = new string('\u00e9', 110_000);
        var oldest = Version(_pageId, Now.AddHours(-3), size: 300_000, fileText: fileText);
        var protectedVersion = Version(_pageId, Now.AddHours(-2), size: 300_000, fileText: fileText, protectedUntil: Now.AddHours(1));
        var newest = Version(_pageId, Now.AddHours(-1), size: 300_000, fileText: fileText);
        await using (var db = CreateContext())
        {
            (await db.Workspaces.SingleAsync()).HistoryCapBytes = HistoryPolicy.Mebibyte;
            db.PageVersions.AddRange(oldest, protectedVersion, newest);
            await db.SaveChangesAsync();
        }

        var result = await Job().PruneAsync(_workspaceId, default);

        result.ShouldBe(new HistoryRetentionResult(1, false));
        await using var read = CreateContext();
        var remaining = (await read.PageVersions.AsNoTracking().Select(v => v.Id).ToListAsync()).ToHashSet();
        remaining.SetEquals([protectedVersion.Id, newest.Id]).ShouldBeTrue();
        (await read.PageVersions.SumAsync(v => v.PayloadBytes)).ShouldBe(1_040_000);
    }

    [Fact]
    public async Task Protected_versions_are_never_removed_for_the_cap_and_excess_is_reported()
    {
        var oldest = Version(_pageId, Now.AddHours(-3), size: 150_000);
        var firstProtected = Version(_pageId, Now.AddHours(-2), size: 600_000, protectedUntil: Now.AddHours(1));
        var secondProtected = Version(_pageId, Now.AddHours(-1), size: 600_000, protectedUntil: Now.AddHours(1));
        await using (var db = CreateContext())
        {
            var workspace = await db.Workspaces.SingleAsync();
            workspace.HistoryEnabled = false;
            workspace.HistoryCapBytes = HistoryPolicy.Mebibyte;
            db.PageVersions.AddRange(oldest, firstProtected, secondProtected);
            await db.SaveChangesAsync();
        }

        var result = await Job().PruneAsync(_workspaceId, default);

        result.ShouldBe(new HistoryRetentionResult(1, true));
        _log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("exceeds its cap", StringComparison.Ordinal));
        await using var read = CreateContext();
        var remaining = (await read.PageVersions.AsNoTracking().Select(v => v.Id).ToListAsync()).ToHashSet();
        remaining.SetEquals([firstProtected.Id, secondProtected.Id]).ShouldBeTrue();
    }

    [Fact]
    public async Task Deletion_and_page_batches_keep_the_newest_id_at_equal_timestamps()
    {
        var versions = Enumerable.Range(0, HistoryRetentionJob.DeleteBatchSize + 5)
            .Select(_ => Version(_pageId, Now.AddDays(-2))).ToList();
        var pages = Enumerable.Range(1, HistoryRetentionJob.PageBatchSize + 1)
            .Select(i => NewPage(_workspaceId, $"a{i:D4}")).ToList();
        var otherVersions = pages.SelectMany(p => new[] { Version(p.Id, Now.AddDays(-2)), Version(p.Id, Now.AddDays(-2)) }).ToList();
        await using (var db = CreateContext())
        {
            db.Pages.AddRange(pages);
            db.PageVersions.AddRange(versions.Concat(otherVersions));
            await db.SaveChangesAsync();
        }

        var result = await Job().PruneAsync(_workspaceId, default);

        result.Deleted.ShouldBe(versions.Count - 1 + pages.Count);
        result.AboveCap.ShouldBeFalse();
        await using var read = CreateContext();
        var remaining = await read.PageVersions.AsNoTracking().Select(v => v.Id).ToListAsync();
        var expected = versions.Concat(otherVersions).GroupBy(v => v.PageId).Select(g => g.Max(v => v.Id)).ToHashSet();
        remaining.ToHashSet().SetEquals(expected).ShouldBeTrue();
    }

    [Fact]
    public async Task The_cap_is_enforced_across_more_than_one_delete_batch()
    {
        const int size = 8_192;
        var versions = Enumerable.Range(0, 260)
            .Select(i => Version(_pageId, Now.AddSeconds(-i), size)).ToList();
        await using (var db = CreateContext())
        {
            (await db.Workspaces.SingleAsync()).HistoryCapBytes = HistoryPolicy.Mebibyte;
            db.PageVersions.AddRange(versions);
            await db.SaveChangesAsync();
        }

        var result = await Job().PruneAsync(_workspaceId, default);

        result.ShouldBe(new HistoryRetentionResult(132, false));
        await using var read = CreateContext();
        var remaining = (await read.PageVersions.AsNoTracking().Select(v => v.Id).ToListAsync()).ToHashSet();
        remaining.SetEquals(versions.Take(128).Select(v => v.Id)).ShouldBeTrue();
        (await read.PageVersions.SumAsync(v => v.PayloadBytes)).ShouldBe(HistoryPolicy.Mebibyte);
    }

    [Fact]
    public async Task A_policy_change_holding_the_workspace_lock_is_seen_before_pruning()
    {
        var version = Version(_pageId, Now.AddDays(-400));
        await using (var setup = CreateContext())
        {
            setup.PageVersions.Add(version);
            await setup.SaveChangesAsync();
        }

        await using var changing = CreateContext();
        await using var transaction = await changing.Database.BeginTransactionAsync();
        await WorkspaceHistoryLock.TakeAsync(changing, _workspaceId, default);
        var workspace = await changing.Workspaces.SingleAsync();
        workspace.HistoryExpiryDays = 730;
        await changing.SaveChangesAsync();

        var factory = new ObservedFactory(ConnectionString);
        var running = new HistoryRetentionJob(factory, new MutableTimeProvider(Now), _log).PruneAsync(_workspaceId, default);
        await factory.SecondContext.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await transaction.CommitAsync();

        (await running).ShouldBe(new HistoryRetentionResult(0, false));
        await using var read = CreateContext();
        (await read.PageVersions.CountAsync(v => v.Id == version.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_page_that_leaves_the_workspace_after_discovery_is_not_pruned_under_its_old_policy()
    {
        var other = new Workspace { Name = "Other", OwnerId = _ownerId, CreatedAt = Now, HistoryExpiryDays = 730 };
        var version = Version(_pageId, Now.AddDays(-400));
        await using (var setup = CreateContext())
        {
            setup.AddRange(other, version);
            await setup.SaveChangesAsync();
        }

        var factory = new PausedFactory(ConnectionString);
        var running = new HistoryRetentionJob(factory, new MutableTimeProvider(Now), _log).PruneAsync(_workspaceId, default);
        await factory.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var move = CreateContext();
            await move.Pages.Where(p => p.Id == _pageId).ExecuteUpdateAsync(set => set.SetProperty(p => p.WorkspaceId, other.Id));
        }
        finally
        {
            factory.Release.TrySetResult();
        }

        (await running).ShouldBe(new HistoryRetentionResult(0, false));
        (await Job().PruneAsync(other.Id, default)).ShouldBe(new HistoryRetentionResult(0, false));
        await using var read = CreateContext();
        (await read.PageVersions.CountAsync(v => v.Id == version.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_policy_change_between_batches_restarts_the_scan_with_the_new_policy()
    {
        var versions = Enumerable.Range(0, HistoryRetentionJob.DeleteBatchSize + 2)
            .Select(_ => Version(_pageId, Now.AddDays(-400))).ToList();
        await using (var setup = CreateContext())
        {
            setup.PageVersions.AddRange(versions);
            await setup.SaveChangesAsync();
        }

        var factory = new PausedFactory(ConnectionString, pauseAt: 3);
        var running = new HistoryRetentionJob(factory, new MutableTimeProvider(Now), _log).PruneAsync(_workspaceId, default);
        await factory.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var change = CreateContext();
            await change.Workspaces.Where(w => w.Id == _workspaceId)
                .ExecuteUpdateAsync(set => set.SetProperty(w => w.HistoryExpiryDays, 730));
        }
        finally
        {
            factory.Release.TrySetResult();
        }

        (await running).ShouldBe(new HistoryRetentionResult(versions.Count - 1, false));
        await using var read = CreateContext();
        (await read.PageVersions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_bucket_winner_removed_between_batches_does_not_cause_every_remaining_version_to_be_pruned()
    {
        var versions = Enumerable.Range(0, HistoryRetentionJob.DeleteBatchSize + 2)
            .Select(_ => Version(_pageId, Now.AddDays(-2))).ToList();
        await using (var setup = CreateContext())
        {
            setup.PageVersions.AddRange(versions);
            await setup.SaveChangesAsync();
        }

        var factory = new PausedFactory(ConnectionString, pauseAt: 3);
        var running = new HistoryRetentionJob(factory, new MutableTimeProvider(Now), _log).PruneAsync(_workspaceId, default);
        await factory.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var db = CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await WorkspaceHistoryLock.TakeAsync(db, _workspaceId, default);
            var newest = versions.Max(v => v.Id);
            (await db.PageVersions.Where(v => v.Id == newest).ExecuteDeleteAsync()).ShouldBe(1);
            await transaction.CommitAsync();
        }
        finally
        {
            factory.Release.TrySetResult();
        }

        (await running).ShouldBe(new HistoryRetentionResult(versions.Count - 2, false));
        await using var read = CreateContext();
        (await read.PageVersions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_crash_before_commit_rolls_back_the_batch_and_a_retry_finishes_it()
    {
        var first = Version(_pageId, Now.AddDays(-400));
        var second = Version(_pageId, Now.AddDays(-400));
        await using (var setup = CreateContext())
        {
            setup.PageVersions.AddRange(first, second);
            await setup.SaveChangesAsync();
        }

        var crashing = new HistoryRetentionJob(new CrashOnCommitFactory(ConnectionString), new MutableTimeProvider(Now), _log);
        await Should.ThrowAsync<SimulatedCrash>(() => crashing.PruneAsync(_workspaceId, default));
        await using (var read = CreateContext())
        {
            (await read.PageVersions.CountAsync()).ShouldBe(2);
        }

        (await Job().PruneAsync(_workspaceId, default)).ShouldBe(new HistoryRetentionResult(2, false));
        await using var final = CreateContext();
        (await final.PageVersions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task An_invalid_persisted_policy_fails_instead_of_deleting_versions()
    {
        var version = Version(_pageId, Now.AddDays(-400));
        await using (var db = CreateContext())
        {
            (await db.Workspaces.SingleAsync()).HistoryDailyDays = 0;
            db.PageVersions.Add(version);
            await db.SaveChangesAsync();
        }

        await Should.ThrowAsync<InvalidOperationException>(() => Job().PruneAsync(_workspaceId, default));
        await using var read = CreateContext();
        (await read.PageVersions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_round_logs_a_failed_workspace_continues_and_retries_it_next_time()
    {
        var otherWorkspace = new Workspace { Name = "Other", OwnerId = _ownerId, CreatedAt = Now };
        var otherPage = NewPage(otherWorkspace.Id, "a0");
        await using (var db = CreateContext())
        {
            (await db.Workspaces.SingleAsync()).HistoryDailyDays = 0;
            db.AddRange(otherWorkspace, otherPage, Version(_pageId, Now.AddDays(-400)), Version(otherPage.Id, Now.AddDays(-400)));
            await db.SaveChangesAsync();
        }

        var workerLog = new CapturingLogger<HistoryRetentionWorker>();
        var worker = new HistoryRetentionWorker(
            new TestDbContextFactory(ConnectionString), Job(), new HistoryOptions(), new MutableTimeProvider(Now), workerLog);
        (await worker.RunRoundAsync(default)).ShouldBe(new HistoryRetentionRound(1, 1));
        workerLog.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("next round will retry", StringComparison.Ordinal));

        await using (var db = CreateContext())
        {
            (await db.Workspaces.SingleAsync(w => w.Id == _workspaceId)).HistoryDailyDays = 30;
            (await db.PageVersions.CountAsync()).ShouldBe(1);
            await db.SaveChangesAsync();
        }

        (await worker.RunRoundAsync(default)).ShouldBe(new HistoryRetentionRound(1, 0));
        await using var read = CreateContext();
        (await read.PageVersions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_disabled_worker_exits_without_pruning()
    {
        var version = Version(_pageId, Now.AddDays(-400));
        await using (var setup = CreateContext())
        {
            setup.PageVersions.Add(version);
            await setup.SaveChangesAsync();
        }

        var worker = new HistoryRetentionWorker(
            new TestDbContextFactory(ConnectionString), Job(), new HistoryOptions { RetentionEnabled = false },
            new MutableTimeProvider(Now), new CapturingLogger<HistoryRetentionWorker>());
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        await using var read = CreateContext();
        (await read.PageVersions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_hosted_worker_prunes_without_an_explicit_call()
    {
        var version = Version(_pageId, Now.AddDays(-400));
        await using (var setup = CreateContext())
        {
            setup.PageVersions.Add(version);
            await setup.SaveChangesAsync();
        }

        var settings = new Dictionary<string, string>
        {
            ["History:RetentionEnabled"] = "True",
            ["History:RetentionInterval"] = "00:00:00.200",
        };
        await using var factory = new InkpactFactory(ConnectionString, time: new MutableTimeProvider(Now), settings: settings);
        _ = factory.Services;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            await using var read = CreateContext();
            if (await read.PageVersions.CountAsync() == 0)
            {
                break;
            }

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "gave up waiting for history retention");
            await Task.Delay(100);
        }
    }

    private HistoryRetentionJob Job() => new(new TestDbContextFactory(ConnectionString), new MutableTimeProvider(Now), _log);

    private static Page NewPage(Guid workspaceId, string sortKey) =>
        new() { WorkspaceId = workspaceId, Title = "Page", SortKey = sortKey, CreatedAt = Now, UpdatedAt = Now };

    private static PageVersion Version(Guid pageId, DateTimeOffset created, int size = 2, string fileText = "", DateTimeOffset? protectedUntil = null) =>
        new(pageId, new byte[size], "Title", fileText, created, null, PageVersionKind.Automatic, protectedUntil);

    private sealed class ObservedFactory(string connectionString) : IDbContextFactory<InkpactDbContext>
    {
        private int _created;

        public TaskCompletionSource SecondContext { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InkpactDbContext CreateDbContext()
        {
            if (Interlocked.Increment(ref _created) == 2)
            {
                SecondContext.TrySetResult();
            }

            return new InkpactDbContext(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
        }
    }

    private sealed class PausedFactory(string connectionString, int pauseAt = 2) : IDbContextFactory<InkpactDbContext>
    {
        private int _created;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InkpactDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);

        public async Task<InkpactDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _created) == pauseAt)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return CreateDbContext();
        }
    }

    private sealed class CrashOnCommitFactory(string connectionString) : IDbContextFactory<InkpactDbContext>
    {
        public InkpactDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).AddInterceptors(new CrashOnCommit()).Options);
    }

    private sealed class CrashOnCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new SimulatedCrash();
    }

    private sealed class SimulatedCrash : Exception;
}
