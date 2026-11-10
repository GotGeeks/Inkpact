using System.Text;
using System.Text.Json;
using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.History;
using Inkpact.Api.Tests.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Inkpact.Api.Tests.History;

public class HistorySnapshotBuilderTests(PostgresFixture postgres, YjsSidecarFixture yjs)
    : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    private static readonly byte[] EmptyDocument = Encoding.UTF8.GetBytes("""{"type":"doc"}""");

    [Fact]
    public async Task A_never_opened_page_starts_with_the_same_fixed_first_paragraph_as_clients()
    {
        var pageId = await AddPageAsync();

        var snapshot = (await Builder().BuildAsync(pageId, default)).ShouldNotBeNull();

        snapshot.PageId.ShouldBe(pageId);
        snapshot.WorkspaceId.ShouldBe(WorkspaceId);
        snapshot.Title.ShouldBe("Page");
        snapshot.MetadataVersion.ShouldBe(1);
        snapshot.ObservedAt.ShouldBe(Start);
        var fixedUpdate = await Codec.BuildAsync(EmptyDocument, default);
        (await Codec.FingerprintAsync([snapshot.State], default)).ShouldBe(await Codec.FingerprintAsync([fixedUpdate], default));
        using var document = JsonDocument.Parse(snapshot.DocumentJson);
        document.RootElement.GetProperty("content")[0].GetProperty("type").GetString().ShouldBe("paragraph");
    }

    [Fact]
    public async Task A_full_history_state_includes_the_snapshot_and_every_uncompacted_update()
    {
        var stream = EditStreams.Get(30, 2);
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, stream);
        await using (var db = CreateContext())
        {
            var page = await db.Pages.SingleAsync(p => p.Id == pageId);
            page.Title = "A title";
            page.FileText = "Large file note text";
            page.Version = 7;
            await db.SaveChangesAsync();
        }

        Options.MaxUpdatesPerCompaction = 5;
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        var snapshot = (await Builder().BuildAsync(pageId, default)).ShouldNotBeNull();
        var first = await Codec.BuildAsync(EmptyDocument, default);

        snapshot.Title.ShouldBe("A title");
        snapshot.FileText.ShouldBe("Large file note text");
        snapshot.MetadataVersion.ShouldBe(7);
        (await Codec.FingerprintAsync([snapshot.State], default)).ShouldBe(await Codec.FingerprintAsync([first, .. stream], default));
        (await ReadStoredAsync(pageId)).LiveSeqs.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_rename_append_and_compaction_cannot_mix_old_metadata_with_new_content()
    {
        var stream = EditStreams.Get(20, 4);
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, stream.Take(1));
        await using (var db = CreateContext())
        {
            var page = await db.Pages.SingleAsync(p => p.Id == pageId);
            page.Title = "Before";
            page.FileText = "Before file";
            await db.SaveChangesAsync();
        }

        var hook = new PausedRead();
        var building = Builder(hooks: hook).BuildAsync(pageId, default);
        await hook.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using (var db = CreateContext())
            {
                var page = await db.Pages.SingleAsync(p => p.Id == pageId);
                page.Title = "After";
                page.FileText = "After file";
                page.Version++;
                await db.SaveChangesAsync();
            }

            await AppendAsync(pageId, stream.Skip(1));
            (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        }
        finally
        {
            hook.Release.TrySetResult();
        }

        var snapshot = (await building).ShouldNotBeNull();
        var first = await Codec.BuildAsync(EmptyDocument, default);
        snapshot.Title.ShouldBe("Before");
        snapshot.FileText.ShouldBe("Before file");
        snapshot.MetadataVersion.ShouldBe(1);
        (await Codec.FingerprintAsync([snapshot.State], default)).ShouldBe(await Codec.FingerprintAsync([first, stream[0]], default));
        (await Codec.FingerprintAsync([snapshot.State], default)).ShouldNotBe(await Codec.FingerprintAsync([first, .. stream], default));
    }

    [Fact]
    public async Task A_late_committed_live_update_below_the_watermark_is_included()
    {
        var stream = EditStreams.Get(20, 5);
        var pageId = await AddPageAsync();
        await using var earlier = CreateContext();
        await using var later = CreateContext();
        await using var firstTransaction = await earlier.Database.BeginTransactionAsync();
        await using var secondTransaction = await later.Database.BeginTransactionAsync();
        var firstRow = new PageUpdate { PageId = pageId, UserId = UserId, Data = stream[0], CreatedAt = Start };
        var secondRow = new PageUpdate { PageId = pageId, UserId = UserId, Data = stream[1], CreatedAt = Start };
        earlier.PageUpdates.Add(firstRow);
        later.PageUpdates.Add(secondRow);
        await earlier.SaveChangesAsync();
        await later.SaveChangesAsync();
        await secondTransaction.CommitAsync();
        firstRow.Seq.ShouldBeLessThan(secondRow.Seq);
        Hooks.OnStage = stage => stage == CompactionStage.Merged ? firstTransaction.CommitAsync() : Task.CompletedTask;
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        Hooks.OnStage = null;
        (await ReadStoredAsync(pageId)).LiveSeqs.ShouldBe([firstRow.Seq]);

        var snapshot = (await Builder().BuildAsync(pageId, default)).ShouldNotBeNull();
        var first = await Codec.BuildAsync(EmptyDocument, default);

        (await Codec.FingerprintAsync([snapshot.State], default))
            .ShouldBe(await Codec.FingerprintAsync([first, stream[0], stream[1]], default));
    }

    [Fact]
    public async Task A_missing_page_does_not_invoke_the_sidecar()
    {
        var codec = Substitute.For<IYjsCodec>();

        (await Builder(codec).BuildAsync(Guid.NewGuid(), default)).ShouldBeNull();

        await codec.DidNotReceiveWithAnyArgs().BuildAsync(default, default);
    }

    [Fact]
    public async Task Mismatched_fingerprints_are_not_accepted_as_history()
    {
        var pageId = await AddPageAsync();
        var codec = FakeCodec();
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(new YjsCompaction([1], [1], [1], [2], "", []));

        await Should.ThrowAsync<HistorySnapshotVerificationException>(() => Builder(codec).BuildAsync(pageId, default));
        await codec.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Theory]
    [InlineData("""{"type":"doc"}""", """{"type":"doc","content":[{"type":"paragraph"}]}""", 0, 0)]
    [InlineData("""{"type":"doc"}""", """{"type":"doc"}""", 1, 0)]
    [InlineData("""{"type":"doc"}""", """{"type":"doc"}""", 0, 1)]
    [InlineData("not json", """{"type":"doc"}""", 0, 0)]
    [InlineData("null", "null", 0, 0)]
    [InlineData("""{"type":"paragraph"}""", """{"type":"paragraph"}""", 0, 0)]
    public async Task A_different_partial_or_invalid_read_is_not_accepted_as_history(string replay, string saved, int replaySkipped, int savedSkipped)
    {
        var pageId = await AddPageAsync();
        var codec = FakeCodec();
        codec.ReadAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(new YjsRead(Encoding.UTF8.GetBytes(replay), replaySkipped), new YjsRead(Encoding.UTF8.GetBytes(saved), savedSkipped));

        await Should.ThrowAsync<HistorySnapshotVerificationException>(() => Builder(codec).BuildAsync(pageId, default));
    }

    [Fact]
    public async Task A_sidecar_failure_is_propagated_instead_of_returning_a_partial_state()
    {
        var pageId = await AddPageAsync();
        var codec = FakeCodec();
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new YjsUnavailableException("Not available"));

        await Should.ThrowAsync<YjsUnavailableException>(() => Builder(codec).BuildAsync(pageId, default));
    }

    private HistorySnapshotBuilder Builder(IYjsCodec? codec = null, IHistorySnapshotHooks? hooks = null) =>
        new(Factory, codec ?? Codec, Clock, hooks ?? new NoHistorySnapshotHooks());

    private static IYjsCodec FakeCodec()
    {
        var codec = Substitute.For<IYjsCodec>();
        codec.BuildAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(new YjsCompaction([1], [1], [1], [1], "", []));
        return codec;
    }

    private sealed class PausedRead : IHistorySnapshotHooks
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AfterMetadataReadAsync(Guid pageId, CancellationToken cancellationToken)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
