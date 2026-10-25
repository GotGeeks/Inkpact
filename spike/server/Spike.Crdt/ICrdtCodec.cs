namespace Spike.Crdt;

/// <summary>
/// The Yjs operations the server needs: compaction (merge), sync step 2 (diff) and search text.
/// All updates and state vectors use v1 encoding, as Yjs and y-protocols do.
/// </summary>
public interface ICrdtCodec
{
    /// <summary>Applies every update in order and returns the document's full state as one update.</summary>
    byte[] Merge(IEnumerable<byte[]> updates);

    /// <summary>
    /// Same result as <see cref="Merge"/>, reached the other way round: every update is applied in its own
    /// transaction, as Yjs' <c>applyUpdate</c> does. Compaction uses it to verify a merge against an
    /// independent replay.
    /// </summary>
    byte[] Replay(IEnumerable<byte[]> updates);

    /// <summary>
    /// Bytes that are equal exactly when two states are the same CRDT document: the same updates integrated
    /// (state vector) and the same deletions (delete set). Whatever the content or schema, so compaction can
    /// verify pages that are not TipTap documents.
    /// </summary>
    byte[] Fingerprint(byte[] state);

    /// <summary>What a client with <paramref name="stateVector"/> is missing.</summary>
    byte[] Diff(byte[] state, byte[] stateVector);

    byte[] StateVector(byte[] state);

    /// <summary>Plain text as ProseMirror's <c>textBetween(0, size, "\n")</c> gives it.</summary>
    string ExtractText(byte[] state);
}
