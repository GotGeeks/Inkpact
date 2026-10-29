using System.Text.Json.Serialization;

namespace Inkpact.Api.Import;

/// <summary>
/// What the other app had of an item when it was last imported, field by field (<c>import_items.base</c>): the third value
/// of a three-way comparison, next to the other app's value now and ours. Only the fields of the item's kind are set. A
/// field moves only when its value has been taken over here, so a change that was left alone is still seen next time.
/// </summary>
public sealed record ImportBase
{
    /// <summary>The title as it became here (cleaned, and cut to the length of a title).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>The Joplin id of the notebook it was in; empty at the top level.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; init; }

    /// <summary>SHA-256 of the note's body only, as lower-case hex: what tells a change of the content from the file's bookkeeping.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BodyHash { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsTodo { get; init; }

    /// <summary>The due date, in milliseconds since 1970 as Joplin writes it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TodoDue { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TodoCompleted { get; init; }

    /// <summary>The Joplin ids of the tags on the note, in order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? TagIds { get; init; }

    public string ToJson() => ImportJson.Write(this);

    public static ImportBase? FromJson(string? json) => json is null ? null : ImportJson.Read<ImportBase>(json);
}
