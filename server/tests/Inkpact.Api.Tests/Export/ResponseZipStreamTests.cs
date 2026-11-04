using System.Text;
using Inkpact.Api.Export;

namespace Inkpact.Api.Tests.Export;

public sealed class ResponseZipStreamTests
{
    /// <summary>A response body as Kestrel has it: asynchronous writes only. Counts the writes it is given.</summary>
    private sealed class AsyncOnlyStream : Stream
    {
        public MemoryStream Received { get; } = new();

        public int Writes { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Received.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public string Text => Encoding.UTF8.GetString(Received.ToArray());
    }

    [Fact]
    public async Task Synchronous_and_small_writes_are_gathered_and_go_out_in_order_with_the_flush()
    {
        var response = new AsyncOnlyStream();
        await using var stream = new ResponseZipStream(_ => Task.FromResult<Stream>(response));

        await stream.WriteAsync("one "u8.ToArray());
        stream.Write("two "u8);
        stream.Write("three "u8.ToArray(), 0, 6);
        stream.Flush();
        await stream.WriteAsync("four"u8.ToArray());

        response.Writes.ShouldBe(0);
        stream.Pending.ShouldBe(18);

        await stream.FlushAsync();

        stream.Pending.ShouldBe(0);
        response.Writes.ShouldBe(1);
        response.Text.ShouldBe("one two three four");
    }

    [Fact]
    public async Task Many_small_entries_are_sent_as_few_large_writes()
    {
        var response = new AsyncOnlyStream();
        await using var stream = new ResponseZipStream(_ => Task.FromResult<Stream>(response));
        var small = new byte[300];

        for (var i = 0; i < 5_000; i++)
        {
            await stream.WriteAsync(small);
        }

        await stream.FlushAsync();

        response.Received.Length.ShouldBe(5_000 * 300);
        response.Writes.ShouldBeLessThanOrEqualTo((5_000 * 300 / ResponseZipStream.ChunkSize) + 1);
    }

    [Fact]
    public async Task A_large_write_goes_straight_through_after_what_was_gathered()
    {
        var response = new AsyncOnlyStream();
        await using var stream = new ResponseZipStream(_ => Task.FromResult<Stream>(response));
        var large = Enumerable.Repeat((byte)'b', ResponseZipStream.ChunkSize * 3).ToArray();

        await stream.WriteAsync("a"u8.ToArray());
        await stream.WriteAsync(large);

        stream.Pending.ShouldBe(0);
        response.Writes.ShouldBe(2);
        response.Received.ToArray()[0].ShouldBe((byte)'a');
        response.Received.Length.ShouldBe(1 + large.Length);
    }

    [Fact]
    public async Task The_response_is_opened_with_the_first_bytes_sent_and_not_before()
    {
        var response = new AsyncOnlyStream();
        var opened = 0;
        await using var stream = new ResponseZipStream(_ =>
        {
            opened++;
            return Task.FromResult<Stream>(response);
        });

        stream.Write("held"u8);
        await stream.WriteAsync("small"u8.ToArray());
        opened.ShouldBe(0);
        stream.HasSent.ShouldBeFalse();

        await stream.FlushAsync();
        await stream.FlushAsync();

        opened.ShouldBe(1);
        stream.HasSent.ShouldBeTrue();
        response.Text.ShouldBe("heldsmall");
    }

    [Fact]
    public async Task A_flush_with_nothing_written_opens_nothing()
    {
        var opened = false;
        await using var stream = new ResponseZipStream(_ =>
        {
            opened = true;
            return Task.FromResult<Stream>(new AsyncOnlyStream());
        });

        await stream.FlushAsync();

        opened.ShouldBeFalse();
    }

    [Fact]
    public async Task What_is_pending_is_not_sent_when_the_stream_is_dropped_and_the_response_is_left_open()
    {
        var response = new AsyncOnlyStream();
        var stream = new ResponseZipStream(_ => Task.FromResult<Stream>(response));
        stream.Write("never sent"u8);

        await stream.DisposeAsync();
        stream.Dispose();

        response.Received.Length.ShouldBe(0);
        response.CanWrite.ShouldBeTrue();
    }
}
