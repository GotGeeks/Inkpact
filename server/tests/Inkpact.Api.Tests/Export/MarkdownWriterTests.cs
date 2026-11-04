using System.Text.Json.Nodes;
using Inkpact.Api.Export;
using static Inkpact.Api.Tests.Export.Pm;

namespace Inkpact.Api.Tests.Export;

/// <summary>A page's document as Markdown, node by node and mark by mark.</summary>
public class MarkdownWriterTests
{
    private static readonly Guid PictureId = Guid.Parse("3f2b8c1e-9d4a-4e6b-8a57-0c1d2e3f4a5b");
    private static readonly Guid FileId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid OtherPage = Guid.Parse("8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f00");

    private static readonly ExportLinks Links = new(
        id => id == PictureId ? "_attachments/mobile.png" : id == FileId ? "_attachments/my manual (1).pdf" : null,
        id => id == OtherPage ? "../Other.md" : null);

    private static readonly (string Name, JsonNode Document, string Expected)[] Cases =
    [
        ("a paragraph", Doc(P(T("Hello world"))), "Hello world\n"),
        ("two paragraphs", Doc(P(T("One")), P(T("Two"))), "One\n\nTwo\n"),
        ("an empty paragraph says nothing", Doc(P(), P(T("Text")), P()), "Text\n"),
        ("an empty document", Doc(), ""),
        ("headings of every level", Doc(H(1, T("A")), H(2, T("B")), H(6, T("F"))), "# A\n\n## B\n\n###### F\n"),
        ("a heading that ends in a hash after a letter", Doc(H(2, T("C#"))), "## C#\n"),
        ("a heading that ends in hashes after a space", Doc(H(2, T("a ##"))), "## a \\##\n"),
        ("a heading that is a hash", Doc(H(1, T("#"))), "# \\#\n"),
        ("a heading with a break is one line", Doc(H(1, T("a"), Br(), T("b"))), "# a b\n"),
        ("bold, italic, strike and code", Doc(P(T("b", "bold"), T(" "), T("i", "italic"), T(" "), T("s", "strike"), T(" "), T("c", "code"))), "**b** *i* ~~s~~ `c`\n"),
        ("underline is HTML", Doc(P(T("under", "underline"))), "<u>under</u>\n"),
        ("marks nested", Doc(P(T("both", "bold", "italic"))), "***both***\n"),
        ("marks over part of a run", Doc(P(T("a "), T("b c", "bold"), T(" d", "bold", "italic"), T(" e"))), "a **b c *d*** e\n"),
        ("white space stays outside the marks", Doc(P(T("a"), T(" bold ", "bold"), T("b"))), "a **bold** b\n"),
        ("a mark that is all white space is plain", Doc(P(T("a"), T("  ", "bold"), T("b"))), "a  b\n"),
        ("code with backticks in it", Doc(P(T("a ` b", "code"))), "``a ` b``\n"),
        ("code that starts with a backtick", Doc(P(T("`x", "code"))), "`` `x ``\n"),
        ("a link", Doc(P(Link("site", "https://example.com/a?b=c"))), "[site](https://example.com/a?b=c)\n"),
        ("a link with a title and a mark", Doc(P(Link("site", "https://example.com/", "The \"site\"", "bold"))), "**[site](https://example.com/ \"The \\\"site\\\"\")**\n"),
        ("a link with a space in its address", Doc(P(Link("a", "https://example.com/a b"))), "[a](<https://example.com/a b>)\n"),
        ("a link to another page of the export", Doc(P(Link("other", "/p/" + OtherPage))), "[other](../Other.md)\n"),
        ("a link to a page that is not in the export", Doc(P(Link("gone", "/p/3f2b8c1e-9d4a-4e6b-8a57-0c1d2e3f4a5b"))), "[gone](/p/3f2b8c1e-9d4a-4e6b-8a57-0c1d2e3f4a5b)\n"),
        ("a hard break", Doc(P(T("one"), Br(), T("two"))), "one\\\ntwo\n"),
        ("a hard break at the end says nothing", Doc(P(T("one"), Br())), "one\n"),
        ("a picture", Doc(Image(PictureId, "A [photo]")), "![A \\[photo\\]](_attachments/mobile.png)\n"),
        ("a picture that is not in the export", Doc(Image(Guid.Parse("11111111-2222-3333-4444-555555555555"), "x")), "![x](attachment:11111111-2222-3333-4444-555555555555)\n"),
        ("a file in a paragraph", Doc(P(T("see "), File(FileId, "my manual (1).pdf"))), "see [my manual (1).pdf](<_attachments/my manual (1).pdf>)\n"),
        ("a quote", Doc(Node("blockquote", null, P(T("one")), P(T("two")))), "> one\n>\n> two\n"),
        ("a quote in a quote", Doc(Node("blockquote", null, Node("blockquote", null, P(T("deep"))))), "> > deep\n"),
        ("a bullet list", Doc(Ul(Li(P(T("a"))), Li(P(T("b"))))), "- a\n- b\n"),
        ("a numbered list from three", Doc(Ol(3, Li(P(T("a"))), Li(P(T("b"))))), "3. a\n4. b\n"),
        ("a list in a list", Doc(Ul(Li(P(T("a")), Ul(Li(P(T("b"))))), Li(P(T("c"))))), "- a\n\n  - b\n\n- c\n"),
        ("a numbered list with a wide marker", Doc(Ol(9, Li(P(T("a")), P(T("more"))), Li(P(T("b"))))), "9. a\n\n   more\n\n10. b\n"),
        ("a list item with nothing in it", Doc(Ul(Li(), Li(P(T("a"))))), "-\n- a\n"),
        ("a task list", Doc(Node("taskList", null, Task(true, P(T("done"))), Task(false, P(T("todo"))))), "- [x] done\n- [ ] todo\n"),
        ("an empty task item", Doc(Node("taskList", null, Task(true), Task(false, P(T("b"))))), "- [x] &nbsp;\n- [ ] b\n"),
        ("lists that touch have the other marker", Doc(Ul(Li(P(T("a")))), Node("taskList", null, Task(false, P(T("b")))), Ul(Li(P(T("c"))))), "- a\n\n* [ ] b\n\n- c\n"),
        ("numbered lists that touch have the other marker", Doc(Ol(1, Li(P(T("a")))), Ol(1, Li(P(T("b"))))), "1. a\n\n1) b\n"),
        ("lists with a paragraph between them have one marker", Doc(Ul(Li(P(T("a")))), P(T("x")), Ul(Li(P(T("b"))))), "- a\n\nx\n\n- b\n"),
        ("a code block with a language", Doc(Code("ts", "const x = 1\nconst y = 2")), "```ts\nconst x = 1\nconst y = 2\n```\n"),
        ("a code block with fences in it", Doc(Code(null, "a\n```\nb")), "````\na\n```\nb\n````\n"),
        ("an empty code block", Doc(Code(null, "")), "```\n```\n"),
        ("a language that is not a word is left out", Doc(Code("a b\nc", "x")), "```\nx\n```\n"),
        ("a rule", Doc(P(T("a")), Node("horizontalRule"), P(T("b"))), "a\n\n---\n\nb\n"),
        ("a code block in a list", Doc(Ul(Li(Code("js", "x();")))), "- ```js\n  x();\n  ```\n"),
        ("a pipe table", Doc(Table(Row(Cell(true, P(T("a"))), Cell(true, P(T("b"), Br(), T("c")))), Row(Cell(false, P(T("1"))), Cell(false, P(T("x|y")))))), "| a | b<br>c |\n| --- | --- |\n| 1 | x\\|y |\n"),
        ("a table's alignment", Doc(Table(Row(Cell(true, P(T("a")), align: "left"), Cell(true, P(T("b")), align: "center"), Cell(true, P(T("c")), align: "right")), Row(Cell(false), Cell(false), Cell(false)))), "| a | b | c |\n| :--- | :---: | ---: |\n|  |  |  |\n"),
        ("a table with a merged cell is HTML", Doc(Table(Row(Cell(true, P(T("a")), colspan: 2)), Row(Cell(false, P(T("1"))), Cell(false, P(T("2")))))), "<table>\n<tr>\n<th colspan=\"2\"><p>a</p></th>\n</tr>\n<tr>\n<td><p>1</p></td>\n<td><p>2</p></td>\n</tr>\n</table>\n"),
        ("a table without a header row is HTML", Doc(Table(Row(Cell(false, P(T("a")))))), "<table>\n<tr>\n<td><p>a</p></td>\n</tr>\n</table>\n"),
        ("a pipe in a link's address and title in a table has a backslash", Doc(Table(Row(Cell(true, P(Link("x", "https://x.org/?q=a|b", "c|d")))))), "| [x](https://x.org/?q=a\\|b \"c\\|d\") |\n| --- |\n"),
        ("a table with a pipe in code is HTML", Doc(Table(Row(Cell(true, P(T("a|b", "code")))))), "<table>\n<tr>\n<th><p><code>a|b</code></p></th>\n</tr>\n</table>\n"),
        ("code in an HTML table has no blank line", Doc(Table(Row(Cell(false, Code(null, "a\n\n \nb"))))), "<table>\n<tr>\n<td><pre><code>a&#10;&#10; \nb</code></pre></td>\n</tr>\n</table>\n"),
        ("a table in a table keeps the lines of its code", Doc(Table(Row(Cell(false, Table(Row(Cell(false, Code(null, "x\ny")))))))), "<table>\n<tr>\n<td><table>\n<tr>\n<td><pre><code>x\ny</code></pre></td>\n</tr>\n</table></td>\n</tr>\n</table>\n"),
        ("a file after a bang", Doc(P(T("Look!"), File(FileId, "my manual (1).pdf"))), "Look\\![my manual (1).pdf](<_attachments/my manual (1).pdf>)\n"),
        ("a link after a bang", Doc(P(T("Look!"), Link("site", "https://example.com/"))), "Look\\![site](https://example.com/)\n"),
        ("a table cell with a list is HTML", Doc(Table(Row(Cell(true, P(T("a")))), Row(Cell(false, Ul(Li(P(T("x")))))))), "<table>\n<tr>\n<th><p>a</p></th>\n</tr>\n<tr>\n<td><ul><li><p>x</p></li></ul></td>\n</tr>\n</table>\n"),
    ];

    public static TheoryData<int> CaseNumbers => [.. Enumerable.Range(0, Cases.Length)];

    [Theory]
    [MemberData(nameof(CaseNumbers))]
    public void A_document_is_written_as_the_markdown_that_says_it(int number)
    {
        var (name, document, expected) = Cases[number];

        Markdown(document, Links).ShouldBe(expected, name);
    }

    // ---- text that Markdown would read as markup -----------------------------------------------------------------------

    [Theory]
    [InlineData("a * b", "a \\* b")]
    [InlineData("snake_case and _lead and trail_", "snake_case and \\_lead and trail\\_")]
    [InlineData("a `tick` b", "a \\`tick\\` b")]
    [InlineData("[not a link](x)", "\\[not a link\\](x)")]
    [InlineData("<b>not html</b> and a < b", "\\<b>not html\\</b> and a < b")]
    [InlineData("a | b", "a \\| b")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("1 ~ 2 ^ 3", "1 \\~ 2 \\^ 3")]
    [InlineData("a == b and a ++ b and a = b + c", "a \\=\\= b and a \\+\\+ b and a = b + c")]
    [InlineData("costs $5 and $6", "costs \\$5 and \\$6")]
    [InlineData("AT&T and &amp; and &#169;", "AT&T and \\&amp; and \\&#169;")]
    [InlineData("see https://example.com and www.example.com", "see https\\://example.com and www\\.example.com")]
    [InlineData("mail mailto:me@example.com, call tel:+3112 at the hotel:", "mail mailto\\:me@example.com, call tel\\:+3112 at the hotel:")]
    public void Text_is_escaped_where_markdown_would_read_it_as_markup_and_only_there(string text, string expected) =>
        Markdown(Doc(P(T(text)))).ShouldBe(expected + "\n");

    [Theory]
    [InlineData("# not a heading", "\\# not a heading")]
    [InlineData("#hashtag", "\\#hashtag")]
    [InlineData("> not a quote", "\\> not a quote")]
    [InlineData("- not a list", "\\- not a list")]
    [InlineData("+ not a list", "\\+ not a list")]
    [InlineData("1. not a list", "1\\. not a list")]
    [InlineData("12) not a list", "12\\) not a list")]
    [InlineData("---", "\\---")]
    [InlineData("===", "\\=\\=\\=")]
    [InlineData(": not a definition", "\\: not a definition")]
    [InlineData("-5 degrees and 1.5 litres", "-5 degrees and 1.5 litres")]
    [InlineData("2024. A year", "2024\\. A year")]
    public void Text_that_starts_a_line_like_markup_gets_a_backslash(string text, string expected) =>
        Markdown(Doc(P(T(text)))).ShouldBe(expected + "\n");

    [Fact]
    public void A_line_after_a_hard_break_is_a_line_start_too()
    {
        Markdown(Doc(P(T("one"), Br(), T("# two"), Br(), T("- three")))).ShouldBe("one\\\n\\# two\\\n\\- three\n");
    }

    [Fact]
    public void A_line_start_after_white_space_or_with_a_tab_is_a_line_start_too()
    {
        Markdown(Doc(P(T("one"), Br(), T("  # two"), Br(), T("   > three"), Br(), T("-\tfour"), Br(), T("1.\tfive"))))
            .ShouldBe("one\\\n  \\# two\\\n   \\> three\\\n\\-\tfour\\\n1\\.\tfive\n");
    }

    [Fact]
    public void Text_in_a_table_cell_has_no_line_starts()
    {
        Markdown(Doc(Table(Row(Cell(true, P(T("# a")))), Row(Cell(false, P(T("- b"))))))).ShouldBe("| # a |\n| --- |\n| - b |\n");
    }

    // ---- marks where Markdown cannot write them ----------------------------------------------------------------------------

    [Fact]
    public void A_mark_that_starts_with_punctuation_inside_a_word_is_HTML_because_Markdown_would_not_open_it()
    {
        Markdown(Doc(P(T("word"), T("(s)", "bold"), T(" and ")))).ShouldBe("word<strong>(s)</strong> and\n");
        Markdown(Doc(P(T("quote "), T("\"yes\"", "italic"), T("!")))).ShouldBe("quote *\"yes\"*!\n");
        Markdown(Doc(P(T("\"yes", "italic"), T("no"), T("\"", "italic")))).ShouldBe("*\"yes*no<em>\"</em>\n");
    }

    [Fact]
    public void Two_runs_that_touch_do_not_run_their_markers_together()
    {
        Markdown(Doc(P(T("a", "bold"), T("b", "italic")))).ShouldBe("**a**<em>b</em>\n");
        Markdown(Doc(P(T("a", "italic"), T("b", "bold")))).ShouldBe("*a*<strong>b</strong>\n");
    }

    [Fact]
    public void Text_with_a_mark_and_a_link_across_pieces_is_written_once_each()
    {
        var content = Doc(P(Link("one ", "https://example.com/", null, "bold"), Link("two", "https://example.com/")));

        Markdown(content).ShouldBe("**[one](https://example.com/)** [two](https://example.com/)\n");
    }

    [Fact]
    public void A_block_the_schema_does_not_have_is_left_out()
    {
        Markdown(Doc(Node("callout", null, P(T("x"))), P(T("kept")))).ShouldBe("kept\n");
    }

    [Fact]
    public void A_document_nested_deeper_than_anyone_writes_is_written_to_a_bound_and_does_not_fail()
    {
        JsonNode inner = P(T("deep"));
        for (var i = 0; i < 150; i++)
        {
            inner = Node("blockquote", null, inner);
        }

        Should.NotThrow(() => Markdown(Doc(inner)));
    }
}
