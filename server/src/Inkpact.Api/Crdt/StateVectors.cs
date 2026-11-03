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
    /// The state vector inside a fingerprint as the sidecar makes it (<c>Y.encodeSnapshot</c>: the delete set,
    /// <c>[varuint clients][clients × (client, [varuint ranges][ranges × (clock, length)])]</c>, then the state vector), or
    /// null when the bytes are not one.
    /// </summary>
    public static byte[]? OfFingerprint(ReadOnlyMemory<byte> fingerprint)
    {
        try
        {
            var reader = new Lib0Reader(fingerprint);
            var clients = reader.ReadVarUint();
            for (ulong i = 0; i < clients; i++)
            {
                reader.ReadVarUint();
                var ranges = reader.ReadVarUint();
                for (ulong j = 0; j < ranges; j++)
                {
                    reader.ReadVarUint();
                    reader.ReadVarUint();
                }
            }

            var stateVector = fingerprint[reader.Position..].ToArray();
            return TryDecode(stateVector) is null ? null : stateVector;
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
