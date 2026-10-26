using Inkpact.Api.Sync;

namespace Inkpact.Api.Tests.Sync;

public class FrameTests
{
    private static readonly Guid PageId = Guid.Parse("01a17706-23f9-79fc-9593-7b0a68b17933");

    private static byte[] Raw(ulong type, string pageId, byte[]? payload = null, bool withPayload = true)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(type);
        writer.WriteVarString(pageId);
        if (withPayload)
        {
            writer.WriteVarUint8Array(payload ?? []);
        }

        return writer.ToArray();
    }

    [Theory]
    [InlineData(MessageType.Subscribe)]
    [InlineData(MessageType.Update)]
    [InlineData(MessageType.Awareness)]
    public void A_frame_with_a_payload_reads_back_as_it_was_written(MessageType type)
    {
        var frame = Frame.Parse(Frame.Encode(type, PageId, [1, 2, 3]));

        frame.Type.ShouldBe(type);
        frame.PageId.ShouldBe(PageId);
        frame.Payload.ToArray().ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void An_unsubscribe_has_no_payload()
    {
        var frame = Frame.Parse(Raw((ulong)MessageType.Unsubscribe, PageId.ToString(), withPayload: false));

        frame.Type.ShouldBe(MessageType.Unsubscribe);
        frame.Payload.Length.ShouldBe(0);
    }

    [Fact]
    public void The_wire_format_is_type_then_page_id_then_payload_in_lib0()
    {
        var bytes = Frame.Encode(MessageType.Update, PageId, [0xAA]);

        bytes[0].ShouldBe((byte)MessageType.Update);
        bytes[1].ShouldBe((byte)36); // the length of the page id, as a varuint
        System.Text.Encoding.UTF8.GetString(bytes, 2, 36).ShouldBe(PageId.ToString("D"));
        bytes[38..].ShouldBe([1, 0xAA]);
    }

    [Fact]
    public void A_sync_answer_holds_the_state_vector_the_count_and_each_update()
    {
        var bytes = Frame.EncodeSync(PageId, [9, 8, 7], [[1], [2, 3]]);

        var reader = new Lib0Reader(bytes);
        reader.ReadVarUint().ShouldBe((ulong)MessageType.Sync);
        reader.ReadVarString().ShouldBe(PageId.ToString("D"));
        reader.ReadVarUint8Array().ToArray().ShouldBe([9, 8, 7]);
        reader.ReadVarUint().ShouldBe(2UL);
        reader.ReadVarUint8Array().ToArray().ShouldBe([1]);
        reader.ReadVarUint8Array().ToArray().ShouldBe([2, 3]);
        reader.HasMore.ShouldBeFalse();
    }

    [Fact]
    public void A_sync_answer_in_log_mode_has_an_empty_state_vector()
    {
        var reader = new Lib0Reader(Frame.EncodeSync(PageId, [], [[1]]));

        reader.ReadVarUint().ShouldBe((ulong)MessageType.Sync);
        reader.ReadVarString().ShouldBe(PageId.ToString("D"));
        reader.ReadVarUint8Array().Length.ShouldBe(0);
        reader.ReadVarUint().ShouldBe(1UL);
    }

    [Fact]
    public void An_error_holds_the_page_and_the_code()
    {
        var reader = new Lib0Reader(Frame.EncodeError(PageId, SyncErrors.ReadOnly));

        reader.ReadVarUint().ShouldBe((ulong)MessageType.Error);
        reader.ReadVarString().ShouldBe(PageId.ToString("D"));
        reader.ReadVarString().ShouldBe(SyncErrors.ReadOnly);
    }

    [Theory]
    [InlineData(MessageType.Sync)]
    [InlineData(MessageType.Ping)]
    [InlineData(MessageType.Pong)]
    [InlineData(MessageType.Error)]
    [InlineData((MessageType)42)]
    public void Types_a_client_may_not_send_are_refused(MessageType type)
    {
        Should.Throw<ProtocolException>(() => Frame.Parse(Raw((ulong)type, PageId.ToString())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("01A17706-23F9-79FC-9593-7B0A68B17933")] // upper case
    [InlineData("{01a17706-23f9-79fc-9593-7b0a68b17933}")]
    [InlineData("01a1770623f979fc95937b0a68b17933")]
    public void A_page_id_must_be_a_guid_in_its_one_canonical_spelling(string pageId)
    {
        Should.Throw<ProtocolException>(() => Frame.Parse(Raw((ulong)MessageType.Update, pageId, [1])));
    }

    [Fact]
    public void Trailing_bytes_are_refused()
    {
        Should.Throw<ProtocolException>(() => Frame.Parse((byte[])[.. Frame.Encode(MessageType.Update, PageId, [1]), 0]));
    }

    [Fact]
    public void A_frame_cut_short_is_refused()
    {
        var bytes = Frame.Encode(MessageType.Update, PageId, [1, 2, 3]);

        for (var length = 0; length < bytes.Length; length++)
        {
            Should.Throw<ProtocolException>(() => Frame.Parse(bytes[..length]), $"cut at {length}");
        }
    }

    [Fact]
    public void Invalid_utf8_in_the_page_id_is_refused()
    {
        Should.Throw<ProtocolException>(() => Frame.Parse((byte[])[(byte)MessageType.Update, 2, 0xC3, 0x28, 0]));
    }

    [Fact]
    public void A_varuint_longer_than_javascript_allows_is_refused()
    {
        Should.Throw<ProtocolException>(() => new Lib0Reader(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }).ReadVarUint());
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(300UL)]
    [InlineData((1UL << 53) - 1)]
    public void Varuints_round_trip(ulong value)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(value);

        new Lib0Reader(writer.ToArray()).ReadVarUint().ShouldBe(value);
    }
}
