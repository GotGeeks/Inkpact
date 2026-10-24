namespace Spike.Server.Protocol;

public enum MessageType : byte
{
    Subscribe = 0,
    Unsubscribe = 1,
    Update = 2,
    Sync = 3,
    Awareness = 4,
}

/// <summary>
/// One message on the multiplexed socket: <c>[varuint type][varstring pageId][payload]</c>.
/// For Update and Awareness, <see cref="Payload"/> is the inner byte array; for Sync it is
/// the raw rest of the frame (read it with <see cref="Frame.ReadSyncUpdates"/>).
/// </summary>
public sealed record Frame(MessageType Type, string PageId, ReadOnlyMemory<byte> Payload)
{
    public const int MaxPageIdLength = 128;

    public static Frame Parse(ReadOnlyMemory<byte> message)
    {
        var reader = new Lib0Reader(message);
        var rawType = reader.ReadVarUint();
        if (rawType > (ulong)MessageType.Awareness)
        {
            throw new ProtocolException($"Unknown message type {rawType}");
        }

        var type = (MessageType)rawType;
        var pageId = reader.ReadVarString();
        if (pageId.Length is 0 or > MaxPageIdLength)
        {
            throw new ProtocolException("Invalid page id");
        }

        var payload = type switch
        {
            MessageType.Update or MessageType.Awareness => reader.ReadVarUint8Array(),
            MessageType.Sync => message[reader.Position..],
            _ => ReadOnlyMemory<byte>.Empty,
        };

        if (type != MessageType.Sync && reader.HasMore)
        {
            throw new ProtocolException("Trailing bytes after frame");
        }

        return new Frame(type, pageId, payload);
    }

    public static IReadOnlyList<byte[]> ReadSyncUpdates(ReadOnlyMemory<byte> payload)
    {
        var reader = new Lib0Reader(payload);
        var count = reader.ReadVarUint();
        var updates = new List<byte[]>();
        for (ulong i = 0; i < count; i++)
        {
            updates.Add(reader.ReadVarUint8Array().ToArray());
        }

        return updates;
    }

    public static byte[] Encode(MessageType type, string pageId) => Start(type, pageId).ToArray();

    public static byte[] Encode(MessageType type, string pageId, ReadOnlySpan<byte> payload)
    {
        var writer = Start(type, pageId);
        writer.WriteVarUint8Array(payload);
        return writer.ToArray();
    }

    public static byte[] EncodeSync(string pageId, IReadOnlyList<byte[]> updates)
    {
        var writer = Start(MessageType.Sync, pageId);
        writer.WriteVarUint((ulong)updates.Count);
        foreach (var update in updates)
        {
            writer.WriteVarUint8Array(update);
        }

        return writer.ToArray();
    }

    private static Lib0Writer Start(MessageType type, string pageId)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint((ulong)type);
        writer.WriteVarString(pageId);
        return writer;
    }
}
