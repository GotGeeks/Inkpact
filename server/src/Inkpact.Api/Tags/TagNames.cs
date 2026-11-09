using Inkpact.Api.Import;

namespace Inkpact.Api.Tags;

/// <summary>
/// The one rule for a tag's name, used by the Joplin import and by the interface alike, so a name typed here finds the tag
/// the import made from the same name and the other way round.
/// </summary>
public static class TagNames
{
    public const int MaxLength = 200;

    /// <summary>The name as it is kept: no control characters, tabs and line breaks as spaces, trimmed. Inner spaces stay as they are.</summary>
    public static string Clean(string? name) =>
        ContentRules.CleanText(name ?? "").Replace('\t', ' ').Replace('\n', ' ').Trim();

    /// <summary>What has to be unique per workspace: the name in lower case.</summary>
    public static string Normalize(string cleanName) => cleanName.ToLowerInvariant();
}
