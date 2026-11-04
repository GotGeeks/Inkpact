using System.Globalization;
using System.Text;

namespace Inkpact.Api.Export;

/// <summary>
/// The YAML at the top of each exported page: what is on the page row and not in the document (title, id, dates, tags, to-do
/// fields). Text is a YAML double-quoted string: only the backslash, the quote and characters that are not printable (control
/// characters, line and paragraph separators) are escaped, so no title can break out of its line, and every other character,
/// emoji included, is written as it is and reads as itself. The title is not repeated as a heading: it is not one in Inkpact either.
/// </summary>
internal static class ExportFrontMatter
{
    public static string Of(ExportPage page)
    {
        var lines = new List<string>
        {
            "---",
            $"title: {Quote(page.Title)}",
            $"id: {Quote(page.Id.ToString("D"))}",
            $"created: {Date(page.CreatedAt)}",
            $"updated: {Date(page.UpdatedAt)}",
        };
        if (page.Tags.Count > 0)
        {
            lines.Add($"tags: [{string.Join(", ", page.Tags.Select(Quote))}]");
        }

        if (page.IsTodo)
        {
            lines.Add("todo: true");
            if (page.CompletedAt is { } completed)
            {
                lines.Add($"completed: {Date(completed)}");
            }

            if (page.DueAt is { } due)
            {
                lines.Add($"due: {Date(due)}");
            }
        }

        lines.Add("---");
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>A YAML double-quoted string. Half of a character outside the basic plane (no valid text has one) becomes U+FFFD.</summary>
    internal static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                quoted.Append(c).Append(value[++i]);
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                case '\n':
                    quoted.Append("\\n");
                    break;
                case '\t':
                    quoted.Append("\\t");
                    break;
                case '\r':
                    quoted.Append("\\r");
                    break;
                case var _ when char.IsSurrogate(c):
                    quoted.Append((char)0xFFFD);
                    break;
                // Not printable in YAML: control characters (C0, DEL, C1, of which U+0085 is a line break) and U+2028/U+2029.
                case var _ when char.IsControl(c) || c is (char)0x2028 or (char)0x2029 or (char)0xFEFF:
                    quoted.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    private static string Date(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
