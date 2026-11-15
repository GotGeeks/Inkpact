namespace Inkpact.Api.Sync;

/// <summary>Limits of the sync hub, from the <c>Sync</c> configuration section. The defaults suit one person typing fast.</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>One message from a client, after reassembly. Text only: images are attachments, referenced by id.</summary>
    public int MaxMessageBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Messages a connection may send per second, on average. Every keystroke and cursor move is one.</summary>
    public int MessagesPerSecond { get; set; } = 100;

    /// <summary>Messages a connection may send at once before the average applies, for a reconnect or a paste.</summary>
    public int MessageBurst { get; set; } = 500;

    public int MaxPagesPerConnection { get; set; } = 100;

    /// <summary>Open sockets per user, over all their browsers and tabs.</summary>
    public int MaxConnectionsPerUser { get; set; } = 20;

    /// <summary>Messages waiting to go out to one client. A client that falls this far behind is dropped and resyncs.</summary>
    public int MaxQueuedMessages { get; set; } = 1000;

    public long MaxQueuedBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// The largest page that can be opened: half of <see cref="MaxQueuedBytes"/>, since a client that opens a page is sent
    /// its whole state in one reply that has to fit the queue with the traffic around it. Exactly half, also when that is
    /// less than a sync message: a larger page could not be opened. The Joplin import limits the notes it converts to it. Worked out, not configured, so that what the import makes and
    /// what a copy may be (<see cref="MaxCopyBytes"/>) cannot drift apart. Run 1.21.
    /// </summary>
    public long MaxPageBytes => MaxQueuedBytes / 2;

    /// <summary>
    /// The largest content the server takes when a client makes a page with it, which is the copy of edits that a Sync would
    /// delete (<c>POST /api/workspaces/{id}/pages</c> with content): <see cref="MaxPageBytes"/> and an eighth more. The copy
    /// is written again from the blocks of the page, which can come out a little larger than the update the page was made
    /// from (a few bytes a block, 0.14 % in a test with 3,000 blocks); without the eighth, a page right at the limit would
    /// get no copy. A page of this size still opens: the queue has twice the room of <see cref="MaxPageBytes"/>.
    /// </summary>
    public long MaxCopyBytes => MaxPageBytes + MaxPageBytes / 8;

    /// <summary>
    /// The most a page's stored bytes (its snapshot and its live updates) may come to: three quarters of
    /// <see cref="MaxQueuedBytes"/>, 12 MiB by default. A client that opens a page whose log is not compacted yet is sent all
    /// of it in one reply, which then still fits the queue with the traffic around it. The hub refuses a live update that
    /// would take a page past it (run B.10); the Joplin import and a restored version keep to it with a replacement and the
    /// page's stored updates together. Worked out, not configured.
    /// </summary>
    public long MaxStoredBytes => MaxQueuedBytes / 4 * 3;

    /// <summary>
    /// An update this small (1/4096 of <see cref="MaxQueuedBytes"/>, 4 KiB by default) is still taken past
    /// <see cref="MaxStoredBytes"/>, up to <see cref="MaxStoredBytesForSmallUpdates"/>: a page at the limit can be made
    /// smaller only by deleting text, which takes a few bytes, and the next compaction drops what was deleted. The hub does
    /// not decode updates, so it cannot tell a deletion from typing; the size keeps a paste out of the margin, and typing a
    /// whole MiB of it takes hours. Run B.10.
    /// </summary>
    public long SmallUpdateBytes => MaxQueuedBytes / 4096;

    /// <summary>
    /// How far small updates may take a page: 13/16 of <see cref="MaxQueuedBytes"/>, 13 MiB by default, which still fits the
    /// queue with the traffic around it. Run B.10.
    /// </summary>
    public long MaxStoredBytesForSmallUpdates => MaxQueuedBytes / 16 * 13;

    /// <summary>
    /// A page whose stored updates are larger than this is sent as stored when a client subscribes, without asking the Yjs
    /// sidecar for a diff: the diff of a large page can take longer than <c>Sidecar:DiffTimeout</c>, and a sidecar that does
    /// not answer in time is restarted, which every page and import waits for. A client opening such a page has little of
    /// it anyway, so the diff would save little. Run 1.16d.
    /// </summary>
    public long MaxDiffBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>The server pings an idle socket this often, so proxies keep it open and dead ones are noticed.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A socket that does not answer the server's ping within this time is closed.</summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How often an open socket checks that its session still exists. Logout closes sockets at once; this catches
    /// sessions that expired or were removed some other way.
    /// </summary>
    public TimeSpan SessionCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a client gets to answer the server's close before the socket is dropped.</summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
