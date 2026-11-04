namespace Inkpact.Api.Export;

/// <summary>
/// The stream a <see cref="System.IO.Compression.ZipArchive"/> writes to when the zip goes straight into a response.
/// <list type="bullet">
/// <item>Kestrel refuses synchronous writes, and the archive still makes a few when it closes an entry (the end of the
/// compressed data, the entry's trailer), because the stream it hands out is closed with a synchronous call. Those bytes are
/// kept and go out with the next asynchronous write or flush, in order, so nothing is written synchronously to the response.</item>
/// <item>Small writes are gathered up to <see cref="ChunkSize"/> before they are sent, so 5,000 small pages are not 5,000 small
/// network writes. A write of a chunk or more (a file being copied) goes straight through after what is gathered.</item>
/// <item>The response is opened (its headers set, the audit line written) by <c>open</c>, when the first bytes are sent and not
/// before: until then nothing has left the server, and a failure can still be answered with an error.</item>
/// </list>
/// Memory stays the size of a chunk, not of a file.
/// </summary>
internal sealed class ResponseZipStream(Func<CancellationToken, Task<Stream>> open) : Stream
{
    public const int ChunkSize = 64 * 1024;

    private readonly MemoryStream _pending = new();
    private Stream? _inner;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Whether anything has been sent: from then on the answer cannot change.</summary>
    public bool HasSent => _inner is not null;

    /// <summary>Bytes written but not sent yet. At most a chunk and an entry's tail; the tests look at it.</summary>
    public long Pending => _pending.Length;

    public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pending.Length + buffer.Length < ChunkSize)
        {
            _pending.Write(buffer.Span);
            return;
        }

        await SendPendingAsync(cancellationToken);
        if (buffer.Length >= ChunkSize)
        {
            await (await InnerAsync(cancellationToken)).WriteAsync(buffer, cancellationToken);
        }
        else
        {
            _pending.Write(buffer.Span);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // A synchronous flush has nothing it can do without blocking; the bytes go with the next asynchronous call.
    public override void Flush()
    {
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await SendPendingAsync(cancellationToken);
        if (_inner is not null)
        {
            await _inner.FlushAsync(cancellationToken);
        }
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        // The response is not this stream's to close, and bytes still pending are not sent: the caller flushes when it means to.
        if (disposing)
        {
            _pending.Dispose();
        }

        base.Dispose(disposing);
    }

    private async ValueTask SendPendingAsync(CancellationToken cancellationToken)
    {
        if (_pending.Length == 0)
        {
            return;
        }

        await (await InnerAsync(cancellationToken)).WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), cancellationToken);
        _pending.SetLength(0);
    }

    private async ValueTask<Stream> InnerAsync(CancellationToken cancellationToken) => _inner ??= await open(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
