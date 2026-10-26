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
