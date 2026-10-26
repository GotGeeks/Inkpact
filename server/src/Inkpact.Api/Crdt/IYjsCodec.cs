namespace Inkpact.Api.Crdt;

/// <summary>What a client with a given state vector is missing, and the state vector of the page as the server has it.</summary>
public sealed record YjsDiff(byte[] Update, byte[] StateVector);

/// <summary>
/// The result of replaying a page's snapshot and updates. The two fingerprints are those of the inputs replayed and of
/// <see cref="State"/> decoded on its own fresh document; equal fingerprints mean the encoding lost nothing.
/// </summary>
public sealed record YjsCompaction(
    byte[] State, byte[] StateVector, byte[] ReplayFingerprint, byte[] StateFingerprint, string Text);

/// <summary>
/// Everything the server does with the inside of a Yjs update. Updates are opaque to the rest of the app: this is the
/// only place that decodes them. Every method either answers, or throws one of the two exceptions below, within its
/// timeout.
/// </summary>
public interface IYjsCodec
{
    /// <summary>
    /// What a client holding <paramref name="stateVector"/> is missing of the page made by <paramref name="updates"/>.
    /// An empty state vector means the client has nothing and gets the full state.
    /// </summary>
    Task<YjsDiff> DiffAsync(ReadOnlyMemory<byte> stateVector, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken);

    /// <summary>Replays <paramref name="inputs"/> (the snapshot first, if any, then the updates) into one state.</summary>
    Task<YjsCompaction> CompactAsync(IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken);

    /// <summary>The fingerprint of replaying <paramref name="updates"/> in order: the state vector and the delete set.</summary>
    Task<byte[]> FingerprintAsync(IReadOnlyList<byte[]> updates, CancellationToken cancellationToken);
}

/// <summary>
/// The Yjs sidecar cannot answer right now: it is not running, died while the call waited, or took too long. Expected
/// while it restarts; nothing is wrong with the input.
/// </summary>
public sealed class YjsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The sidecar answered with an error: the input does not decode. Trying again gives the same answer.</summary>
public sealed class YjsRejectedException(string message) : Exception(message);
