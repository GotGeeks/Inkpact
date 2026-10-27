using System.Text.Json;
using FsCheck.Xunit;
using Inkpact.Api.Import;

namespace Inkpact.Api.Tests.Import;

public sealed class ContentRulesTests
{
    private static JsonElement AllowList()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "web", "src", "editor", "allowList.json");
            if (File.Exists(candidate))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(candidate));
                return json.RootElement.Clone();
            }
        }

        throw new FileNotFoundException("web/src/editor/allowList.json was not found above the test assembly");
    }

    private static string[] Strings(JsonElement list) => [.. list.EnumerateArray().Select(item => item.GetString()!)];

    [Fact]
    public void The_tags_are_those_the_client_sanitises_with()
    {
        ContentRules.AllowedTags.Order(StringComparer.Ordinal).ShouldBe(Strings(AllowList().GetProperty("tags")).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("HTTP://EXAMPLE.COM/a?b=c#d", "HTTP://EXAMPLE.COM/a?b=c#d")]
    [InlineData("mailto:a@example.com", "mailto:a@example.com")]
    [InlineData("  https://example.com\n", "https://example.com")]
    [InlineData("https://exa\tmple.com", "https://example.com")]
    public void Web_and_mail_addresses_are_links(string href, string expected)
    {
        ContentRules.AllowedLink(href, out _).ShouldBe(expected);
    }

    [Theory]
    [InlineData("javascript:alert(1)", "javascript")]
    [InlineData("JaVaScRiPt:alert(1)", "javascript")]
    [InlineData("  javascript:alert(1)", "javascript")]
    [InlineData("\u0001javascript:alert(1)", "javascript")]
    [InlineData("java\tscript:alert(1)", "javascript")]
    [InlineData("java\nscript:alert(1)", "javascript")]
    [InlineData("java\r\nscript:alert(1)", "javascript")]
    [InlineData("vbscript:msgbox(1)", "vbscript")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", "data")]
    [InlineData("file:///c:/windows/win.ini", "file")]
    [InlineData("joplin://x-callback-url/openNote?id=1", "joplin")]
    [InlineData("blob:https://example.com/1", "blob")]
    [InlineData("ftp://example.com", "ftp")]
    [InlineData("//example.com", "relative")]
    [InlineData("/path", "relative")]
    [InlineData("#anchor", "relative")]
    [InlineData("page.html", "relative")]
    [InlineData("", "relative")]
    [InlineData("https://", "https")]
    [InlineData("https:", "https")]
    [InlineData("1http://example.com", "relative")]
    public void Anything_else_is_not(string href, string scheme)
    {
        ContentRules.AllowedLink(href, out var found).ShouldBeNull();
        found.ShouldBe(scheme);
    }

    [Fact]
    public void An_address_that_is_enormous_is_not_a_link()
    {
        ContentRules.AllowedLink("https://example.com/" + new string('a', ContentRules.MaxHrefLength), out _).ShouldBeNull();
    }

    [Theory]
    [InlineData(":/0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData(":/0123456789abcdef0123456789abcdef#section", "0123456789abcdef0123456789abcdef")]
    [InlineData(":/0123456789ABCDEF0123456789abcdef", null)]
    [InlineData(":/0123456789abcdef", null)]
    [InlineData("https://example.com/:/0123456789abcdef0123456789abcdef", null)]
    [InlineData(null, null)]
    public void A_joplin_address_is_a_colon_slash_and_32_hex_digits(string? address, string? expected)
    {
        ContentRules.JoplinItemId(address).ShouldBe(expected);
    }

    [Fact]
    public void Text_loses_what_a_document_cannot_hold()
    {
        ContentRules.CleanText("a\0b\u0001c\ud800d\r\ne\rf\tg\uFFFEh😀").ShouldBe("abc\uFFFDd\ne\nf\tg\uFFFD" + "h😀");
    }

    [Fact]
    public void Limiting_does_not_cut_a_character_in_half_and_says_so()
    {
        var notes = new ConversionNotes();

        var limited = ContentRules.Limit("ab😀cd", 3, notes, "alt");

        limited.ShouldBe("ab");
        notes.ToList().ShouldBe([new ConversionNote(NoteCodes.Truncated, "alt")]);
    }

    [Theory]
    [InlineData("ts", "ts")]
    [InlineData("c++ {highlight}", "c++")]
    [InlineData("{.python}", "python")]
    [InlineData("<script>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_language_is_a_short_word(string? info, string? expected)
    {
        ContentRules.Language(info).ShouldBe(expected);
    }

    [Theory]
    [InlineData("<div><p>x</p></div>", 2)]
    [InlineData("<ul><li>a<br><img src=x></li></ul>", 2)]
    [InlineData("<br/><img src=x /><hr>", 0)]
    [InlineData("</div></div><p>x</p>", 1)]
    [InlineData("a < b and c > d", 0)]
    [InlineData("<DIV><Div>x</div></DIV><div></div>", 2)]
    // The parser adds the body and the row a table leaves out, and they close with the table.
    [InlineData("<table><tr><td>a</td></tr></table><p></p>", 4)]
    [InlineData("<table><td>a</table>", 4)]
    // A script's content is text; inside SVG it is markup, and a void element's name is an element there.
    [InlineData("<script>if (a<b) x('<div>')</script><p></p>", 1)]
    [InlineData("<svg><style><g><g></g></g></style></svg>", 4)]
    [InlineData("<svg><source><source>", 3)]
    // AngleSharp opens an element for a </br> in MathML, and what follows goes into it.
    [InlineData("<math></br></p>", 3)]
    // A browser ignores the slash on an element that is not void: these nest.
    [InlineData("<div/><div/><div/>", 3)]
    // A closing tag only closes the innermost element: a stray one, or one the parser ignores, takes no level off.
    [InlineData("<div></i><div></i><div></i>", 3)]
    [InlineData("<span><div></span><span><div></span>", 4)]
    [InlineData("<divé><divé></div>", 2)]
    // What a browser closes further out stays open: higher than the parser builds, never lower.
    [InlineData("<div><span><p>x</div><div></div>", 4)]
    [InlineData("<ul><li>a<li>b</ul><p></p>", 4)]
    [InlineData("<table><tr><td>a<td>b</table><p></p>", 6)]
    [InlineData("<p><b></p>x<p><b></p>x", 4)]
    public void The_depth_of_markup_is_counted_without_parsing_it(string html, int depth)
    {
        ContentRules.MarkupDepth(html).ShouldBe(depth);
    }

    /// <summary>Pieces of markup that between them use what the parser opens and closes in ways of its own.</summary>
    private static readonly string[] Tokens =
    [
        "<div>", "</div>", "<div/>", "<p>", "</p>", "<span>", "</span>", "<b>", "</b>", "<b class=x>", "<i>", "</i>", "<a>",
        "</a>", "<ul>", "</ul>", "<li>", "</li>", "<table>", "</table>", "<tr>", "</tr>", "<td>", "</td>", "<svg>", "</svg>",
        "<path/>", "<g>", "</g>", "<foreignObject>", "<math>", "<mi>", "<select>", "<option>", "<button>", "</button>",
        "<template>", "</template>", "<h1>", "</h2>", "<font color=red>", "</font>", "<nobr>", "<object>", "</object>",
        "<br>", "</br>", "<img>", "<dl>", "<dd>", "<blockquote>", "</blockquote>", "<pre>", "<textarea>", "</textarea>",
        "<script>", "</script>", "<!--", "-->", "<frameset>", "<body>", "</body>", "<html>", "<col>", "<tbody>", "x", " ",
    ];

    // The estimate counts open elements; the tree also has the void ones at its leaves (a br), which are never open.
    [Property(MaxTest = 3000)]
    public bool The_estimate_is_never_lower_than_the_depth_the_parser_builds(int[] picks)
    {
        var html = string.Concat(picks.Select(pick => Tokens[(int)((uint)pick % (uint)Tokens.Length)]));

        return ContentRules.MarkupDepth(html) + 1 >= ParsedDepth(html);
    }

    [Fact]
    public void The_estimate_keeps_up_with_formatting_the_parser_opens_again()
    {
        var html = string.Concat(Enumerable.Repeat("<p><b class=a><b class=b><b class=c><b class=d></p>x", 200));

        ContentRules.MarkupDepth(html).ShouldBeGreaterThanOrEqualTo(ParsedDepth(html));
    }

    /// <summary>How deep the elements nest once AngleSharp, the converter's parser, has built them; a leaf element counts.</summary>
    private static int ParsedDepth(string html)
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        using var document = parser.ParseDocument("");
        var deepest = 0;
        var stack = new Stack<(AngleSharp.Dom.INode Node, int Depth)>(parser.ParseFragment(html, document.Body!).Select(node => (node, 0)));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            var here = node is AngleSharp.Dom.IElement ? depth + 1 : depth;
            deepest = Math.Max(deepest, here);
            foreach (var child in node.ChildNodes)
            {
                stack.Push((child, here));
            }
        }

        return deepest;
    }

    [Theory]
    [InlineData("a<b>c</b>d", "a c d")]
    [InlineData("a < b &amp; c > d", "a < b & c > d")]
    [InlineData("a<!-- <b>x</b> -->b", "a b")]
    [InlineData("a<script>if (a < b) x()</script>b<STYLE type=x>p{}</style >c", "a b c")]
    [InlineData("a<!-- never closed <b>x</b>", "a ")]
    [InlineData("a<script>never closed", "a ")]
    [InlineData("a<scripts>b", "a b")]
    public void Markup_too_deep_to_parse_gives_its_text(string html, string text)
    {
        ContentRules.TextOfMarkup(html).ShouldBe(text);
    }

    [Theory]
    [InlineData("<!--")]
    [InlineData("<script>")]
    [InlineData("<a")]
    public void Reading_the_text_of_markup_takes_one_pass_whatever_is_left_open(string open)
    {
        var html = string.Concat(Enumerable.Repeat("<div>", ContentRules.MaxMarkupDepth + 1)) + string.Concat(Enumerable.Repeat(open + "x", 50_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        ContentRules.TextOfMarkup(html);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }
}
