namespace Inkpact.Api.Sync;

public enum MessageType : byte
{
    /// <summary>Client: start receiving a page. The payload is the client's Yjs state vector, which may be empty.</summary>
    Subscribe = 0,

    /// <summary>Client: stop receiving a page. No payload.</summary>
    Unsubscribe = 1,

    /// <summary>Both ways: one Yjs update, opaque to the server.</summary>
    Update = 2,

    /// <summary>Server: the answer to a subscribe, the stored updates of the page in order.</summary>
    Sync = 3,

    /// <summary>Both ways: a y-protocols awareness update (who is there, cursors). Relayed, never stored.</summary>
    Awareness = 4,

    /// <summary>Client, a single byte without page id: is the connection alive?</summary>
    Ping = 5,

    /// <summary>Server, a single byte without page id: the answer to <see cref="Ping"/>.</summary>
    Pong = 6,

    /// <summary>Server: something about one page was refused. The payload is an error code for the client to translate.</summary>
    Error = 7,
}

/// <summary>Error codes in <see cref="MessageType.Error"/> frames.</summary>
public static class SyncErrors
{
    /// <summary>No such page, or no access to it; the two are not told apart, as in the REST API.</summary>
    public const string PageNotFound = "page_not_found";

    /// <summary>The user may read the page but not change it. The update was not stored.</summary>
    public const string ReadOnly = "read_only";

    /// <summary>The connection already receives as many pages as it may.</summary>
    public const string TooManyPages = "too_many_pages";
}

/// <summary>
/// One message from a client on the multiplexed socket: <c>[varuint type][varstring pageId][payload]</c>, where the
/// payload of Subscribe, Update and Awareness is one lib0 byte array and Unsubscribe has none. Ping is the single type
/// byte and is handled before parsing.
/// </summary>
public sealed record Frame(MessageType Type, Guid PageId, ReadOnlyMemory<byte> Payload)
{
    public static Frame Parse(ReadOnlyMemory<byte> message)
    {
        var reader = new Lib0Reader(message);
        var type = reader.ReadVarUint() switch
        {
            (ulong)MessageType.Subscribe => MessageType.Subscribe,
            (ulong)MessageType.Unsubscribe => MessageType.Unsubscribe,
            (ulong)MessageType.Update => MessageType.Update,
            (ulong)MessageType.Awareness => MessageType.Awareness,
            var other => throw new ProtocolException($"Clients may not send message type {other}"),
        };

        // Only the canonical form, so that one page never shows up under two names.
        var rawId = reader.ReadVarString();
        if (!Guid.TryParseExact(rawId, "D", out var pageId) || rawId != pageId.ToString("D"))
        {
            throw new ProtocolException("Invalid page id");
        }

        var payload = type == MessageType.Unsubscribe ? ReadOnlyMemory<byte>.Empty : reader.ReadVarUint8Array();
        if (reader.HasMore)
        {
            throw new ProtocolException("Trailing bytes after frame");
        }

        return new Frame(type, pageId, payload);
    }

    public static byte[] Encode(MessageType type, Guid pageId, ReadOnlySpan<byte> payload)
    {
        var writer = Start(type, pageId);
        writer.WriteVarUint8Array(payload);
        return writer.ToArray();
    }

    public static byte[] EncodeSync(Guid pageId, IReadOnlyList<byte[]> updates)
    {
        var writer = Start(MessageType.Sync, pageId);
        writer.WriteVarUint((ulong)updates.Count);
        foreach (var update in updates)
        {
            writer.WriteVarUint8Array(update);
        }

        return writer.ToArray();
    }

    public static byte[] EncodeError(Guid pageId, string code)
    {
        var writer = Start(MessageType.Error, pageId);
        writer.WriteVarString(code);
        return writer.ToArray();
    }

    private static Lib0Writer Start(MessageType type, Guid pageId)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint((ulong)type);
        writer.WriteVarString(pageId.ToString("D"));
        return writer;
    }
}
