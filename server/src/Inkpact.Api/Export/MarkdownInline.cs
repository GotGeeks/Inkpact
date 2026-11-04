using System.Text;
using System.Text.RegularExpressions;

namespace Inkpact.Api.Export;

/// <summary>Where the paths of a page's pictures and files, and of other pages it links to, come from. Null: not in the export.</summary>
/// <param name="PathOfAttachment">The address of an attachment, as it is to be written in the link (already percent-encoded where it needs to be).</param>
/// <param name="PathOfPage">The address of another page (its address in the app, <c>/p/&lt;id&gt;</c>, is replaced by it).</param>
public sealed record ExportLinks(Func<Guid, string?> PathOfAttachment, Func<Guid, string?> PathOfPage)
{
    /// <summary>Paths that are not in the export: a picture or file stays as <c>attachment:&lt;id&gt;</c>, a page link as it is.</summary>
    public static ExportLinks None { get; } = new(_ => null, _ => null);
}

/// <summary>Where a piece of text is: what has no line breaks, or the one line of a table cell.</summary>
internal enum InlineContext
{
    Paragraph,
    Heading,
    Cell,
}

/// <summary>
/// The inline content of a block as Markdown: text with its marks, links, hard breaks, files. Text is escaped only where
/// Markdown would read it as markup. Marks are opened and closed in a fixed order, with white space kept outside them (a
/// marker next to white space is no marker), and a mark that Markdown cannot write there (next to a letter after punctuation,
/// or right after another emphasis) is written as HTML, which the import reads as well.
/// </summary>
internal static partial class MarkdownInline
{
    /// <summary>
    /// The order marks are opened in, outermost first, whatever order the document has them in, so that equal marks are
    /// written equally (the HTML of a table too). A link is inside emphasis, and code innermost: Markdown reads nothing inside it.
    /// </summary>
    internal static readonly string[] MarkOrder = ["underline", "bold", "italic", "strike", "link", "code"];

    private sealed record MarkKey(string Type, string? Href = null, string? Title = null);

    private sealed record Token(string Text, IReadOnlyList<MarkKey> Marks, bool IsBreak = false, bool IsAtom = false);

    [GeneratedRegex(@"^(\d{1,9})([.)])([ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"^([-+=:#>])", RegexOptions.CultureInvariant)]
    private static partial Regex LineStart();

    [GeneratedRegex(@"&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});", RegexOptions.CultureInvariant)]
    private static partial Regex Entity();

    // Not only after a space: Markdown links an address after an underscore, a star, a bracket too.
    [GeneratedRegex(@"(https?|ftp)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoLinkScheme();

    [GeneratedRegex(@"www\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoLinkWww();

    // The import's autolinks also make a link of these, but not inside a word ("hotel:").
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(mailto|tel):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoLinkMailOrPhone();

    public static string Write(IReadOnlyList<DocNode> nodes, InlineContext context, ExportLinks links)
    {
        var tokens = Merge(Tokens(nodes, links));
        var text = Render(tokens, context);
        return context == InlineContext.Cell ? text.Trim() : EscapeLineStarts(text.Trim(' ', '\t'));
    }

    /// <summary>Text that is a line of Markdown by itself (a heading's, an alt text): escaped, with no line breaks.</summary>
    public static string Plain(string text) => Escape(text.Replace('\n', ' ').Replace('\r', ' '));

    /// <summary>The characters of <paramref name="text"/> that Markdown would read as markup, with a backslash before them.</summary>
    public static string Escape(string text)
    {
        var result = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var previous = i > 0 ? text[i - 1] : '\0';
            switch (c)
            {
                case '\\' or '`' or '*' or '[' or ']' or '~' or '^' or '|' or '$':
                    result.Append('\\').Append(c);
                    break;
                case '_' when !char.IsLetterOrDigit(previous) || !char.IsLetterOrDigit(next):
                    // Between two letters an underscore is no emphasis.
                    result.Append("\\_");
                    break;
                // A tag, a comment, or an address in angle brackets (also an email address, whose local part may start with nearly
                // anything): only a "<" with white space or nothing after it is left as it is.
                case '<' when next != '\0' && !char.IsWhiteSpace(next):
                    result.Append("\\<");
                    break;
                case '&' when Entity().Match(text, i) is { Success: true } entity && entity.Index == i:
                    result.Append("\\&");
                    break;
                case '=' or '+' when next == c || previous == c:
                    result.Append('\\').Append(c);
                    break;
                default:
                    result.Append(c);
                    break;
            }
        }

        var escaped = result.ToString();
        // An address in text is not a link unless it was one: the editor made it one with a mark, which is written as a link.
        escaped = AutoLinkScheme().Replace(escaped, m => m.Value.Replace("://", "\\://", StringComparison.Ordinal));
        escaped = AutoLinkMailOrPhone().Replace(escaped, m => m.Groups[1].Value + "\\:");
        return AutoLinkWww().Replace(escaped, m => m.Value[..3] + "\\.");
    }

    /// <summary>
    /// Markdown reads some lines by how they start: a heading, a quote, a list, a rule, a definition, also after up to three
    /// spaces (and a line after a break keeps its spaces). Text that starts a line so gets a backslash.
    /// </summary>
    private static string EscapeLineStarts(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            var start = line[indent.Length..];
            if (NumberedLine().Match(start) is { Success: true } numbered)
            {
                lines[i] = indent + numbered.Groups[1].Value + "\\" + numbered.Groups[2].Value + start[(numbered.Length - numbered.Groups[3].Length)..];
                continue;
            }

            if (LineStart().IsMatch(start))
            {
                // A list marker or a quote or a heading or a rule or a setext line or a definition: escaped at its first character.
                var first = start[0];
                var rest = start[1..];
                var risky = first is '#' or '>' or ':' || rest.Length == 0 || rest[0] is ' ' or '\t' || rest.All(c => c == first || c is ' ' or '\t');
                if (risky)
                {
                    lines[i] = indent + "\\" + start;
                }
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>The longest run of backticks in <paramref name="text"/>: a fence around it must be longer.</summary>
    internal static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    // ---- tokens --------------------------------------------------------------------------------------------------------

    private static List<Token> Tokens(IReadOnlyList<DocNode> nodes, ExportLinks links)
    {
        var tokens = new List<Token>();
        foreach (var node in nodes)
        {
            switch (node.Type)
            {
                case "text" when !string.IsNullOrEmpty(node.Text):
                    {
                        var marks = MarksOf(node, links);
                        var piece = node.Text.Replace('\r', ' ').Replace('\n', ' ');
                        // White space carries no mark of its own: it is inside the marks that go on past it (see Render).
                        tokens.Add(new Token(piece, string.IsNullOrWhiteSpace(piece) && !marks.Any(m => m.Type == "code") ? [] : marks));
                        break;
                    }
                case "hardBreak":
                    tokens.Add(new Token("", [], IsBreak: true));
                    break;
                case "attachment":
                    if (Guid.TryParse(node.String("attachmentId"), out var fileId))
                    {
                        var name = node.String("fileName") is { Length: > 0 } fileName ? fileName : "file";
                        tokens.Add(new Token($"[{Plain(name)}]({Destination(links.PathOfAttachment(fileId) ?? $"attachment:{fileId:D}")})", [], IsAtom: true));
                    }

                    break;
                case "image":
                    if (Guid.TryParse(node.String("attachmentId"), out var imageId))
                    {
                        tokens.Add(new Token(Image(node, links.PathOfAttachment(imageId) ?? $"attachment:{imageId:D}"), [], IsAtom: true));
                    }

                    break;
            }
        }

        // A break at either end, or with nothing but white space after it, says nothing in Markdown.
        static bool Blank(Token token) =>
            token.IsBreak || (!token.IsAtom && !token.Marks.Any(m => m.Type == "code") && string.IsNullOrWhiteSpace(token.Text));

        while (tokens.Count > 0 && Blank(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        while (tokens.Count > 0 && Blank(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        return tokens;
    }

    internal static string Image(DocNode node, string path) => $"![{Plain(node.String("alt") ?? "")}]({Destination(path)})";

    private static List<MarkKey> MarksOf(DocNode node, ExportLinks links)
    {
        var marks = new List<MarkKey>();
        foreach (var mark in node.Marks.OrderBy(m => Array.IndexOf(MarkOrder, m.Type)))
        {
            if (Array.IndexOf(MarkOrder, mark.Type) < 0)
            {
                continue;
            }

            marks.Add(mark.Type == "link"
                ? new MarkKey("link", PageOrOwn(mark.Text("href") ?? "", links), mark.Text("title"))
                : new MarkKey(mark.Type));
        }

        // A link with no address says nothing.
        return [.. marks.Where(m => m.Type != "link" || !string.IsNullOrEmpty(m.Href))];
    }

    /// <summary>Text next to text with the same marks is one piece, so that a mark is opened once.</summary>
    private static List<Token> Merge(List<Token> tokens)
    {
        var merged = new List<Token>();
        foreach (var token in tokens)
        {
            if (merged.Count > 0 && !token.IsBreak && !token.IsAtom && !merged[^1].IsBreak && !merged[^1].IsAtom && merged[^1].Marks.SequenceEqual(token.Marks))
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + token.Text };
            }
            else
            {
                merged.Add(token);
            }
        }

        return merged;
    }

    // ---- writing -------------------------------------------------------------------------------------------------------

    private sealed record Open(MarkKey Key, string Close);

    private static string Render(List<Token> tokens, InlineContext context)
    {
        var html = HtmlRuns(tokens);
        var output = new StringBuilder();
        var open = new List<Open>();
        var pending = new StringBuilder();
        // Where the output ended right after a mark was closed, to see when another is opened right against it.
        var closedAt = -1;

        void CloseDownTo(int count)
        {
            while (open.Count > count)
            {
                output.Append(open[^1].Close);
                open.RemoveAt(open.Count - 1);
                closedAt = output.Length;
            }
        }

        // A bang right before a link (a file's too) would make it a picture.
        void NoBangBefore(string link)
        {
            if (link.StartsWith('[') && output.Length > 0 && output[^1] == '!')
            {
                output.Insert(output.Length - 1, '\\');
            }
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsBreak)
            {
                CloseDownTo(0);
                output.Append(pending);
                pending.Clear();
                output.Append(context switch { InlineContext.Cell => "<br>", InlineContext.Heading => " ", _ => "\\\n" });
                continue;
            }

            if (token.IsAtom)
            {
                CloseDownTo(0);
                output.Append(pending);
                pending.Clear();
                NoBangBefore(token.Text);
                output.Append(token.Text);
                continue;
            }

            var isCode = token.Marks.Any(m => m.Type == "code");
            var (lead, core, trail) = isCode ? ("", token.Text, "") : SplitWhiteSpace(token.Text);
            if (core.Length == 0)
            {
                // Nothing but white space: it carries no mark of its own. It waits, and goes inside the marks that go on past it and
                // outside those that end.
                pending.Append(token.Text);
                // A code span ends before white space that is not its own: its fence was made for the text it was opened for.
                if (open.Count > 0 && open[^1].Key.Type == "code")
                {
                    CloseDownTo(open.Count - 1);
                }

                continue;
            }

            var target = token.Marks;
            var common = 0;
            while (common < open.Count && common < target.Count && open[common].Key == target[common])
            {
                common++;
            }

            // The white space between two runs is outside the marks on both sides: closed first, then the space, then opened.
            CloseDownTo(common);
            output.Append(pending).Append(lead);
            pending.Clear();
            for (var m = common; m < target.Count; m++)
            {
                var (opener, closer) = Markers(target[m], core, output.Length > 0 && output.Length == closedAt ? output[^1] : (char?)null, output.Length > 0 ? output[^1] : (char?)null, html.Contains((i, m)));
                NoBangBefore(opener);
                output.Append(opener);
                open.Add(new Open(target[m], closer));
            }

            // Code as HTML (see Markers) is read as Markdown inside the tag, so its text is escaped like any.
            output.Append(isCode && !(open.Count > 0 && open[^1].Close == "</code>") ? core : Escape(core));
            pending.Append(trail);
        }

        CloseDownTo(0);
        output.Append(pending);
        return output.ToString();
    }

    private static (string Lead, string Core, string Trail) SplitWhiteSpace(string text)
    {
        var start = 0;
        while (start < text.Length && IsSpace(text[start]))
        {
            start++;
        }

        var end = text.Length;
        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return (text[..start], text[start..end], text[end..]);
    }

    private static bool IsSpace(char c) => char.IsWhiteSpace(c);

    /// <summary>
    /// The marks of spans that Markdown cannot write where they are, as (token, position in its marks). A span is what Render
    /// opens and closes: it ends when a mark outside it ends, and begins again after, so it is worked out the way Render does.
    /// </summary>
    private static HashSet<(int, int)> HtmlRuns(List<Token> tokens)
    {
        var html = new HashSet<(int, int)>();
        var open = new List<(MarkKey Key, int Token, int Position)>();
        var last = -1;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.IsBreak || token.IsAtom)
            {
                CloseAll();
                last = i;
                continue;
            }

            if (token.Marks.Count == 0 && string.IsNullOrWhiteSpace(token.Text))
            {
                continue;
            }

            var common = 0;
            while (common < open.Count && common < token.Marks.Count && open[common].Key == token.Marks[common])
            {
                common++;
            }

            CloseDownTo(common);
            for (var m = common; m < token.Marks.Count; m++)
            {
                open.Add((token.Marks[m], i, m));
            }

            last = i;
        }

        CloseAll();
        return html;

        void CloseAll() => CloseDownTo(0);

        // The span being closed is the top of the stack; what is above it, inside, closes right before it.
        void CloseDownTo(int count)
        {
            while (open.Count > count)
            {
                var (key, start, position) = open[^1];
                open.RemoveAt(open.Count - 1);
                if (key.Type is "bold" or "italic" or "strike")
                {
                    // Marks inside this one that end together with it put their own marker against it.
                    var innerAtEnd = tokens[last].Marks.Count > position + 1 && !tokens[last].IsAtom;
                    if (Unsafe(tokens, start, last, tokens[start].Marks.Count > position + 1, innerAtEnd))
                    {
                        html.Add((start, position));
                    }
                }
            }
        }
    }

    /// <summary>
    /// A marker next to a letter, when the text inside it starts or ends with punctuation (or another marker), is not one
    /// Markdown opens or closes: <c>word**(s)**</c>. Whether it is next to a letter is looked at in the text on either side.
    /// </summary>
    private static bool Unsafe(List<Token> tokens, int start, int end, bool innerAtStart, bool innerAtEnd)
    {
        var first = SplitWhiteSpace(tokens[start].Text);
        var last = SplitWhiteSpace(tokens[end].Text);
        if (first.Core.Length == 0 || last.Core.Length == 0)
        {
            return false;
        }

        var before = first.Lead.Length > 0 || start == 0 || tokens[start - 1].IsBreak || tokens[start - 1].IsAtom ? ' ' : tokens[start - 1].Text[^1];
        var after = last.Trail.Length > 0 || end + 1 >= tokens.Count || tokens[end + 1].IsBreak || tokens[end + 1].IsAtom ? ' ' : tokens[end + 1].Text[0];
        var firstIsPunctuation = innerAtStart || !char.IsLetterOrDigit(first.Core[0]);
        var lastIsPunctuation = innerAtEnd || !char.IsLetterOrDigit(last.Core[^1]);
        return (firstIsPunctuation && char.IsLetterOrDigit(before)) || (lastIsPunctuation && char.IsLetterOrDigit(after));
    }

    private static (string Open, string Close) Markers(MarkKey key, string core, char? justClosed, char? last, bool html)
    {
        switch (key.Type)
        {
            case "underline":
                return ("<u>", "</u>");
            case "code":
                {
                    // A backtick right before would make this fence longer than it is: a code span of its own is written as HTML.
                    if (last == '`')
                    {
                        return ("<code>", "</code>");
                    }

                    var fence = new string('`', LongestBacktickRun(core) + 1);
                    var pad = core.StartsWith('`') || core.EndsWith('`') || (core.StartsWith(' ') && core.EndsWith(' ') && core.Trim().Length > 0) ? " " : "";
                    return (fence + pad, pad + fence);
                }
            case "link":
                return ("[", $"]({Destination(key.Href ?? "")}{(key.Title is { Length: > 0 } title ? $" \"{title.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)}\"" : "")})");
        }

        var (markdown, tag) = key.Type switch
        {
            "bold" => ("**", "strong"),
            "italic" => ("*", "em"),
            _ => ("~~", "del"),
        };
        // Right after the same character, another run would run into this one: `**a***b*`.
        var touching = justClosed == markdown[0];
        return html || touching ? ($"<{tag}>", $"</{tag}>") : (markdown, markdown);
    }

    /// <summary>The path of another page of the export for <c>/p/&lt;id&gt;</c>, otherwise the address as it is.</summary>
    internal static string PageOrOwn(string href, ExportLinks links) =>
        ExportPageLinks.PageIdOf(href) is { } id && links.PathOfPage(id) is { } path ? path : href;

    /// <summary>
    /// A link's address as Markdown writes it: as it is, or in angle brackets when it has white space or brackets. A "|" has
    /// a backslash, which reads the same, so that a table cell does not end at it.
    /// </summary>
    internal static string Destination(string address)
    {
        var piped = address.Replace("|", "\\|", StringComparison.Ordinal);
        return address.Length == 0 || address.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '<' or '>')
            ? "<" + piped.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal) + ">"
            : piped;
    }
}
