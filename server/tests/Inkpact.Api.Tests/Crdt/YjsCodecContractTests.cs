using System.Diagnostics;
using Inkpact.Api.Crdt;
using Inkpact.Api.Tests.Testing;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Crdt;

/// <summary>One real sidecar process for every test class that uses <see cref="YjsCollection"/>.</summary>
public sealed class YjsSidecarFixture : IAsyncLifetime
{
    public YjsSidecar Sidecar { get; } = YjsSidecars.Create();

    public Task InitializeAsync() => YjsSidecars.StartAsync(Sidecar);

    public Task DisposeAsync() => YjsSidecars.StopAsync(Sidecar);
}

[CollectionDefinition(Name)]
public sealed class YjsCollection : ICollectionFixture<YjsSidecarFixture>
{
    public const string Name = "yjs";
}

/// <summary>
/// What the app needs from Yjs for compaction, sync and search (scenario 14 of the spike), asked of the real sidecar
/// process. The fixtures are written by real Yjs, so a failure means the server would disagree with the browsers.
/// </summary>
[Collection(YjsCollection.Name)]
public sealed class YjsCodecContractTests(YjsSidecarFixture yjs, ITestOutputHelper output)
{
    private IYjsCodec Codec => yjs.Sidecar;

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task Compacting_a_fixture_gives_the_state_vector_and_the_size_of_a_fresh_encode(string name)
    {
        var fixture = YjsFixture.Load(name);

        var compaction = await Codec.CompactAsync(fixture.UpdateBytes, default);

        // Yjs writes the clients in descending order, the fixture lists them ascending.
        new SortedDictionary<string, ulong>(YjsFixture.DecodeStateVector(compaction.StateVector))
            .ShouldBe(new SortedDictionary<string, ulong>(fixture.ExpectedStateVector));
        compaction.State.Length.ShouldBe(fixture.JsStateBytes);
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task The_state_vector_read_out_of_a_fingerprint_is_the_pages_state_vector(string name)
    {
        var updates = YjsFixture.Load(name).UpdateBytes;

        var stateVector = StateVectors.OfFingerprint(await Codec.FingerprintAsync(updates, default));

        stateVector.ShouldNotBeNull();
        YjsFixture.DecodeStateVector(stateVector).ShouldBe(YjsFixture.DecodeStateVector((await Codec.CompactAsync(updates, default)).StateVector), ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task The_two_fingerprints_of_a_compaction_are_equal_and_match_the_replay(string name)
    {
        var updates = YjsFixture.Load(name).UpdateBytes;

        var compaction = await Codec.CompactAsync(updates, default);

        // The state decoded on its own is the same document as the inputs replayed.
        compaction.StateFingerprint.ShouldBe(compaction.ReplayFingerprint);
        compaction.ReplayFingerprint.ShouldBe(await Codec.FingerprintAsync(updates, default));
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task The_text_is_the_prosemirror_text_apart_from_empty_lines_and_trailing_spaces(string name)
    {
        var fixture = YjsFixture.Load(name);

        var compaction = await Codec.CompactAsync(fixture.UpdateBytes, default);

        YjsFixture.Lines(compaction.Text).ShouldBe(YjsFixture.Lines(fixture.ExpectedText));
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task Compacting_its_own_output_changes_nothing(string name)
    {
        var first = await Codec.CompactAsync(YjsFixture.Load(name).UpdateBytes, default);

        // Compaction may run twice on one page.
        var second = await Codec.CompactAsync([first.State], default);

        second.State.ShouldBe(first.State);
        second.StateVector.ShouldBe(first.StateVector);
        second.ReplayFingerprint.ShouldBe(first.ReplayFingerprint);
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task A_snapshot_plus_updates_that_overlap_it_gives_the_same_document(string name)
    {
        var fixture = YjsFixture.Load(name);
        var updates = fixture.UpdateBytes;
        var full = await Codec.FingerprintAsync(updates, default);
        var half = await Codec.CompactAsync(updates.Take(fixture.PartialCount).ToList(), default);

        // A crash between writing a snapshot and removing what it folded in leaves both behind.
        var overlapping = await Codec.CompactAsync([half.State, .. updates], default);
        var everything = await Codec.CompactAsync([.. updates, .. updates], default);

        overlapping.ReplayFingerprint.ShouldBe(full);
        everything.ReplayFingerprint.ShouldBe(full);
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task A_chain_of_incremental_compactions_equals_one_replay_of_everything(string name)
    {
        var updates = YjsFixture.Load(name).UpdateBytes;
        var full = await Codec.CompactAsync(updates, default);

        byte[]? snapshot = null;
        YjsCompaction? last = null;
        var step = Math.Max(updates.Count / 7, 1);
        for (var from = 0; from < updates.Count; from += step)
        {
            // Each round folds the next updates into the previous snapshot, as the worker will.
            List<byte[]> inputs = snapshot is null ? [] : [snapshot];
            inputs.AddRange(updates.Skip(from).Take(step));
            last = await Codec.CompactAsync(inputs, default);
            last.StateFingerprint.ShouldBe(last.ReplayFingerprint);
            snapshot = last.State;
        }

        last!.ReplayFingerprint.ShouldBe(full.ReplayFingerprint);
        last.State.ShouldBe(full.State);
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task Updates_out_of_causal_order_give_the_same_document(string name)
    {
        var updates = YjsFixture.Load(name).UpdateBytes;
        var random = new Random(2026); // seeded, so a failure can be repeated
        var shuffled = updates.OrderBy(_ => random.Next()).ToList();

        // An update whose dependencies are missing waits until they arrive.
        (await Codec.CompactAsync(shuffled, default)).ReplayFingerprint
            .ShouldBe(await Codec.FingerprintAsync(updates, default));
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task The_fingerprint_changes_when_an_update_is_missing(string name)
    {
        var updates = YjsFixture.Load(name).UpdateBytes;

        (await Codec.FingerprintAsync(updates.Take(updates.Count - 1).ToList(), default))
            .ShouldNotBe(await Codec.FingerprintAsync(updates, default));
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task A_diff_against_a_partial_state_vector_completes_the_partial_state(string name)
    {
        var fixture = YjsFixture.Load(name);
        var updates = fixture.UpdateBytes;
        var full = await Codec.CompactAsync(updates, default);

        var diff = await Codec.DiffAsync(fixture.PartialStateVectorBytes, updates, default);

        diff.Update.Length.ShouldBeLessThan(full.State.Length);
        diff.StateVector.ShouldBe(full.StateVector);
        // The client applies the diff on top of what it has, with Yjs.
        var client = await Codec.FingerprintAsync([.. updates.Take(fixture.PartialCount), diff.Update], default);
        client.ShouldBe(full.ReplayFingerprint);
    }

    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task A_diff_against_an_empty_state_vector_is_the_full_state(string name)
    {
        var updates = YjsFixture.Load(name).UpdateBytes;
        var full = await Codec.CompactAsync(updates, default);

        var diff = await Codec.DiffAsync(ReadOnlyMemory<byte>.Empty, updates, default);

        diff.Update.ShouldBe(full.State);
        diff.StateVector.ShouldBe(full.StateVector);
    }

    [Fact]
    public async Task A_deletion_that_arrives_before_its_content_is_applied()
    {
        // Two updates: a delete set for three characters, then the content it deletes. Yjs keeps the deletion
        // waiting until the content arrives (the YDotNet library lost it, which is why it is not used).
        var repro = YjsFixture.Read("yrs-out-of-order-repro");

        var compaction = await Codec.CompactAsync(YjsFixture.Bytes(repro.GetProperty("updates")), default);

        YjsFixture.Lines(compaction.Text).ShouldBe(YjsFixture.Lines(repro.GetProperty("yjsText").GetString()!));
        compaction.StateFingerprint.ShouldBe(compaction.ReplayFingerprint);
    }

    [Fact]
    public async Task Half_of_a_deleted_emoji_shows_as_the_replacement_character_as_in_yjs()
    {
        var fixture = YjsFixture.Read("surrogate-split");

        var compaction = await Codec.CompactAsync(YjsFixture.Bytes(fixture.GetProperty("updates")), default);

        compaction.Text.ShouldBe(fixture.GetProperty("expectedText").GetString());
        compaction.Text.ShouldBe("a�b");
        compaction.StateFingerprint.ShouldBe(compaction.ReplayFingerprint);
    }

    [Fact]
    public async Task One_transaction_can_lose_a_deletion_so_updates_are_applied_one_at_a_time()
    {
        // Found by the 50,000-edit stress test of run 0.5: a snapshot and the updates after it, applied in one Yjs
        // transaction, end without a deletion every browser has. Invisible in the text; only the delete set shows it.
        var repro = YjsFixture.Read("yjs-one-transaction-repro");
        var inputs = new List<byte[]> { Convert.FromBase64String(repro.GetProperty("snapshot").GetString()!) };
        inputs.AddRange(YjsFixture.Bytes(repro.GetProperty("updates")));

        var compaction = await Codec.CompactAsync(inputs, default);

        Base64(compaction.ReplayFingerprint).ShouldBe(repro.GetProperty("fingerprint").GetString());
        Base64(compaction.StateFingerprint).ShouldBe(repro.GetProperty("fingerprint").GetString());
        Base64(compaction.ReplayFingerprint).ShouldNotBe(repro.GetProperty("oneTransactionFingerprint").GetString());
    }

    [Fact]
    public async Task A_page_that_is_not_an_editor_document_compacts_and_has_no_text()
    {
        // A Y.Map and a Y.Text, no `default` XML fragment: nothing here may assume the editor's schema.
        var page = YjsFixture.Read("non-tiptap");

        var compaction = await Codec.CompactAsync(YjsFixture.Bytes(page.GetProperty("updates")), default);

        compaction.Text.ShouldBe("");
        Base64(compaction.ReplayFingerprint).ShouldBe(page.GetProperty("fingerprint").GetString());
        compaction.StateFingerprint.ShouldBe(compaction.ReplayFingerprint);
    }

    [Fact]
    public async Task An_empty_page_compacts_into_the_empty_document()
    {
        var compaction = await Codec.CompactAsync([], default);

        compaction.Text.ShouldBe("");
        compaction.StateFingerprint.ShouldBe(compaction.ReplayFingerprint);
        (await Codec.DiffAsync(ReadOnlyMemory<byte>.Empty, [], default)).StateVector.ShouldBe(compaction.StateVector);
    }

    [Fact]
    public async Task Ten_thousand_updates_compact_quickly()
    {
        var fixture = YjsFixture.Load("tiptap-10k");
        var updates = fixture.UpdateBytes;
        await Codec.FingerprintAsync(updates.Take(10).ToList(), default); // warm up

        var stopwatch = Stopwatch.StartNew();
        var compaction = await Codec.CompactAsync(updates, default);
        var compactMs = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        await Codec.DiffAsync(fixture.PartialStateVectorBytes, updates, default);
        var diffMs = stopwatch.Elapsed.TotalMilliseconds;

        output.WriteLine($"{updates.Count} updates ({updates.Sum(u => u.Length)} bytes): compact {compactMs:F1} ms -> {compaction.State.Length} bytes; diff {diffMs:F1} ms");
        // The spike measured 33 ms and 8 ms on a laptop; the bound is for a slow CI machine, not a benchmark.
        compactMs.ShouldBeLessThan(5_000);
        diffMs.ShouldBeLessThan(2_000);
    }
}
