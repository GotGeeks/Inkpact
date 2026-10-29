using Inkpact.Api.Sync;

namespace Inkpact.Api.Crdt;

/// <summary>
/// Reads Yjs state vectors (<c>[varuint count][count × (varuint client, varuint clock)]</c>) without Yjs, to tell whether a
/// page has been written to since a state vector was taken. The order the clients are written in does not matter here.
/// </summary>
public static class StateVectors
{
    /// <summary>The clock of every client in the state vector, or null when the bytes are not a state vector.</summary>
    public static Dictionary<ulong, ulong>? TryDecode(ReadOnlyMemory<byte> stateVector)
    {
        try
        {
            var reader = new Lib0Reader(stateVector);
            var count = reader.ReadVarUint();
            var clocks = new Dictionary<ulong, ulong>();
            for (ulong i = 0; i < count; i++)
            {
                var client = reader.ReadVarUint();
                clocks[client] = reader.ReadVarUint();
            }

            return reader.HasMore ? null : clocks;
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="current"/> holds anything that <paramref name="stored"/> does not: a client it has not heard
    /// of, or a client that has written more. Bytes that are not a state vector count as gone past: when in doubt, the page
    /// was changed. A deletion alone does not move a state vector; that is what the page fingerprint is for.
    /// </summary>
    public static bool HasGonePast(ReadOnlyMemory<byte> current, ReadOnlyMemory<byte> stored)
    {
        if (TryDecode(current) is not { } now || TryDecode(stored) is not { } before)
        {
            return true;
        }

        return now.Any(client => client.Value > before.GetValueOrDefault(client.Key));
    }
}
