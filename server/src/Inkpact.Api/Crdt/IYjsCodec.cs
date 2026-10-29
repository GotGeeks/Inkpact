namespace Inkpact.Api.Crdt;

/// <summary>What a client with a given state vector is missing, and the state vector of the page as the server has it.</summary>
public sealed record YjsDiff(byte[] Update, byte[] StateVector);

/// <summary>
/// The update that replaces a page's content, and the page right after it: its state vector, its fingerprint (as
/// <see cref="IYjsCodec.FingerprintAsync"/> gives it) and its text for search.
/// </summary>
public sealed record YjsReplacement(byte[] Update, byte[] StateVector, byte[] Fingerprint, string Text);

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

    /// <summary>
    /// The Yjs update of a new page whose content is <paramref name="content"/>, a ProseMirror document as JSON
    /// (<c>{"type":"doc","content":[...]}</c>). It starts from the page's fixed first-paragraph update, so a client that applies
    /// that update afterwards changes nothing. Content with a node, mark or attribute that the editor does not know is
    /// rejected with <see cref="YjsRejectedException"/>.
    /// </summary>
    Task<byte[]> BuildAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>
    /// The Yjs update that replaces the content of an existing page, made of <paramref name="inputs"/> (its snapshot first,
    /// if any, then its updates), with <paramref name="content"/> (a ProseMirror document as JSON). It deletes every block the
    /// page has and writes the new content after them, and holds only what the inputs lack. An edit made meanwhile is merged
    /// only where it does not depend on a deleted block: text typed into an old block is deleted with it, so the caller makes
    /// sure the page has no update the inputs lack (see <c>SyncHub.PublishAsync</c>). The same content rules as
    /// <see cref="BuildAsync"/> apply, and are refused the same way. The sidecar checks the update by replaying the inputs and
    /// it, and answers with the page after it as well.
    /// </summary>
    Task<YjsReplacement> ReplaceAsync(IReadOnlyList<byte[]> inputs, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

/// <summary>
/// The Yjs sidecar cannot answer right now: it is not running, died while the call waited, or took too long. Expected
/// while it restarts; nothing is wrong with the input.
/// </summary>
public sealed class YjsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The sidecar answered with an error: the input does not decode. Trying again gives the same answer.</summary>
public sealed class YjsRejectedException(string message) : Exception(message)
{
    /// <summary>What the sidecar's builder says before the reason when it refuses content (<c>InvalidContentError</c> in build.ts).</summary>
    public const string InvalidContentPrefix = "invalid content: ";

    /// <summary>The content given to build or replace a page was refused, rather than the page or the request.</summary>
    public bool IsInvalidContent => Message.StartsWith(InvalidContentPrefix, StringComparison.Ordinal);
}
