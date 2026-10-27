using System.Globalization;
using System.Text.RegularExpressions;

namespace Inkpact.Api.Import;

/// <summary>The kinds of item in a Joplin export that the import uses (Joplin's <c>type_</c>).</summary>
internal static class JexTypes
{
    public const int Note = 1;
    public const int Folder = 2;
    public const int Resource = 4;
    public const int Tag = 5;
    public const int NoteTag = 6;
}

/// <summary>
/// One item of a Joplin export as its file reads: a title, for a note a body, and <c>key: value</c> lines. A body can hold
/// anything, including lines that look like those, so the lines are read from the end of the file.
/// </summary>
internal sealed partial class JexItem(string title, string body, IReadOnlyDictionary<string, string> fields)
{
    /// <summary>The most property lines an item may have; a file with more is not a Joplin item.</summary>
    private const int MaxFields = 500;

    public string Title { get; } = title;

    /// <summary>The note's Markdown or HTML. Empty for the other kinds of item.</summary>
    public string Body { get; } = body;

    public IReadOnlyDictionary<string, string> Fields { get; } = fields;

    public int Type => int.TryParse(Get("type_"), NumberStyles.None, CultureInfo.InvariantCulture, out var type) ? type : 0;

    public string Id => Get("id");

    public string Get(string name) => Fields.TryGetValue(name, out var value) ? value : "";

    /// <summary>A flag as Joplin writes it: 1 is yes, anything else no.</summary>
    public bool Flag(string name) => Get(name) == "1";

    /// <summary>
    /// A time as Joplin writes it: milliseconds since 1970 (a due date), or ISO text (a creation time). Zero, nothing and
    /// anything that is not a time are no time.
    /// </summary>
    public DateTimeOffset? Time(string name) => ParseTime(Get(name));

    public static DateTimeOffset? ParseTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds))
        {
            // The last millisecond DateTimeOffset has is the end of year 9999.
            return milliseconds is > 0 and <= 253_402_300_799_999 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            && time.Year >= 1970
            ? time
            : null;
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsId(string value) => IdPattern().IsMatch(value);

    /// <summary>The item the text of a file holds, or null when it is not one: no property lines at the end, or too many.</summary>
    public static JexItem? Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var end = text.Length;
        while (end > 0 && text[end - 1] is '\n' or '\r' or ' ')
        {
            end--;
        }

        // The property lines, from the last one up to the first blank line.
        var blankLineStart = -1;
        var position = end;
        while (true)
        {
            var lineStart = position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
            var line = text.AsSpan(lineStart, position - lineStart).Trim();
            if (line.IsEmpty)
            {
                blankLineStart = lineStart;
                break;
            }

            var colon = line.IndexOf(':');
            if (colon < 0 || fields.Count >= MaxFields)
            {
                return null;
            }

            // Where a key is there twice, the one higher up wins, as Joplin reads it.
            fields[line[..colon].Trim().ToString()] = line[(colon + 1)..].Trim().ToString();

            if (lineStart == 0)
            {
                break;
            }

            position = lineStart - 1;
        }

        if (fields.Count == 0)
        {
            return null;
        }

        // What is above the blank line: the title, a blank line, and the body.
        var title = "";
        var body = "";
        if (blankLineStart > 0)
        {
            var head = text.AsSpan(0, Math.Max(0, blankLineStart - 1));
            var firstBreak = head.IndexOf('\n');
            title = (firstBreak < 0 ? head : head[..firstBreak]).TrimEnd('\r').ToString();
            if (firstBreak >= 0)
            {
                var rest = head[(firstBreak + 1)..];
                var secondBreak = rest.IndexOf('\n');
                body = secondBreak < 0 ? "" : rest[(secondBreak + 1)..].ToString();
            }
        }

        return new JexItem(title, body, fields);
    }
}
