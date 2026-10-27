using System.Buffers;
using System.Security.Cryptography;

namespace Inkpact.Api.Attachments;

/// <summary>
/// Attachments in a local directory, as <c>&lt;root&gt;/ab/cd/&lt;sha256&gt;</c>. A file is written to a temporary name
/// while it is hashed and moved into place only when it is complete, so a file under its hash is always whole. Two
/// uploads of the same content at once both end up pointing at one file: it does not matter which move wins.
/// </summary>
public sealed class LocalAttachmentStore : IAttachmentStore
{
    private const int BufferSize = 81920;

    private readonly string _root;
    private readonly string _temporary;

    public LocalAttachmentStore(AttachmentOptions options)
    {
        _root = Path.GetFullPath(options.Directory);
        _temporary = Path.Combine(_root, "tmp");
        CreateDirectory(_root);
        CreateDirectory(_temporary);

        // Left by an upload that was cut off by a crash or a restart. Nothing refers to them.
        foreach (var leftover in Directory.EnumerateFiles(_temporary))
        {
            TryDelete(leftover);
        }
    }

    public async Task<StoredFile> PutAsync(Stream content, long maxSize, CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(_temporary, Guid.NewGuid().ToString("N"));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            var head = new byte[StoredFile.HeadLength];
            var headLength = 0;
            await using (var file = new FileStream(temporary, FileOptionsFor(FileMode.CreateNew)))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int read;
                    while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                    {
                        size += read;
                        if (size > maxSize)
                        {
                            throw new FileTooLargeException(maxSize);
                        }

                        if (headLength < head.Length)
                        {
                            var take = Math.Min(read, head.Length - headLength);
                            buffer.AsSpan(0, take).CopyTo(head.AsSpan(headLength));
                            headLength += take;
                        }

                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                // On disk before it gets its name: a crash after the move must not leave a short file under a hash.
                file.Flush(flushToDisk: true);
            }

            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            var stored = new StoredFile(sha256, size) { Head = head[..headLength] };
            if (size == 0)
            {
                TryDelete(temporary);
                return stored;
            }

            var target = PathFor(sha256);
            CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                TryDelete(temporary);
            }
            else
            {
                try
                {
                    File.Move(temporary, target);
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Another upload of the same content got there first.
                    TryDelete(temporary);
                }
            }

            return stored;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        if (!IsSha256(sha256))
        {
            return Task.FromResult<Stream?>(null);
        }

        try
        {
            return Task.FromResult<Stream?>(new FileStream(PathFor(sha256), FileOptionsFor(FileMode.Open)));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    /// <summary>Where the file with this hash lives. Two levels of 256 directories keep each one small.</summary>
    public string PathFor(string sha256) => Path.Combine(_root, sha256[..2], sha256[2..4], sha256);

    /// <summary>Only a hash names a file, so nothing read from the database can point outside the root.</summary>
    internal static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static FileStreamOptions FileOptionsFor(FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = mode == FileMode.Open ? FileAccess.Read : FileAccess.Write,
            Share = mode == FileMode.Open ? FileShare.Read : FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous,
        };
        if (mode != FileMode.Open && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still open somewhere, or already gone; the next start removes what is left.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
