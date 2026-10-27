using System.Formats.Tar;
using System.Text;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// Writes a JEX file the way Joplin does: a tar with one <c>&lt;id&gt;.md</c> per item (title, a blank line, the body for a
/// note, a blank line, and <c>key: value</c> lines ending in <c>type_</c>), and a resource's bytes under
/// <c>resources/</c>. Ids are 32 hex digits; a test gives short names and the builder pads them.
/// </summary>
public sealed class JexBuilder
{
    private readonly List<(string Name, byte[] Data)> _entries = [];

    /// <summary>A 32-digit id from a short name, the same every time.</summary>
    public static string Id(string name) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..32];

    private static string Fields(IEnumerable<(string Key, string Value)> fields, int type) =>
        string.Join('\n', fields.Select(f => $"{f.Key}: {f.Value}").Append($"type_: {type}"));

    private static IEnumerable<(string, string)> Extra(Dictionary<string, string>? extra) =>
        extra?.Select(pair => (pair.Key, pair.Value)) ?? [];

    public JexBuilder Folder(string name, string? parent = null, string? title = null, Dictionary<string, string>? extra = null)
    {
        IEnumerable<(string, string)> fields =
        [
            ("id", Id(name)),
            ("parent_id", parent is null ? "" : Id(parent)),
            .. Extra(extra),
        ];
        return Add(Id(name), $"{title ?? name}\n\n{Fields(fields, 2)}");
    }

    public JexBuilder Note(
        string name,
        string? parent,
        string body = "",
        string? title = null,
        bool html = false,
        bool todo = false,
        string? due = null,
        string? completed = null,
        Dictionary<string, string>? extra = null)
    {
        var fields = new List<(string, string)>
        {
            ("id", Id(name)),
            ("parent_id", parent is null ? "" : Id(parent)),
            ("created_time", "2026-01-02T03:04:05.000Z"),
            ("updated_time", "2026-02-03T04:05:06.000Z"),
            ("user_created_time", "2026-01-02T03:04:05.000Z"),
            ("user_updated_time", "2026-02-03T04:05:06.000Z"),
            ("is_conflict", "0"),
            ("is_todo", todo ? "1" : "0"),
            ("todo_due", due ?? "0"),
            ("todo_completed", completed ?? "0"),
            ("markup_language", html ? "2" : "1"),
            ("encryption_applied", "0"),
            ("deleted_time", "0"),
        };
        fields.AddRange(Extra(extra));
        // A later key replaces an earlier one, so a test can override any of the defaults.
        var distinct = fields.GroupBy(f => f.Item1).Select(g => g.Last());
        return Add(Id(name), $"{title ?? name}\n\n{body}\n\n{Fields(distinct, 1)}");
    }

    public JexBuilder Tag(string name, string? title = null) =>
        Add(Id(name), $"{title ?? name}\n\n{Fields([("id", Id(name))], 5)}");

    public JexBuilder NoteTag(string note, string tag) =>
        Add(Id($"{note}->{tag}"), $"\n\n{Fields([("id", Id($"{note}->{tag}")), ("note_id", Id(note)), ("tag_id", Id(tag))], 6)}");

    /// <summary>A resource: its item (the file name is in the title, as Joplin writes it) and its bytes.</summary>
    public JexBuilder Resource(string name, string fileName, byte[] bytes, string mime = "application/octet-stream")
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        Add(Id(name), $"{fileName}\n\n{Fields([("id", Id(name)), ("mime", mime), ("filename", ""), ("file_extension", extension), ("size", bytes.Length.ToString())], 4)}");
        _entries.Add(($"resources/{Id(name)}.{extension}", bytes));
        return this;
    }

    /// <summary>An item of a kind the import does not use (a revision, a setting).</summary>
    public JexBuilder Other(string name, int type) => Add(Id(name), $"\n\n{Fields([("id", Id(name))], type)}");

    /// <summary>The text of an item exactly as given, for the shapes Joplin never writes.</summary>
    public JexBuilder Raw(string fileName, string text) => Add(fileName, text, isId: false);

    private JexBuilder Add(string id, string text, bool isId = true)
    {
        _entries.Add((isId ? $"{id}.md" : id, Encoding.UTF8.GetBytes(text)));
        return this;
    }

    public JexBuilder Entry(string name, byte[] data)
    {
        _entries.Add((name, data));
        return this;
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, data) in _entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
            }
        }

        return stream.ToArray();
    }

    public string ToFile(string directory)
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.jex");
        File.WriteAllBytes(path, ToBytes());
        return path;
    }
}
