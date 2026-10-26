using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Inkpact.Api.Crdt;

/// <summary>The operations of the sidecar protocol, as in <c>server/sidecar/src/ops.ts</c>.</summary>
internal enum SidecarOp : byte
{
    Ping = 0,
    Diff = 1,
    Compact = 2,
    Fingerprint = 3,
}

/// <summary>
/// One running sidecar process and the conversation with it. All integers are little-endian u32:
/// <c>request: [length][id][op: u8][count][count × ([length][bytes])]</c> and
/// <c>response: [length][id][status: u8, 0 = ok][count][count × ([length][bytes])]</c>, where <c>length</c> counts the
/// bytes after it. Requests are answered in order, but responses are matched by id anyway. When the process is gone
/// (exited, killed, or its output no longer makes sense) the session is over for good; the supervisor starts a new one.
/// </summary>
internal sealed class SidecarSession : IDisposable
{
    /// <summary>The sidecar treats a bigger frame as a broken stream and exits.</summary>
    public const int MaxFrameBytes = 256 * 1024 * 1024;

    private const int HeaderBytes = 4 + 4 + 1 + 4;
    private const int MaxResponseParts = 16;
    private const int ErrorLinesKept = 20;

    private readonly Process _process;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, Pending> _pending = new();
    private readonly Queue<string> _errorLines = new();
    private readonly Task _reading;
    private readonly Task _errors;
    private long _sequence;
    private uint _lastId;
    private volatile bool _ended;
    private string? _killReason;

    private SidecarSession(Process process, TimeProvider time, ILogger logger)
    {
        _process = process;
        _time = time;
        _logger = logger;
        ProcessId = process.Id;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        Exited = process.WaitForExitAsync();
        _reading = Task.Run(ReadResponsesAsync);
        _errors = Task.Run(LogErrorsAsync);
    }

    public int ProcessId { get; }

    /// <summary>Completes when the process is gone, however that came about.</summary>
    public Task Exited { get; }

    /// <summary>Why this server killed the process, or null if it ended on its own.</summary>
    public string? KillReason => _killReason;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static SidecarSession Start(ProcessStartInfo info, TimeProvider time, ILogger logger)
    {
        var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start");
        return new SidecarSession(process, time, logger);
    }

    public string LastErrorLines
    {
        get
        {
            lock (_errorLines)
            {
                return string.Join(Environment.NewLine, _errorLines);
            }
        }
    }

    /// <summary>
    /// Sends one request and waits for its answer. Throws <see cref="YjsUnavailableException"/> when the process is gone,
    /// dies meanwhile or takes longer than <paramref name="timeout"/>, and <see cref="YjsRejectedException"/> when it
    /// answers with an error. A timeout kills the process when the late request is the one it is working on: Node
    /// handles one request at a time, so a stuck request blocks all the others.
    /// </summary>
    public async Task<byte[][]> CallAsync(
        SidecarOp op, IReadOnlyList<ReadOnlyMemory<byte>> parts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ended || Exited.IsCompleted)
        {
            throw new YjsUnavailableException("The Yjs sidecar is not running");
        }

        long size = HeaderBytes;
        foreach (var part in parts)
        {
            size += 4 + part.Length;
        }

        if (size - 4 > MaxFrameBytes)
        {
            throw new YjsRejectedException($"The request is {size} bytes, over the {MaxFrameBytes} the sidecar accepts");
        }

        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        var frame = ArrayPool<byte>.Shared.Rent((int)size);
        Encode(frame, op, parts);
        var pending = await SendAsync(frame, (int)size, timeoutSource.Token);

        // Only after a write that finished: a cancelled one may still have the buffer, so that one is left to the GC.
        ArrayPool<byte>.Shared.Return(frame);

        Response response;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
            response = await pending.Completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            // A request that waits behind a slower one is only abandoned: the one in front has its own timeout.
            if (_pending.Values.All(other => other.Sequence >= pending.Sequence))
            {
                Kill($"a {op} request took longer than {timeout.TotalSeconds:0.###} s");
            }

            throw new YjsUnavailableException($"The Yjs sidecar did not answer a {op} request in {timeout.TotalSeconds:0.###} s");
        }

        if (response.Failure is not null)
        {
            throw response.Failure;
        }

        if (!response.Ok)
        {
            throw new YjsRejectedException(response.Parts is [var message, ..] ? Encoding.UTF8.GetString(message) : "The Yjs sidecar refused the request");
        }

        return response.Parts;
    }

    /// <summary>Ends the process: closes its input so it exits by itself, and kills it if it does not within <paramref name="grace"/>.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        try
        {
            _stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Closed already.
        }

        try
        {
            await Exited.WaitAsync(grace, _time);
        }
        catch (TimeoutException)
        {
            Kill("it did not stop when its input was closed");
            await Exited;
        }
    }

    public void Kill(string reason)
    {
        if (Interlocked.CompareExchange(ref _killReason, reason, null) is null)
        {
            _logger.LogWarning("Killing the Yjs sidecar (process {ProcessId}): {Reason}", ProcessId, reason);
        }

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited already.
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Could not kill the Yjs sidecar (process {ProcessId})", ProcessId);
        }
    }

    /// <summary>Waits for the reading loops to end (they do once the process is gone), then fails every call still waiting.</summary>
    public async Task EndAsync()
    {
        await Exited;
        await Task.WhenAll(_reading, _errors).WaitAsync(TimeSpan.FromSeconds(5), _time).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Before the sweep, so a call that registers meanwhile either sees the flag or is seen by the sweep.
        _ended = true;
        var failure = new YjsUnavailableException("The Yjs sidecar stopped");
        foreach (var pending in _pending.Values)
        {
            pending.Completion.TrySetResult(new Response(true, [], failure));
        }

        _pending.Clear();
    }

    public void Dispose() => _process.Dispose();

    private static void Encode(byte[] frame, SidecarOp op, IReadOnlyList<ReadOnlyMemory<byte>> parts)
    {
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0); // the length, below
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0); // the id, once the write lock is held
        span[8] = (byte)op;
        BinaryPrimitives.WriteUInt32LittleEndian(span[9..], (uint)parts.Count);
        var position = HeaderBytes;
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[position..], (uint)part.Length);
            part.Span.CopyTo(span[(position + 4)..]);
            position += 4 + part.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)(position - 4));
    }

    private async Task<Pending> SendAsync(byte[] frame, int size, CancellationToken timeout)
    {
        // One frame at a time. Waiting here is no reason to kill anything: whoever holds the lock has its own timeout.
        try
        {
            await _writeLock.WaitAsync(timeout);
        }
        catch (OperationCanceledException)
        {
            throw new YjsUnavailableException("The Yjs sidecar did not take the request in time");
        }

        try
        {
            var id = ++_lastId;
            var pending = new Pending(Interlocked.Increment(ref _sequence));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), id);
            _pending[id] = pending;
            if (_ended)
            {
                _pending.TryRemove(id, out _);
                throw new YjsUnavailableException("The Yjs sidecar stopped");
            }

            try
            {
                await _stdin.WriteAsync(frame.AsMemory(0, size), timeout);
                await _stdin.FlushAsync(timeout);
            }
            catch (OperationCanceledException)
            {
                // Part of the frame may be written, and the process is not reading: it cannot be used again.
                Kill("it did not read its input in time");
                throw new YjsUnavailableException("The Yjs sidecar did not read the request in time");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _pending.TryRemove(id, out _);
                throw new YjsUnavailableException("The Yjs sidecar closed its input", ex);
            }

            return pending;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadResponsesAsync()
    {
        var header = new byte[HeaderBytes];
        try
        {
            while (true)
            {
                if (!await TryReadAsync(header.AsMemory(0, 4)))
                {
                    return; // The process closed its output: it is exiting.
                }

                var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (length is < HeaderBytes - 4 or > MaxFrameBytes)
                {
                    throw new InvalidDataException($"A response claims a length of {length} bytes");
                }

                await _stdout.ReadExactlyAsync(header.AsMemory(4, HeaderBytes - 4));
                var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var ok = header[8] == 0;
                var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(9));
                if (count > MaxResponseParts)
                {
                    throw new InvalidDataException($"A response has {count} parts");
                }

                var remaining = (long)length - (HeaderBytes - 4);
                var parts = new byte[count][];
                var lengthBytes = new byte[4];
                for (var i = 0; i < parts.Length; i++)
                {
                    remaining -= 4;
                    await _stdout.ReadExactlyAsync(lengthBytes);
                    var partLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
                    if (remaining < 0 || partLength > remaining)
                    {
                        throw new InvalidDataException("A response part runs past the end of its frame");
                    }

                    parts[i] = new byte[partLength];
                    await _stdout.ReadExactlyAsync(parts[i]);
                    remaining -= partLength;
                }

                if (remaining != 0)
                {
                    throw new InvalidDataException("A response has bytes after its last part");
                }

                if (_pending.TryRemove(id, out var pending))
                {
                    pending.Completion.TrySetResult(new Response(ok, parts, null));
                }
                else
                {
                    _logger.LogDebug("The Yjs sidecar answered request {Id}, which nobody waits for", id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            // Not the usual end (the process exiting, or killed on purpose): the stream is broken, so end the process.
            if (_killReason is null)
            {
                _logger.LogError(ex, "Reading the Yjs sidecar's answers failed");
                Kill("its output could not be read");
            }
        }
    }

    /// <summary>Fills the buffer. False when the stream ended before the first byte; a stream that ends in the middle throws.</summary>
    private async Task<bool> TryReadAsync(Memory<byte> buffer)
    {
        var read = await _stdout.ReadAsync(buffer);
        if (read == 0)
        {
            return false;
        }

        await _stdout.ReadExactlyAsync(buffer[read..]);
        return true;
    }

    private async Task LogErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                lock (_errorLines)
                {
                    if (_errorLines.Count == ErrorLinesKept)
                    {
                        _errorLines.Dequeue();
                    }

                    _errorLines.Enqueue(line);
                }

                _logger.LogWarning("Yjs sidecar (process {ProcessId}): {Line}", ProcessId, line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process is gone.
        }
    }

    private sealed class Pending(long sequence)
    {
        /// <summary>Order of writing: the lowest is the request Node is working on.</summary>
        public long Sequence { get; } = sequence;

        public TaskCompletionSource<Response> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>An answer, or the reason there will be none. Not an exception on the task, which nobody who gave up would observe.</summary>
    private sealed record Response(bool Ok, byte[][] Parts, Exception? Failure);
}
