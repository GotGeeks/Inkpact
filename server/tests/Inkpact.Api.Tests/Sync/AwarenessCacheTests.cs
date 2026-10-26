using Inkpact.Api.Sync;

namespace Inkpact.Api.Tests.Sync;

public class AwarenessCacheTests
{
    private readonly object _alice = new();
    private readonly object _bob = new();

    [Fact]
    public void An_entry_round_trips_through_the_y_protocols_format()
    {
        AwarenessEntry[] entries = [new(1, 2, """{"a":1}"""), new(3, 4, AwarenessEntry.Removed)];

        AwarenessEntry.Parse(AwarenessEntry.Encode(entries)).ShouldBe(entries);
    }

    [Fact]
    public void A_newer_clock_replaces_an_older_one_and_an_older_one_is_ignored()
    {
        var cache = new AwarenessCache();

        cache.Apply([new(1, 5, "\"new\"")], _alice);
        cache.Apply([new(1, 4, "\"old\"")], _alice);

        AwarenessEntry.Parse(cache.EncodeAll()!).ShouldBe([new AwarenessEntry(1, 5, "\"new\"")]);
    }

    [Fact]
    public void A_null_state_removes_the_client()
    {
        var cache = new AwarenessCache();
        cache.Apply([new(1, 1, "{}")], _alice);

        cache.Apply([new(1, 2, AwarenessEntry.Removed)], _alice);

        cache.EncodeAll().ShouldBeNull();
    }

    [Fact]
    public void Removing_an_owner_removes_only_its_clients_at_their_own_clock()
    {
        var cache = new AwarenessCache();
        cache.Apply([new(1, 3, "{}"), new(2, 7, "{}")], _alice);
        cache.Apply([new(9, 1, "{}")], _bob);

        var removal = cache.RemoveOwner(_alice);

        AwarenessEntry.Parse(removal!).OrderBy(e => e.ClientId)
            .ShouldBe([new AwarenessEntry(1, 3, AwarenessEntry.Removed), new AwarenessEntry(2, 7, AwarenessEntry.Removed)]);
        cache.Count.ShouldBe(1);
        cache.RemoveOwner(_alice).ShouldBeNull();
    }

    [Fact]
    public void Trailing_bytes_after_the_entries_are_refused()
    {
        Should.Throw<ProtocolException>(() => AwarenessEntry.Parse((byte[])[.. AwarenessEntry.Encode([new(1, 1, "{}")]), 0]));
    }
}
