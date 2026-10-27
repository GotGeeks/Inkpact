namespace Inkpact.Api.Attachments;

/// <summary>
/// The SHA-256 of a stored file, as lower-case hex, its size, and its first bytes (up to <see cref="HeadLength"/>), so
/// the type can be told without reading the file again.
/// </summary>
public sealed record StoredFile(string Sha256, long Size)
{
    public const int HeadLength = 16;

    public byte[] Head { get; init; } = [];

    // The head follows from the content, as the hash does: the same hash is the same file.
    public bool Equals(StoredFile? other) => other is not null && Sha256 == other.Sha256 && Size == other.Size;

    public override int GetHashCode() => HashCode.Combine(Sha256, Size);
}

/// <summary>A file was larger than the limit it was stored with. Nothing of it is kept.</summary>
public sealed class FileTooLargeException(long maxSize) : Exception($"The file is larger than {maxSize} bytes.")
{
    public long MaxSize { get; } = maxSize;
}

/// <summary>
/// Where the bytes of attachments live, addressed by their SHA-256: the same content is stored once, however often it
/// is added. A local directory in phase 1A; an S3-compatible store can replace it without changing any caller.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>
    /// Reads the stream to its end and stores it, unless a file with that content is already there. An empty stream
    /// is not stored (nothing could refer to it). Throws <see cref="FileTooLargeException"/> as soon as more than
    /// <paramref name="maxSize"/> bytes arrive.
    /// </summary>
    Task<StoredFile> PutAsync(Stream content, long maxSize, CancellationToken cancellationToken = default);

    /// <summary>The stored file, or null when there is none with that hash.</summary>
    Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default);
}
