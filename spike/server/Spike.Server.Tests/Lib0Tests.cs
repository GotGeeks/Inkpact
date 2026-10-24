using Spike.Server.Protocol;

namespace Spike.Server.Tests;

public sealed class Lib0Tests
{
    [Fact]
    public void Encoding_matches_lib0_js_byte_for_byte()
    {
        // Produced by lib0 in Node: writeVarUint(300), writeVarString("é"),
        // writeVarUint8Array([1, 2]), writeVarUint(2 ** 32).
        var expected = Convert.FromHexString("ac0202c3a90201028080808010");

        var writer = new Lib0Writer();
        writer.WriteVarUint(300);
        writer.WriteVarString("é");
        writer.WriteVarUint8Array([1, 2]);
        writer.WriteVarUint(1UL << 32);

        Assert.Equal(expected, writer.ToArray());
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(1UL << 53)]
    public void VarUint_round_trips(ulong value)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(value);
        var reader = new Lib0Reader(writer.ToArray());

        Assert.Equal(value, reader.ReadVarUint());
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void Truncated_input_is_a_protocol_error()
    {
        Assert.Throws<ProtocolException>(() => new Lib0Reader(new byte[] { 0x80 }).ReadVarUint());
        Assert.Throws<ProtocolException>(() => new Lib0Reader(new byte[] { 5, 1, 2 }).ReadVarUint8Array());
    }

    [Fact]
    public void Frame_round_trips_update_and_sync()
    {
        var update = Frame.Parse(Frame.Encode(MessageType.Update, "page-1", [9, 8, 7]));
        Assert.Equal(MessageType.Update, update.Type);
        Assert.Equal("page-1", update.PageId);
        Assert.Equal([9, 8, 7], update.Payload.ToArray());

        var sync = Frame.Parse(Frame.EncodeSync("page-2", [[1], [2, 3]]));
        Assert.Equal(MessageType.Sync, sync.Type);
        Assert.Equal([[1], [2, 3]], Frame.ReadSyncUpdates(sync.Payload));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown-type")]
    [InlineData("trailing-bytes")]
    public void Invalid_frames_are_rejected(string kind)
    {
        byte[] frame = kind switch
        {
            "" => Frame.Encode(MessageType.Subscribe, "x")[..1],
            "unknown-type" => [9, 1, (byte)'x'],
            _ => [.. Frame.Encode(MessageType.Subscribe, "x"), 0],
        };

        Assert.Throws<ProtocolException>(() => Frame.Parse(frame));
    }

    [Fact]
    public void Awareness_entries_round_trip()
    {
        AwarenessEntry[] entries = [new(7, 3, """{"user":{"name":"jos"}}"""), new(8, 1, AwarenessEntry.Removed)];

        Assert.Equal(entries, AwarenessEntry.Parse(AwarenessEntry.Encode(entries)));
    }
}
