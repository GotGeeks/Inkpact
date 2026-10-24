namespace Spike.Crdt;

/// <summary>
/// The Yjs operations the server needs: compaction (merge), sync step 2 (diff) and search text.
/// All updates and state vectors use v1 encoding, as Yjs and y-protocols do.
/// </summary>
public interface ICrdtCodec
{
    /// <summary>Applies every update in order and returns the document's full state as one update.</summary>
    byte[] Merge(IEnumerable<byte[]> updates);

    /// <summary>What a client with <paramref name="stateVector"/> is missing.</summary>
    byte[] Diff(byte[] state, byte[] stateVector);

    byte[] StateVector(byte[] state);

    /// <summary>Plain text as ProseMirror's <c>textBetween(0, size, "\n")</c> gives it.</summary>
    string ExtractText(byte[] state);
}
