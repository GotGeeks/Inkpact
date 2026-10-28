using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inkpact.Api.Import;

/// <summary>
/// What imported content may contain. This is the server's side of the check on pasted and imported HTML; the client's
/// is <c>web/src/editor/sanitize.ts</c>. Both take their tags from <c>web/src/editor/allowList.json</c>, which is built
/// into this assembly. Nothing outside the lists reaches the document: the converters only ever build nodes of the
/// editor's schema, so the tags say what the converters understand, and the rest keeps its text. No attribute of the
/// HTML is copied into the document either: the converters read a few (an address, a span, a checkbox, a style) to
/// decide what to build, so the list's attributes, which say what a paste keeps, have no use here.
/// </summary>
internal static partial class ContentRules
{
    /// <summary>The tags the HTML converter understands: <c>tags</c> in <c>allowList.json</c>.</summary>
    public static readonly IReadOnlySet<string> AllowedTags = ReadAllowedTags();

    private static HashSet<string> ReadAllowedTags()
    {
        using var stream = typeof(ContentRules).Assembly.GetManifestResourceStream("Inkpact.Api.Import.allowList.json")
            ?? throw new InvalidOperationException("allowList.json is not built into the assembly");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Elements that only group or lay out: their content stays, and nobody needs to hear about them.</summary>
    public static readonly IReadOnlySet<string> LayoutElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "html", "body", "section", "article", "header", "footer", "main", "nav", "aside", "figure", "figcaption", "font",
        "center", "form", "fieldset", "legend", "caption", "tfoot", "colgroup", "col", "address", "hgroup", "wbr", "bdi",
        "bdo", "small", "big", "dir", "menu", "thead", "tbody", "time",
    };

    /// <summary>A custom element (<c>twk-icon</c>) is a container the page invented: its content stays, and it is not worth a line in the report.</summary>
    public static bool IsLayoutElement(string tag) => LayoutElements.Contains(tag) || tag.Contains('-');

    /// <summary>Elements that go with everything inside them: they hold code, not text.</summary>
    public static readonly IReadOnlySet<string> RemovedElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "noscript", "template", "head",
        "title", "meta", "link", "base", "svg", "math", "canvas", "audio", "video", "source", "track", "map", "area",
        "select", "option", "optgroup", "datalist", "button", "textarea", "dialog", "noframes", "param",
    };

    /// <summary>Removed elements nobody needs to hear about: they hold styling or metadata, never content.</summary>
    public static readonly IReadOnlySet<string> SilentlyRemovedElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "style", "head", "meta", "link", "base", "title", "noscript", "template", "noframes", "param", "source", "track",
    };

    /// <summary>Elements that have no content and no closing tag.</summary>
    public static readonly IReadOnlySet<string> VoidElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>How deep markup may nest before it is not parsed at all.</summary>
    public const int MaxMarkupDepth = 3000;

    /// <summary>Elements whose content is text to the parser, not markup (outside SVG, MathML and a select).</summary>
    private static readonly HashSet<string> RawTextElements = new(StringComparer.Ordinal)
    {
        "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes",
    };

    /// <summary>Marks a part of a table that the parser adds where the markup leaves it out.</summary>
    private const char Implied = '+';

    /// <summary>
    /// A cheap upper estimate of how deep the tags of <paramref name="html"/> nest: one pass, no parsing, and never lower
    /// than what the parser will build, since the parse's time grows with the square of the depth. Every start tag opens
    /// an element except a void one (a browser ignores the slash in <c>&lt;div/&gt;</c>), and so do the parts of a table
    /// the parser adds (<c>&lt;table&gt;&lt;td&gt;</c> is a cell in a row in a body). A closing tag only closes the
    /// innermost element, and only when it names it. A browser does more: it closes what a closing tag names further out,
    /// and a <c>p</c> before the next block; but the parser also ignores closing tags (a stray <c>&lt;/i&gt;</c>, a
    /// <c>&lt;/span&gt;</c> outside the <c>div</c> it is in) and opens formatting again that something else closed. To
    /// follow all of that is to parse; what stays open here only makes the estimate higher.
    /// </summary>
    public static int MarkupDepth(string html)
    {
        var open = new List<string>();
        // How many elements are open whose content the parser reads by rules of its own: there, a void element's name (a
        // source in SVG) is an element like any other, and a script is markup.
        var otherRules = 0;
        var deepest = 0;
        for (var at = html.IndexOf('<'); at >= 0 && at + 1 < html.Length; at = html.IndexOf('<', at + 1))
        {
            var closing = html[at + 1] == '/';
            var start = at + (closing ? 2 : 1);
            if (start >= html.Length || !char.IsAsciiLetter(html[start]))
            {
                continue;
            }

            // The tag's name as the parser reads it: up to white space, a slash or the end of the tag; ASCII letters lowered.
            var end = start;
            while (end < html.Length && html[end] is not (' ' or '\t' or '\n' or '\r' or '\f' or '/' or '>'))
            {
                end++;
            }

            var name = string.Create(end - start, (html, start), static (name, source) =>
            {
                for (var i = 0; i < name.Length; i++)
                {
                    var c = source.html[source.start + i];
                    name[i] = char.IsAsciiLetterUpper(c) ? (char)(c | 0x20) : c;
                }
            });

            // Inside a note's body, these never open or close anything.
            if (name is "html" or "body" or "head")
            {
                continue;
            }

            if (closing)
            {
                // The parts of a table the parser added close with the part the markup closes.
                var innermost = open.FindLastIndex(element => element[0] != Implied);
                if (innermost >= 0 && open[innermost] == name)
                {
                    open.RemoveRange(innermost, open.Count - innermost);
                    otherRules -= HasOtherRules(name) ? 1 : 0;
                }
                else if (otherRules > 0)
                {
                    // AngleSharp opens an element for some closing tags here (</br> in MathML), so one that closes
                    // nothing counts as opening one.
                    open.Add(name);
                    deepest = Math.Max(deepest, open.Count);
                }

                continue;
            }

            var top = open.Count > 0 ? open[^1] : null;
            open.AddRange(name switch
            {
                "tr" when top is not ("tbody" or "thead" or "tfoot" or "+tbody") => ["+tbody"],
                "td" or "th" when top is "tbody" or "thead" or "tfoot" or "+tbody" => ["+tr"],
                "td" or "th" when top is not ("tr" or "+tr" or "td" or "th") => ["+tbody", "+tr"],
                "col" when top is not "colgroup" => ["+colgroup"],
                _ => [],
            });

            if (otherRules > 0 || !VoidElements.Contains(name))
            {
                open.Add(name);
                otherRules += HasOtherRules(name) ? 1 : 0;
            }

            deepest = Math.Max(deepest, open.Count);

            // A script's content is text, whatever is in it.
            if (otherRules == 0 && name == "plaintext")
            {
                break;
            }

            if (otherRules == 0 && RawTextElements.Contains(name))
            {
                var close = html.IndexOf("</" + name, end, StringComparison.OrdinalIgnoreCase);
                if (close < 0)
                {
                    break;
                }

                at = close - 1;
            }
        }

        return deepest;
    }

    private static bool HasOtherRules(string name) => name is "svg" or "math" or "select";

    /// <summary>
    /// The text of markup without parsing it: tags and comments gone, scripts and styles with their content, entities
    /// decoded. One pass: whatever is not closed (a comment, a script) runs to the end, as in a browser.
    /// </summary>
    public static string TextOfMarkup(string html)
    {
        var text = new StringBuilder(html.Length);
        var at = 0;
        while (at < html.Length)
        {
            var open = html.IndexOf('<', at);
            if (open < 0 || open + 1 >= html.Length)
            {
                text.Append(html, at, html.Length - at);
                break;
            }

            text.Append(html, at, open - at);
            var next = html[open + 1];
            int resume;
            if (string.CompareOrdinal(html, open, "<!--", 0, 4) == 0)
            {
                resume = After(html, "-->", open + 4);
            }
            else if (RawTextElement(html, open) is { } element)
            {
                resume = After(html, "</" + element, open + 1 + element.Length);
                resume = resume < html.Length ? After(html, ">", resume) : resume;
            }
            else if (char.IsAsciiLetter(next) || next is '/' or '!' or '?')
            {
                resume = After(html, ">", open + 1);
            }
            else
            {
                // A < that starts no tag ("a < b") is text.
                text.Append('<');
                at = open + 1;
                continue;
            }

            text.Append(' ');
            at = resume;
        }

        return System.Net.WebUtility.HtmlDecode(text.ToString());
    }

    /// <summary>Where the text after the next <paramref name="end"/> from <paramref name="from"/> starts; the end of the text when there is none.</summary>
    private static int After(string html, string end, int from)
    {
        var found = html.IndexOf(end, from, StringComparison.OrdinalIgnoreCase);
        return found < 0 ? html.Length : found + end.Length;
    }

    /// <summary>The name of the element at <paramref name="at"/> when its content is code rather than markup (a script, a style).</summary>
    private static string? RawTextElement(string html, int at)
    {
        foreach (var name in (string[])["script", "style"])
        {
            var end = at + 1 + name.Length;
            if (end <= html.Length
                && string.Compare(html, at + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (end == html.Length || html[end] is ' ' or '\t' or '\n' or '\r' or '\f' or '/' or '>'))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The Joplin resource id inside <c>:/id</c>, with an optional <c>#fragment</c>.</summary>
    [GeneratedRegex(@"^:/(?<id>[0-9a-f]{32})(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JoplinItemAddress();

    public static string? JoplinItemId(string? address)
    {
        var match = address is null ? Match.Empty : JoplinItemAddress().Match(address.Trim());
        return match.Success ? match.Groups["id"].Value : null;
    }

    public const int MaxHrefLength = 8192;
    public const int MaxTitleLength = 1000;
    public const int MaxAltLength = 1000;
    public const int MaxLanguageLength = 40;
    public const int MaxCellSpan = 100;

    /// <summary>Cells a table may have once every row is as wide as the table.</summary>
    public const int MaxTableCells = 100_000;

    /// <summary>
    /// How deep blocks may nest (quotes, lists, tables) before the inner ones are flattened. The sidecar stops at 64
    /// levels of elements; a list is two of those for every level it nests.
    /// </summary>
    public const int MaxBlockNesting = 15;

    /// <summary>How deep the HTML tree is followed. Deeper than any page written by a person; stops a hostile one from using up the stack.</summary>
    public const int MaxTreeDepth = 200;

    /// <summary>Leading and trailing C0 control characters and spaces are not part of an address.</summary>
    private static readonly char[] ControlsAndSpace = [.. Enumerable.Range(0, 0x21).Select(i => (char)i)];

    /// <summary>
    /// The address of a link, if it may be one: absolute, and <c>http</c>, <c>https</c> or <c>mailto</c> (the rule of
    /// <c>links.ts</c>). Otherwise null, and <paramref name="scheme"/> says what it was, for the report: its scheme, or
    /// <c>relative</c>. Browsers ignore tabs and line breaks inside an address, so <c>java\tscript:</c> is
    /// <c>javascript:</c>; this reads it the same way.
    /// </summary>
    public static string? AllowedLink(string? href, out string scheme)
    {
        scheme = "relative";
        if (href is null)
        {
            return null;
        }

        var builder = new StringBuilder(href.Length);
        foreach (var c in href)
        {
            if (c is not ('\t' or '\n' or '\r'))
            {
                builder.Append(c);
            }
        }

        var address = builder.ToString().Trim(ControlsAndSpace);
        var colon = address.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(address[0]) || !address[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            return null;
        }

        scheme = address[..colon].ToLowerInvariant();
        if (address.Length > MaxHrefLength || scheme is not ("http" or "https" or "mailto"))
        {
            return null;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (scheme != "mailto" && string.IsNullOrEmpty(uri.Host)))
        {
            return null;
        }

        return address;
    }

    /// <summary>Text the document can hold: no half surrogate pairs, no control characters other than the tab and the line break.</summary>
    public static string CleanText(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c) || c is '￾' or '￿')
            {
                builder.Append('�');
            }
            else if (c == '\r')
            {
                // A line break is one \n, whichever system wrote the note.
                if (i + 1 >= text.Length || text[i + 1] != '\n')
                {
                    builder.Append('\n');
                }
            }
            else if (c is '\n' or '\t' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>At most <paramref name="max"/> characters, without cutting a surrogate pair in half.</summary>
    public static string Limit(string text, int max, ConversionNotes notes, string what)
    {
        if (text.Length <= max)
        {
            return text;
        }

        notes.Add(NoteCodes.Truncated, what);
        return Cut(text, max);
    }

    /// <summary>At most <paramref name="max"/> characters, without cutting a surrogate pair in half: half a pair is not text a database takes.</summary>
    public static string Cut(string text, int max) =>
        text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];

    /// <summary>A code block's language as the editor stores it: a short word, or none.</summary>
    public static string? Language(string? info)
    {
        var word = info?.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (word is null)
        {
            return null;
        }

        word = word.TrimStart('{', '.').TrimEnd('}');
        return word.Length is > 0 and <= MaxLanguageLength && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '#' or '-' or '_' or '.')
            ? word
            : null;
    }
}
