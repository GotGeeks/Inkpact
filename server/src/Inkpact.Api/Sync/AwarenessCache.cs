namespace Inkpact.Api.Sync;

/// <summary>One entry of a y-protocols awareness update. <see cref="State"/> is JSON; "null" means removed.</summary>
public sealed record AwarenessEntry(ulong ClientId, ulong Clock, string State)
{
    public const string Removed = "null";

    /// <summary>Format: varuint count, then per entry varuint clientID, varuint clock, varstring JSON state.</summary>
    public static IReadOnlyList<AwarenessEntry> Parse(ReadOnlyMemory<byte> update)
    {
        var reader = new Lib0Reader(update);
        var count = reader.ReadVarUint();
        var entries = new List<AwarenessEntry>();
        for (ulong i = 0; i < count; i++)
        {
            entries.Add(new AwarenessEntry(reader.ReadVarUint(), reader.ReadVarUint(), reader.ReadVarString()));
        }

        if (reader.HasMore)
        {
            throw new ProtocolException("Trailing bytes after awareness update");
        }

        return entries;
    }

    public static byte[] Encode(IReadOnlyCollection<AwarenessEntry> entries)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint((ulong)entries.Count);
        foreach (var entry in entries)
        {
            writer.WriteVarUint(entry.ClientId);
            writer.WriteVarUint(entry.Clock);
            writer.WriteVarString(entry.State);
        }

        return writer.ToArray();
    }
}

/// <summary>
/// The latest presence per awareness client on one page, in memory only, never stored. A new subscriber sees at once
/// who is there, and when a connection drops the server announces the removal instead of peers waiting 30 s for the
/// awareness timeout. Not thread-safe: the hub uses it under the page lock.
/// </summary>
public sealed class AwarenessCache
{
    private readonly Dictionary<ulong, (AwarenessEntry Entry, object Owner)> _entries = [];

    public int Count => _entries.Count;

    public void Apply(IReadOnlyList<AwarenessEntry> update, object owner)
    {
        foreach (var entry in update)
        {
            var known = _entries.TryGetValue(entry.ClientId, out var current);
            if (known && entry.Clock < current.Entry.Clock)
            {
                continue;
            }

            if (entry.State == AwarenessEntry.Removed)
            {
                _entries.Remove(entry.ClientId);
            }
            else
            {
                _entries[entry.ClientId] = (entry, owner);
            }
        }
    }

    /// <summary>An awareness update with every known client, or null when nobody is there.</summary>
    public byte[]? EncodeAll() =>
        _entries.Count == 0 ? null : AwarenessEntry.Encode([.. _entries.Values.Select(value => value.Entry)]);

    /// <summary>
    /// Forgets the clients of <paramref name="owner"/> and returns the update that removes them for peers, or null when
    /// it had none. The removal keeps the same clock: y-protocols applies a null state at an equal clock, and a later
    /// reconnect of that client (clock + 1) is then accepted again.
    /// </summary>
    public byte[]? RemoveOwner(object owner)
    {
        var removed = _entries.Values
            .Where(value => value.Owner == owner)
            .Select(value => value.Entry with { State = AwarenessEntry.Removed })
            .ToList();

        foreach (var entry in removed)
        {
            _entries.Remove(entry.ClientId);
        }

        return removed.Count == 0 ? null : AwarenessEntry.Encode(removed);
    }
}
