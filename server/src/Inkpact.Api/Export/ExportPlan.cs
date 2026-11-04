using System.Text;

namespace Inkpact.Api.Export;

/// <summary>A page of the export, with what its front matter says. Tags are their names, sorted.</summary>
internal sealed record ExportPage(
    Guid Id,
    Guid? ParentId,
    string Title,
    string SortKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsTodo,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? DueAt,
    IReadOnlyList<string> Tags);

/// <summary>A row of the attachments table of the workspace: a file a page may use, stored once per content under its hash.</summary>
internal sealed record ExportFile(Guid Id, string FileName, string Sha256, long Size, DateTimeOffset CreatedAt);

/// <summary>A page and where its file is in the zip (<c>Notes/Ideas.md</c>, with <c>/</c> between the folders).</summary>
internal sealed record PlannedPage(ExportPage Page, string Path);

/// <summary>A stored file to be copied into the zip, and where it goes (<c>_attachments/manual.pdf</c>).</summary>
internal sealed record PlannedFile(ExportFile File, string Path);

/// <summary>
/// Where everything goes in the zip: the page tree as folders (a page is <c>Title.md</c> and the pages under it are in the folder
/// <c>Title/</c> beside it), and the attachments in <c>_attachments/</c>, one copy per content. The same tree always gives the
/// same plan. The paths of pages are known up front; those of files are given the first time a page uses one, in tree order.
/// </summary>
internal sealed class ExportPlan
{
    public const string AttachmentsFolder = "_attachments";

    public const string ReadmeName = "README.md";

    private readonly Dictionary<Guid, PlannedPage> _pages;
    private readonly Dictionary<Guid, ExportFile> _fileRows;
    private readonly Dictionary<string, PlannedFile> _filesByHash = new(StringComparer.Ordinal);
    private readonly ExportNames.Folder _fileNames = new();
    private readonly List<PlannedFile> _newFiles = [];

    private ExportPlan(List<PlannedPage> pages, IEnumerable<ExportFile> files)
    {
        Pages = pages;
        _pages = pages.ToDictionary(p => p.Page.Id);
        _fileRows = files.ToDictionary(f => f.Id);
    }

    /// <summary>The pages in tree order: a parent before its children, siblings by key.</summary>
    public IReadOnlyList<PlannedPage> Pages { get; }

    /// <summary>
    /// Plans <paramref name="pages"/> (any order; the ones whose parent is not among them are put at the top, as the tree does not
    /// have them) and the attachments of the workspace they may use.
    /// </summary>
    public static ExportPlan Build(IReadOnlyList<ExportPage> pages, IEnumerable<ExportFile> files)
    {
        var root = new ExportNames.Folder();
        root.Reserve(ReadmeName);
        root.Reserve(AttachmentsFolder);
        // The folder of the pages below a page, made when the first of them is placed. Parents come first in tree order.
        var folders = new Dictionary<Guid, (string Directory, ExportNames.Folder Names)>();
        var planned = new List<PlannedPage>(pages.Count);
        foreach (var page in Inkpact.Api.Pages.TreeOrder.Of(pages, p => p.Id, p => p.ParentId, p => p.SortKey))
        {
            var (directory, names) = page.ParentId is { } parent && folders.TryGetValue(parent, out var folder) ? folder : ("", root);
            var stem = names.TakeStem(page.Title);
            planned.Add(new PlannedPage(page, directory + stem + ".md"));
            folders[page.Id] = (directory + stem + "/", new ExportNames.Folder());
        }

        return new ExportPlan(planned, files);
    }

    /// <summary>
    /// The path in the zip of the file with this attachment id, given a name the first time one with its content is asked for;
    /// null when the id is no attachment of the workspace (it is not exported: it may belong to another workspace).
    /// </summary>
    public string? UseFile(Guid attachmentId)
    {
        if (!_fileRows.TryGetValue(attachmentId, out var file))
        {
            return null;
        }

        if (!_filesByHash.TryGetValue(file.Sha256, out var planned))
        {
            planned = new PlannedFile(file, AttachmentsFolder + "/" + _fileNames.TakeFile(file.FileName));
            _filesByHash[file.Sha256] = planned;
            _newFiles.Add(planned);
        }

        return planned.Path;
    }

    /// <summary>The files first used since the last call, to be copied into the zip now.</summary>
    public IReadOnlyList<PlannedFile> TakeNewFiles()
    {
        var files = _newFiles.ToArray();
        _newFiles.Clear();
        return files;
    }

    /// <summary>The path of another page as the page at <paramref name="fromPath"/> links to it; null when it is not in the export.</summary>
    public string? PathOfPage(string fromPath, Guid pageId) =>
        _pages.TryGetValue(pageId, out var target) ? Relative(fromPath, target.Path) : null;

    /// <summary>The path of a file as the page at <paramref name="fromPath"/> links to it.</summary>
    public static string PathOfFile(string fromPath, string filePath) => Relative(fromPath, filePath);

    /// <summary>
    /// The way from the folder of <paramref name="fromPath"/> to <paramref name="toPath"/>, with <c>..</c> where it goes up, each
    /// name percent-encoded so that every Markdown reader follows it: only letters, digits and <c>- _ . ~</c> stay as they are.
    /// </summary>
    internal static string Relative(string fromPath, string toPath)
    {
        var from = fromPath.Split('/')[..^1];
        var to = toPath.Split('/');
        var common = 0;
        while (common < from.Length && common < to.Length - 1 && from[common] == to[common])
        {
            common++;
        }

        var parts = Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common).Select(Encode));
        return string.Join('/', parts);
    }

    private static string Encode(string name)
    {
        var builder = new StringBuilder();
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value < 128 ? char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value is '-' or '_' or '.' or '~' : Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString());
            }
            else
            {
                var length = rune.EncodeToUtf8(bytes);
                foreach (var b in bytes[..length])
                {
                    builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        return builder.ToString();
    }
}
