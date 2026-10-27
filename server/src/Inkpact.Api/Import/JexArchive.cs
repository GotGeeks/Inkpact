using System.Formats.Tar;
using System.Text;
using System.Text.RegularExpressions;

namespace Inkpact.Api.Import;

/// <summary>A JEX file the import cannot use. The code is what the client translates.</summary>
internal sealed class JexException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed record JexFolder(string Id, string ParentId, string Title, bool Encrypted, bool Trashed, long Offset, long Length);

internal sealed record JexNote(
    string Id,
    string ParentId,
    string Title,
    bool IsHtml,
    bool IsTodo,
    DateTimeOffset? Due,
    DateTimeOffset? Completed,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    bool Encrypted,
    bool Conflict,
    bool Trashed,
    long Offset,
    long Length);

internal sealed record JexTag(string Id, string Title, bool Encrypted, long Offset, long Length);

internal sealed record JexResource(string Id, string Title, string FileName, string Extension, long DataOffset, long DataLength);

/// <summary>An item that is bigger than the importer reads into memory. Only its id and title are known.</summary>
internal sealed record JexOversized(string Id, string Title, long Length);

/// <summary>Everything in a JEX file that the import goes through, found in one pass over the archive.</summary>
internal sealed class JexIndex(string path)
{
    public string Path { get; } = path;

    public Dictionary<string, JexFolder> Folders { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, JexNote> Notes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, JexTag> Tags { get; } = new(StringComparer.Ordinal);

    /// <summary>The resources the export has the bytes of, by id.</summary>
    public Dictionary<string, JexResource> Resources { get; } = new(StringComparer.Ordinal);

    /// <summary>For every note, the tags on it (ids).</summary>
    public Dictionary<string, List<string>> TagsOfNote { get; } = new(StringComparer.Ordinal);

    public List<JexOversized> Oversized { get; } = [];

    /// <summary>Items of kinds the import does not use (settings, searches, revisions, keys), and files that are not items.</summary>
    public int Skipped { get; set; }

    /// <summary>Items that could not be read: not an item, or without an id.</summary>
    public int Unreadable { get; set; }
}

/// <summary>
/// Reads a JEX file: an uncompressed tar of Joplin's items, one <c>&lt;id&gt;.md</c> each, and the bytes of resources under
/// <c>resources/</c>. Nothing is extracted: entries are read from the archive's stream, only entries named like an id
/// are looked at, and a resource's bytes are not read at all until a note needs them.
/// </summary>
internal static partial class JexArchive
{
    /// <summary>The largest item (a note, with its body) that is read into memory. Bigger ones are reported and skipped.</summary>
    public const long MaxItemSize = 64L * 1024 * 1024;

    /// <summary>The most entries a file may have. A real export has a few per note.</summary>
    public const int MaxEntries = 500_000;

    [GeneratedRegex(@"^(?:\./)?(?<id>[0-9a-f]{32})\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemName();

    [GeneratedRegex(@"^(?:\./)?resources/(?<id>[0-9a-f]{32})(?:\.[^/\\]{0,40})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceName();

    /// <summary>
    /// Goes through the archive once and returns what is in it. Throws <see cref="JexException"/> when it is not a tar, is
    /// cut off, has too many entries, or has no item of the kinds the import uses.
    /// </summary>
    public static async Task<JexIndex> ReadIndexAsync(
        string path, CancellationToken cancellationToken, long maxItemSize = MaxItemSize, int maxEntries = MaxEntries)
    {
        var index = new JexIndex(path);
        var resourceItems = new Dictionary<string, JexItem>(StringComparer.Ordinal);
        var entries = 0;
        try
        {
            await using var file = OpenRead(path);
            await using var reader = new TarReader(file);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (++entries > maxEntries)
                {
                    throw new JexException("import_too_many_items", $"The archive has more than {maxEntries} entries.");
                }

                // Only plain files; a link or a device is never followed.
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                {
                    continue;
                }

                if (ResourceName().Match(entry.Name) is { Success: true } resource)
                {
                    index.Resources[resource.Groups["id"].Value] = new JexResource(resource.Groups["id"].Value, "", "", "", entry.DataOffset, entry.Length);
                }
                else if (ItemName().Match(entry.Name) is { Success: true } item)
                {
                    await ReadItemAsync(index, resourceItems, item.Groups["id"].Value, entry, maxItemSize, cancellationToken);
                }
                else
                {
                    index.Skipped++;
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or FormatException or ArgumentException or NotSupportedException)
        {
            throw new JexException("import_invalid", $"The file is not a readable tar archive: {e.Message}");
        }

        // The bytes of a resource are in the archive on their own; its title, name and extension are in its item.
        foreach (var (id, item) in resourceItems)
        {
            var data = index.Resources.GetValueOrDefault(id);
            index.Resources[id] = new JexResource(
                id,
                item.Title,
                item.Get("filename"),
                item.Get("file_extension"),
                data?.DataOffset ?? -1,
                data?.DataLength ?? 0);
        }

        // Bytes that no item describes are not a resource of the export.
        foreach (var id in index.Resources.Keys.Where(id => !resourceItems.ContainsKey(id)).ToList())
        {
            index.Resources.Remove(id);
        }

        if (index.Notes.Count + index.Folders.Count + index.Tags.Count + index.Oversized.Count == 0)
        {
            throw new JexException("import_empty", "The archive has no notes, notebooks or tags.");
        }

        return index;
    }

    private static async Task ReadItemAsync(
        JexIndex index, Dictionary<string, JexItem> resourceItems, string id, TarEntry entry, long maxItemSize, CancellationToken cancellationToken)
    {
        if (entry.Length > maxItemSize)
        {
            // Too big to read: the first line is the title, and that is all that is needed to say what was skipped.
            var head = new byte[4096];
            var read = await entry.DataStream!.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
            var title = Encoding.UTF8.GetString(head, 0, read).Split('\n', 2)[0].TrimEnd('\r');
            index.Oversized.Add(new JexOversized(id, ContentRules.CleanText(title), entry.Length));
            return;
        }

        var bytes = new byte[entry.Length];
        await entry.DataStream!.ReadExactlyAsync(bytes, cancellationToken);
        var item = JexItem.Parse(Encoding.UTF8.GetString(bytes));
        if (item is null || item.Id != id || !JexItem.IsId(item.Id))
        {
            index.Unreadable++;
            return;
        }

        var offset = entry.DataOffset;
        var length = entry.Length;
        switch (item.Type)
        {
            case JexTypes.Note:
                index.Notes[id] = new JexNote(
                    id,
                    item.Get("parent_id"),
                    item.Title,
                    IsHtml: item.Get("markup_language") == "2",
                    IsTodo: item.Flag("is_todo"),
                    Due: item.Time("todo_due"),
                    Completed: item.Time("todo_completed"),
                    Created: item.Time("user_created_time") ?? item.Time("created_time"),
                    Updated: item.Time("user_updated_time") ?? item.Time("updated_time"),
                    Encrypted: item.Flag("encryption_applied"),
                    Conflict: item.Flag("is_conflict"),
                    Trashed: item.Time("deleted_time") is not null,
                    offset,
                    length);
                break;
            case JexTypes.Folder:
                index.Folders[id] = new JexFolder(
                    id, item.Get("parent_id"), item.Title, item.Flag("encryption_applied"), item.Time("deleted_time") is not null, offset, length);
                break;
            case JexTypes.Tag:
                index.Tags[id] = new JexTag(id, item.Title, item.Flag("encryption_applied"), offset, length);
                break;
            case JexTypes.Resource:
                resourceItems[id] = item;
                break;
            case JexTypes.NoteTag:
                if (JexItem.IsId(item.Get("note_id")) && JexItem.IsId(item.Get("tag_id")))
                {
                    if (!index.TagsOfNote.TryGetValue(item.Get("note_id"), out var tags))
                    {
                        index.TagsOfNote[item.Get("note_id")] = tags = [];
                    }

                    tags.Add(item.Get("tag_id"));
                }

                break;
            default:
                index.Skipped++;
                break;
        }
    }

    /// <summary>Whether the file starts like a tar archive: a first entry that reads. A quick check at upload; the job reads the rest.</summary>
    public static async Task<bool> LooksLikeTarAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = OpenRead(path);
            await using var reader = new TarReader(file);
            return await reader.GetNextEntryAsync(copyData: false, cancellationToken) is not null;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or FormatException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The bytes of an item's file, as they were when the archive was indexed. Null when they are gone or cut off.</summary>
    public static async Task<byte[]?> ReadItemBytesAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        await using var file = OpenRead(path);
        file.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[length];
        try
        {
            await file.ReadExactlyAsync(bytes, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        return bytes;
    }

    /// <summary>A stream over part of the archive, for a resource's bytes. The caller disposes it.</summary>
    public static Stream OpenSlice(string path, long offset, long length)
    {
        var file = OpenRead(path);
        file.Seek(offset, SeekOrigin.Begin);
        return new SliceStream(file, length);
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>The first <paramref name="length"/> bytes of a stream, then the end; disposes the stream with itself.</summary>
    private sealed class SliceStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
