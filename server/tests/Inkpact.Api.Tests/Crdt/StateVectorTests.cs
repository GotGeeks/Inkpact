using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Crdt;
using Inkpact.Api.Sync;

namespace Inkpact.Api.Tests.Crdt;

/// <summary>Reading Yjs state vectors in C#, to tell whether a page was written to since one was taken.</summary>
public sealed class StateVectorTests
{
    private static byte[] Encode(IEnumerable<KeyValuePair<ulong, ulong>> clocks)
    {
        var list = clocks.ToList();
        var writer = new Lib0Writer();
        writer.WriteVarUint((ulong)list.Count);
        foreach (var (client, clock) in list)
        {
            writer.WriteVarUint(client);
            writer.WriteVarUint(clock);
        }

        return writer.ToArray();
    }

    /// <summary>Random clocks for a few clients, ids as large as Yjs makes them (32 bits), written in any order.</summary>
    private static Gen<Dictionary<ulong, ulong>> Clocks() =>
        Gen.ArrayOf(Gen.Zip(Gen.Choose(1, 6), Gen.Choose(0, 300)), 5)
            .Select(pairs => pairs
                .GroupBy(p => (ulong)p.Item1 * 700_000_001UL)
                .ToDictionary(g => g.Key, g => (ulong)g.Last().Item2));

    [Fact]
    public void A_state_vector_is_read_whatever_the_order_of_its_clients()
    {
        var property = Prop.ForAll(Arb.From(Clocks().SelectMany(clocks => Gen.Shuffle(clocks.ToArray()).Select(order => (clocks, order)))), pair =>
            StateVectors.TryDecode(Encode(pair.order))!.OrderBy(c => c.Key).SequenceEqual(pair.clocks.OrderBy(c => c.Key)));

        Check.One(Config.QuickThrowOnFailure.WithQuietOnSuccess(true), property);
    }

    [Fact]
    public void A_page_has_gone_past_a_state_vector_when_a_client_has_written_more_or_one_is_new()
    {
        var pairs = Arb.From(Gen.Zip(Clocks(), Clocks()), _ => []);
        var property = Prop.ForAll(pairs, pair =>
        {
            var (stored, current) = pair;
            var expected = current.Any(c => c.Value > stored.GetValueOrDefault(c.Key));
            // Written in descending order as Yjs does, and in ascending order: the answer is the same.
            var actual = StateVectors.HasGonePast(
                Encode(current.OrderByDescending(c => c.Key)), Encode(stored.OrderBy(c => c.Key)));
            return actual == expected;
        });

        Check.One(Config.QuickThrowOnFailure.WithQuietOnSuccess(true), property);
    }

    [Fact]
    public void A_state_vector_that_is_the_same_has_not_been_gone_past()
    {
        var bytes = Encode(new Dictionary<ulong, ulong> { [1] = 2, [3_000_000_000] = 41 });

        StateVectors.HasGonePast(bytes, bytes).ShouldBeFalse();
        StateVectors.HasGonePast(Encode([]), bytes).ShouldBeFalse();
    }

    [Fact]
    public void A_client_that_wrote_one_more_item_or_that_is_new_counts_as_gone_past()
    {
        var stored = Encode(new Dictionary<ulong, ulong> { [1] = 2, [9] = 4 });

        StateVectors.HasGonePast(Encode(new Dictionary<ulong, ulong> { [1] = 2, [9] = 5 }), stored).ShouldBeTrue();
        StateVectors.HasGonePast(Encode(new Dictionary<ulong, ulong> { [1] = 2, [9] = 4, [77] = 1 }), stored).ShouldBeTrue();
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x01 })]
    [InlineData(new byte[] { 0x01, 0x01, 0x01, 0xFF })]
    [InlineData(new byte[] { 0x00, 0x00 })]
    public void Bytes_that_are_not_a_state_vector_count_as_gone_past(byte[] garbage)
    {
        StateVectors.TryDecode(garbage).ShouldBeNull();
        StateVectors.HasGonePast(garbage, Encode([])).ShouldBeTrue();
        StateVectors.HasGonePast(Encode([]), garbage).ShouldBeTrue();
    }
}
