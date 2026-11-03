using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inkpact.Api.Import;

/// <summary>What happened to an item that is in the report. The client translates the statuses and the codes of the reasons.</summary>
public static class ReportStatus
{
    /// <summary>Imported, but not as it was (see the reasons).</summary>
    public const string Imperfect = "imperfect";

    /// <summary>An earlier import's page was brought up to date with the note, and there is something to say about it (see the reasons).</summary>
    public const string Updated = "updated";

    /// <summary>
    /// The note changed in Joplin and here: the page here is as it was, and Joplin's version is a page beside it
    /// (<c>PageId</c>; the original is <c>OriginalPageId</c>).
    /// </summary>
    public const string ConflictCopy = "conflict_copy";

    /// <summary>Imported as a copy of a conflict that Joplin had made.</summary>
    public const string Conflict = "conflict";

    /// <summary>Not imported, on purpose (see the reason).</summary>
    public const string Skipped = "skipped";

    /// <summary>Not imported, because something went wrong.</summary>
    public const string Failed = "failed";
}

/// <summary>One line of the report: an item, what became of it, and why.</summary>
public sealed record ReportEntry(
    string Kind,
    string ExternalId,
    string Title,
    string Status,
    IReadOnlyList<ConversionNote> Reasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PageId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OriginalPageId = null);

/// <summary>What the import made, per kind. <c>AlreadyImported</c> counts items an earlier import made, which this one left alone.</summary>
public sealed class ImportCounts
{
    public int Notebooks { get; set; }

    public int Notes { get; set; }

    public int Todos { get; set; }

    public int Tags { get; set; }

    public int Images { get; set; }

    public int Files { get; set; }

    public int AlreadyImported { get; set; }

    /// <summary>Pages of an earlier import whose content was taken from a note that changed only in Joplin.</summary>
    public int Updated { get; set; }

    /// <summary>Pages of an earlier import that nobody changed here, converted again because the converter is newer.</summary>
    public int Reconverted { get; set; }

    /// <summary>Pages that changed only here, and were left as they were.</summary>
    public int KeptInInkpact { get; set; }

    /// <summary>Notes that changed in Joplin and here: Joplin's version became a page beside the page here.</summary>
    public int Conflicts { get; set; }

    /// <summary>Items an earlier import made that are in the trash here now; left there.</summary>
    public int AlreadyInTrash { get; set; }

    public int Trashed { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    /// <summary>Lines left out of the report because it is full.</summary>
    public int Unlisted { get; set; }
}

/// <summary>Writes and reads the two JSON columns of an import.</summary>
public static class ImportJson
{
    /// <summary>The most lines the report keeps; the rest are in <see cref="ImportCounts.Unlisted"/>.</summary>
    public const int MaxReportEntries = 5_000;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) where T : new() => JsonSerializer.Deserialize<T>(json, Options) ?? new T();
}
