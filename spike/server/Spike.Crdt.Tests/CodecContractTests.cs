using System.Diagnostics;
using Xunit.Abstractions;

namespace Spike.Crdt.Tests;

/// <summary>One Yjs sidecar process for the whole run: implementation under test, and reference for yrs.</summary>
public sealed class SidecarFixture : IDisposable
{
    public YjsSidecarCodec Codec { get; } = new(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "client", "sidecar", "yjs-sidecar.ts")));

    public void Dispose() => Codec.Dispose();
}

[CollectionDefinition(Name)]
public sealed class YjsCollection : ICollectionFixture<SidecarFixture>
{
    public const string Name = "yjs";
}

/// <summary>
/// What any server-side Yjs implementation must do for compaction, sync and search. Expectations come
/// from real Yjs (the fixtures, or the sidecar as reference), so a failure means the server would
/// diverge from the browsers.
/// </summary>
public abstract class CodecContractTests(SidecarFixture yjs, ITestOutputHelper output)
{
    public static TheoryData<string> Fixtures => ["tiptap-small", "tiptap-10k", "no-format-2k", "insert-only-2k"];

    protected abstract ICrdtCodec Codec { get; }

    /// <summary>Short name used in output file names and logs.</summary>
    protected abstract string Name { get; }

    /// <summary>Real Yjs, to check outputs the way a browser would read them.</summary>
    protected YjsSidecarCodec Reference => yjs.Codec;

    protected ITestOutputHelper Output => output;

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Merged_log_gives_the_same_plain_text_as_prosemirror(string name)
    {
        var fixture = Fixture.Load(name);

        Assert.Equal(fixture.ExpectedText, Codec.ExtractText(Codec.Merge(fixture.UpdateBytes)));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Merged_state_vector_matches_yjs(string name)
    {
        var fixture = Fixture.Load(name);

        var stateVector = Codec.StateVector(Codec.Merge(fixture.UpdateBytes));

        Assert.Equal(fixture.ExpectedStateVector, Fixture.DecodeStateVector(stateVector));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Merging_a_snapshot_again_changes_nothing(string name)
    {
        var merged = Codec.Merge(Fixture.Load(name).UpdateBytes);

        // Compaction may run twice on the same page.
        Assert.Equal(merged, Codec.Merge([merged]));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Snapshot_plus_updates_it_already_contains_gives_the_same_text(string name)
    {
        var fixture = Fixture.Load(name);
        var merged = Codec.Merge(fixture.UpdateBytes);

        // A crash between writing a snapshot and removing merged updates leaves both behind.
        Assert.Equal(fixture.ExpectedText, Codec.ExtractText(Codec.Merge([merged, .. fixture.UpdateBytes])));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Incremental_compaction_snapshot_plus_newer_updates_gives_the_same_text(string name)
    {
        var fixture = Fixture.Load(name);
        var snapshot = Codec.Merge(fixture.UpdateBytes.Take(fixture.PartialCount));
        File.WriteAllBytes(OutputFile(name, "partial-snapshot"), snapshot);

        // Compaction folds new updates into the previous snapshot, again and again.
        var merged = Codec.Merge([snapshot, .. fixture.UpdateBytes.Skip(fixture.PartialCount)]);

        Assert.Equal(fixture.ExpectedText, Codec.ExtractText(merged));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Server_side_diff_read_by_yjs_brings_a_half_synced_client_up_to_date(string name)
    {
        var fixture = Fixture.Load(name);
        var full = Codec.Merge(fixture.UpdateBytes);
        var diff = Codec.Diff(full, Convert.FromBase64String(fixture.PartialStateVector));
        File.WriteAllBytes(OutputFile(name, "merged"), full);
        File.WriteAllBytes(OutputFile(name, "diff"), diff);

        // The browser applies the diff with Yjs, so Yjs is what must get it right.
        var clientState = Reference.Merge(fixture.UpdateBytes.Take(fixture.PartialCount));

        Assert.True(diff.Length < full.Length, $"diff {diff.Length} bytes should be smaller than full state {full.Length}");
        Assert.Equal(fixture.ExpectedText, Reference.ExtractText(Reference.Merge([clientState, diff])));
    }

    [Fact]
    public void Ten_thousand_updates_compact_quickly()
    {
        var fixture = Fixture.Load("tiptap-10k");
        var updates = fixture.UpdateBytes;
        Codec.Merge(updates.Take(10)); // warm up (native library, sidecar JIT)

        var stopwatch = Stopwatch.StartNew();
        var merged = Codec.Merge(updates);
        var mergeMs = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        var text = Codec.ExtractText(merged);
        var extractMs = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        Codec.Diff(merged, Convert.FromBase64String(fixture.PartialStateVector));
        var diffMs = stopwatch.Elapsed.TotalMilliseconds;

        output.WriteLine($"[{Name}] {updates.Count} updates ({updates.Sum(u => u.Length)} bytes): merge {mergeMs:F1} ms -> {merged.Length} bytes; text {extractMs:F1} ms; diff {diffMs:F1} ms");
        output.WriteLine($"[{Name}] Yjs reference: encodeStateAsUpdate {fixture.JsStateBytes} bytes; mergeUpdates {fixture.JsMergeMs} ms -> {fixture.JsMergeBytes} bytes");

        Assert.Equal(fixture.ExpectedText, text);
        Assert.InRange(mergeMs, 0, 5_000);
    }

    protected string OutputFile(string fixture, string kind) =>
        Path.Combine(Fixture.OutputDirectory, $"{Name}-{fixture}.{kind}.bin");

    protected static string Escape(string value) =>
        string.Concat(value.Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:x4}"));
}

[Collection(YjsCollection.Name)]
public sealed class YjsSidecarCodecTests(SidecarFixture yjs, ITestOutputHelper output) : CodecContractTests(yjs, output)
{
    protected override ICrdtCodec Codec => Reference;

    protected override string Name => "yjs-sidecar";

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Updates_applied_out_of_order_give_the_same_document(string name)
    {
        var fixture = Fixture.Load(name);

        // Updates whose dependencies are missing must wait until they arrive.
        var shuffled = fixture.UpdateBytes.OrderBy(_ => Random.Shared.Next()).ToList();
        var fromShuffled = Codec.Merge(shuffled);

        Assert.Equal(fixture.ExpectedStateVector, Fixture.DecodeStateVector(Codec.StateVector(fromShuffled)));
        Assert.Equal(fixture.ExpectedText, Codec.ExtractText(fromShuffled));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Merged_diff_applied_on_top_of_existing_state_gives_the_same_text(string name)
    {
        var fixture = Fixture.Load(name);
        var full = Codec.Merge(fixture.UpdateBytes);
        var clientState = Codec.Merge(fixture.UpdateBytes.Take(fixture.PartialCount));

        // A merged multi-client update on top of a snapshot, e.g. a reconnecting client's diff after a restore.
        var diff = Codec.Diff(full, Convert.FromBase64String(fixture.PartialStateVector));

        Assert.Equal(fixture.ExpectedText, Codec.ExtractText(Codec.Merge([clientState, diff])));
    }

    [Fact]
    public void Deletion_that_arrives_before_its_content_is_applied()
    {
        var repro = Fixture.LoadRepro();

        Assert.Equal(repro.YjsText, Codec.ExtractText(Codec.Merge(repro.Updates)));
    }

    [Fact]
    public void Split_surrogate_pair_gives_the_same_text_as_yjs()
    {
        // Both texts pass through JSON, which turns a lone surrogate into U+FFFD the same way.
        var fixture = Fixture.LoadSurrogateSplit();

        Assert.Equal(fixture.ExpectedText, Codec.ExtractText(Codec.Merge(fixture.Updates)));
    }

    [Fact]
    public void Sidecar_memory_after_compacting_ten_thousand_updates()
    {
        Codec.Merge(Fixture.Load("tiptap-10k").UpdateBytes);

        var (current, peak) = Reference.Memory;
        Output.WriteLine($"[yjs-sidecar] working set {current / 1024 / 1024} MB, peak {peak / 1024 / 1024} MB");
        Assert.InRange(peak, 1, 500L * 1024 * 1024);
    }
}

/// <summary>
/// yrs (YDotNet 0.6.0) passes the contract, but diverges from Yjs where it has to apply updates whose
/// content is not there yet. These tests pin today's behaviour: when one starts failing, a newer
/// YDotNet changed it and yrs is worth evaluating again (docs/runs/0.4.md).
/// </summary>
[Collection(YjsCollection.Name)]
public sealed class YrsCodecTests(SidecarFixture yjs, ITestOutputHelper output) : CodecContractTests(yjs, output)
{
    protected override ICrdtCodec Codec { get; } = new YrsCodec();

    protected override string Name => "yrs";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Known_divergence_yrs_drops_a_deletion_that_arrives_before_its_content(bool garbageCollect)
    {
        // Two updates: a delete set for 3 characters, then the content it deletes. Yjs keeps the
        // deletion pending until the content arrives; yrs loses it, with or without garbage collection.
        var repro = Fixture.LoadRepro();
        var codec = new YrsCodec(garbageCollect);

        var text = codec.ExtractText(codec.Merge(repro.Updates));

        Assert.Equal(repro.YrsText, text);
        Assert.NotEqual(repro.YjsText, text);
    }

    [Fact]
    public void Known_divergence_yrs_drops_the_character_after_a_split_emoji()
    {
        // Yjs deleted the low surrogate of 😀 in "a😀b": Yjs shows "a�b", yrs keeps the emoji and loses "b".
        var fixture = Fixture.LoadSurrogateSplit();

        var text = Codec.ExtractText(Codec.Merge(fixture.Updates));

        Output.WriteLine($"[yrs] Yjs: {Escape(fixture.ExpectedText)}, yrs: {Escape(text)}");
        Assert.Equal("a\U0001F600", text);
    }

    [Fact]
    public void Known_divergence_yrs_mis_applies_a_merged_diff_on_top_of_existing_state()
    {
        // The same operation passes in Yjs (YjsSidecarCodecTests); here the merged result loses edits.
        var fixture = Fixture.Load("tiptap-10k");
        var full = Codec.Merge(fixture.UpdateBytes);
        var clientState = Codec.Merge(fixture.UpdateBytes.Take(fixture.PartialCount));
        var diff = Codec.Diff(full, Convert.FromBase64String(fixture.PartialStateVector));

        Assert.NotEqual(fixture.ExpectedText, Codec.ExtractText(Codec.Merge([clientState, diff])));
    }
}
