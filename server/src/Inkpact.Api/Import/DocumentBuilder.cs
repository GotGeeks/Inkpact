using System.Globalization;
using System.Text.RegularExpressions;

namespace Inkpact.Api.Import;

/// <summary>The formatting on a piece of text while a converter walks through it.</summary>
internal sealed record TextMarks(
    bool Bold = false, bool Italic = false, bool Strike = false, bool Underline = false, bool Code = false,
    string? Href = null, string? Title = null)
{
    /// <summary>
    /// The marks as the editor stores them. The editor's inline code excludes every other mark (including a link), so
    /// code on its own keeps only itself, and code inside a link keeps the link.
    /// </summary>
    public IReadOnlyList<PmMark> ToPm(ConversionNotes notes)
    {
        var marks = new List<PmMark>(4);
        if (Code && Href is null)
        {
            return [new PmMark("code")];
        }

        if (Code)
        {
            notes.Add(NoteCodes.MarkDropped, "code");
        }

        if (Bold)
        {
            marks.Add(new PmMark("bold"));
        }

        if (Italic)
        {
            marks.Add(new PmMark("italic"));
        }

        if (Strike)
        {
            marks.Add(new PmMark("strike"));
        }

        if (Underline)
        {
            marks.Add(new PmMark("underline"));
        }

        if (Href is not null)
        {
            var attributes = new Dictionary<string, object?> { ["href"] = Href };
            if (!string.IsNullOrEmpty(Title))
            {
                attributes["title"] = Title;
            }

            marks.Add(new PmMark("link", attributes));
        }

        return marks;
    }
}

/// <summary>
/// Collects what a converter finds into blocks: text and inline nodes are gathered into a paragraph, and a block ends the
/// paragraph before it. HTML collapses white space the way a browser does; Markdown keeps its text as it is.
/// </summary>
internal sealed partial class BlockSink(bool collapseWhiteSpace, ConversionNotes notes)
{
    private readonly List<PmNode> _blocks = [];
    private readonly List<(string? Text, TextMarks Marks, PmNode? Node)> _runs = [];

    [GeneratedRegex(@"[ \t\r\n\f]+")]
    private static partial Regex WhiteSpace();

    public void AddText(string text, TextMarks marks)
    {
        text = ContentRules.CleanText(text);
        if (collapseWhiteSpace)
        {
            text = WhiteSpace().Replace(text, " ");
            // After a space, or at the start of a paragraph, a space adds nothing.
            var afterSpace = _runs.Count == 0
                ? true
                : _runs[^1] is { Text: { } last } && last.EndsWith(' ');
            if (afterSpace && text.StartsWith(' '))
            {
                text = text[1..];
            }
            else if (_runs.Count > 0 && _runs[^1].Node is { Type: "hardBreak" } && text.StartsWith(' '))
            {
                text = text[1..];
            }
        }

        if (text.Length > 0)
        {
            _runs.Add((text, marks, null));
        }
    }

    /// <summary>An inline node: a hard break, or a file.</summary>
    public void AddInline(PmNode node) => _runs.Add((null, new TextMarks(), node));

    public void AddBlock(PmNode block)
    {
        EndParagraph();
        _blocks.Add(block);
    }

    public void AddBlocks(IEnumerable<PmNode> blocks)
    {
        foreach (var block in blocks)
        {
            AddBlock(block);
        }
    }

    public bool HasPendingInline => _runs.Count > 0;

    public void EndParagraph()
    {
        var inline = TakeInline();
        if (inline.Count > 0)
        {
            _blocks.Add(PmNode.Element("paragraph", inline));
        }
    }

    public List<PmNode> Finish()
    {
        EndParagraph();
        return [.. _blocks];
    }

    private void TrimEnd()
    {
        while (_runs.Count > 0 && _runs[^1].Text is { } last)
        {
            var trimmed = last.TrimEnd(' ');
            if (trimmed.Length > 0)
            {
                _runs[^1] = (trimmed, _runs[^1].Marks, null);
                return;
            }

            _runs.RemoveAt(_runs.Count - 1);
        }
    }

    private void TrimStart()
    {
        while (_runs.Count > 0 && _runs[0].Text is { } first)
        {
            var trimmed = first.TrimStart(' ');
            if (trimmed.Length > 0)
            {
                _runs[0] = (trimmed, _runs[0].Marks, null);
                return;
            }

            _runs.RemoveAt(0);
        }
    }

    private List<PmNode> TakeInline()
    {
        // A paragraph that an image or a list cut out of a longer one keeps no space at its edges.
        while (collapseWhiteSpace && _runs.Count > 0 && _runs[^1].Node is { Type: "hardBreak" })
        {
            _runs.RemoveAt(_runs.Count - 1);
        }

        TrimEnd();
        TrimStart();

        var nodes = new List<PmNode>(_runs.Count);
        var pendingText = new System.Text.StringBuilder();
        TextMarks? pendingMarks = null;
        void FlushText()
        {
            if (pendingMarks is not null && pendingText.Length > 0)
            {
                nodes.Add(PmNode.TextNode(pendingText.ToString(), pendingMarks.ToPm(notes)));
            }

            pendingText.Clear();
            pendingMarks = null;
        }

        foreach (var (text, marks, node) in _runs)
        {
            if (node is not null)
            {
                FlushText();
                nodes.Add(node);
                continue;
            }

            // Neighbours with the same formatting are one text, as the editor keeps them.
            if (pendingMarks is not null && pendingMarks != marks)
            {
                FlushText();
            }

            pendingMarks = marks;
            pendingText.Append(text);
        }

        FlushText();
        _runs.Clear();
        return nodes;
    }
}

/// <summary>The shapes the editor's schema asks of a node's content, so that no converter has to remember them.</summary>
internal static class Shapes
{
    public static PmNode Paragraph(params PmNode[] inline) => PmNode.Element("paragraph", inline);

    /// <summary>
    /// A heading (or other block with only inline content) from what a sink collected: the paragraphs are joined with
    /// line breaks, and blocks that cannot be inside it (an image) follow it.
    /// </summary>
    public static IEnumerable<PmNode> Textblock(string type, Dictionary<string, object?>? attributes, IReadOnlyList<PmNode> blocks)
    {
        var inline = new List<PmNode>();
        var after = new List<PmNode>();
        foreach (var block in blocks)
        {
            if (block.Type != "paragraph")
            {
                after.Add(block);
                continue;
            }

            if (inline.Count > 0)
            {
                inline.Add(PmNode.Element("hardBreak"));
            }

            inline.AddRange(block.Content ?? []);
        }

        if (inline.Count > 0)
        {
            yield return PmNode.Element(type, inline, attributes);
        }

        foreach (var node in after)
        {
            yield return node;
        }
    }

    /// <summary>A list or task item holds a paragraph first, then any blocks.</summary>
    public static PmNode ListItem(string type, IReadOnlyList<PmNode> blocks, Dictionary<string, object?>? attributes = null)
    {
        var content = new List<PmNode>(blocks.Count + 1);
        if (blocks.Count == 0 || blocks[0].Type != "paragraph")
        {
            content.Add(PmNode.Element("paragraph"));
        }

        content.AddRange(blocks);
        return PmNode.Element(type, content, attributes);
    }

    public static List<PmNode> CellContent(IReadOnlyList<PmNode> blocks) =>
        blocks.Count == 0 ? [PmNode.Element("paragraph")] : [.. blocks];

    /// <summary>
    /// The text as paragraphs, line by line, for content that cannot be converted any better: blank lines separate
    /// paragraphs, and the lines of one paragraph are joined by line breaks.
    /// </summary>
    public static List<PmNode> PlainParagraphs(string text)
    {
        var paragraphs = new List<PmNode>();
        var inline = new List<PmNode>();
        void End()
        {
            if (inline.Count > 0)
            {
                paragraphs.Add(PmNode.Element("paragraph", inline));
                inline = [];
            }
        }

        foreach (var line in ContentRules.CleanText(text).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                End();
                continue;
            }

            if (inline.Count > 0)
            {
                inline.Add(PmNode.Element("hardBreak"));
            }

            inline.Add(PmNode.TextNode(line.Trim()));
        }

        End();
        return paragraphs;
    }

    public static Dictionary<string, object?> Attributes(string name, object? value) => new() { [name] = value };

    /// <summary>The number a numbered list starts at, as written; 1 when it says nothing sensible.</summary>
    public static int ListStart(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is > 1 and <= 1_000_000 ? number : 1;

    /// <summary>
    /// The lists that <paramref name="items"/> make. A list in the editor is all checkboxes or none, so a run of each kind
    /// is a list of its own, and only the first run of a numbered list starts at its number. Nested <paramref name="depth"/>
    /// blocks deep, past what the editor gets, the items' blocks stay without the list.
    /// </summary>
    public static List<PmNode> Lists(IReadOnlyList<RawItem> items, bool ordered, int start, int depth, ConversionNotes notes)
    {
        if (depth >= ContentRules.MaxBlockNesting)
        {
            notes.Add(NoteCodes.NestingFlattened, "list");
            return [.. items.SelectMany(item => item.Blocks)];
        }

        var lists = new List<PmNode>();
        for (var at = 0; at < items.Count;)
        {
            var task = items[at].Task;
            var run = items.Skip(at).TakeWhile(item => item.Task == task).ToList();
            at += run.Count;

            if (task)
            {
                lists.Add(PmNode.Element("taskList", run.Select(item => ListItem(
                    "taskItem", item.Blocks, item.Checked ? Attributes("checked", true) : null))));
            }
            else
            {
                var attributes = ordered && start != 1 ? Attributes("start", start) : null;
                lists.Add(PmNode.Element(
                    ordered ? "orderedList" : "bulletList", run.Select(item => ListItem("listItem", item.Blocks)), attributes));
                start = 1;
            }
        }

        return lists;
    }

    /// <summary>
    /// The table that <paramref name="rows"/> make, made regular. Nested <paramref name="depth"/> blocks deep past what the
    /// editor gets, or too big to make regular (the cells that fill the rows are counted), the cells' blocks stay without
    /// the table.
    /// </summary>
    public static List<PmNode> Table(IReadOnlyList<IReadOnlyList<RawCell>> rows, int depth, ConversionNotes notes)
    {
        if ((depth >= ContentRules.MaxBlockNesting ? null : TableGrid.Build(rows)) is { } table)
        {
            return [table];
        }

        var blocks = rows.SelectMany(row => row).SelectMany(cell => cell.Content).ToList();
        if (blocks.Count > 0)
        {
            notes.Add(NoteCodes.NestingFlattened, "table");
        }

        return blocks;
    }
}

/// <summary>A list item as a converter found it: with a checkbox or without, and its blocks.</summary>
internal sealed record RawItem(bool Task, bool Checked, IReadOnlyList<PmNode> Blocks);

/// <summary>A cell as a converter found it, before the table is made regular.</summary>
internal sealed record RawCell(bool Header, IReadOnlyList<PmNode> Content, int ColSpan = 1, int RowSpan = 1);

/// <summary>
/// Makes a table the way the editor keeps one: every column of every row taken by exactly one cell. The editor's table
/// plugin repairs a table that is not by changing cells, and every client that opened the page would write its own repair;
/// so the import does it.
/// </summary>
internal static class TableGrid
{
    private sealed record Placed(RawCell Cell, int Column, int ColSpan, int RowSpan);

    public static PmNode? Build(IReadOnlyList<IReadOnlyList<RawCell>> rows)
    {
        if (Place(rows) is not { } placed)
        {
            return null;
        }

        var width = placed.SelectMany(row => row).Select(cell => cell.Column + cell.ColSpan).DefaultIfEmpty(0).Max();
        if (width == 0 || (long)width * placed.Count > ContentRules.MaxTableCells)
        {
            return null;
        }

        // Now that the width is known, every column that no cell takes gets an empty one.
        var remaining = new int[width];
        var tableRows = new List<PmNode>(placed.Count);
        foreach (var row in placed)
        {
            var cells = new List<PmNode>(width);
            var next = 0;
            for (var column = 0; column < width;)
            {
                if (next < row.Count && row[next].Column == column)
                {
                    var cell = row[next++];
                    for (var c = column; c < column + cell.ColSpan; c++)
                    {
                        remaining[c] = cell.RowSpan;
                    }

                    cells.Add(Cell(cell.Cell, cell.ColSpan, cell.RowSpan));
                    column += cell.ColSpan;
                }
                else
                {
                    if (remaining[column] == 0)
                    {
                        cells.Add(Cell(new RawCell(false, []), 1, 1));
                    }

                    column++;
                }
            }

            for (var c = 0; c < width; c++)
            {
                remaining[c] = Math.Max(0, remaining[c] - 1);
            }

            tableRows.Add(PmNode.Element("tableRow", cells));
        }

        return PmNode.Element("table", tableRows);
    }

    /// <summary>
    /// Where each cell goes, the way a browser places them: in the first column that no cell from above still takes. A
    /// column span that would run into a cell from above stops before it. A row without cells of its own (all of it taken
    /// from above, or nothing in it) shows with no height, so it is left out, and the cells that span it span one row less.
    /// Null when one row is already wider than a table may have cells.
    /// </summary>
    private static List<List<Placed>>? Place(IReadOnlyList<IReadOnlyList<RawCell>> rows)
    {
        var placed = new List<List<Placed>>(rows.Count);
        // For every column, how many more rows (this one included) a cell from above still takes.
        var covered = new List<int>();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new List<Placed>(rows[r].Count);
            var column = 0;
            foreach (var cell in rows[r])
            {
                while (column < covered.Count && covered[column] > 0)
                {
                    column++;
                }

                var colSpan = 1;
                while (colSpan < Math.Clamp(cell.ColSpan, 1, ContentRules.MaxCellSpan)
                    && (column + colSpan >= covered.Count || covered[column + colSpan] == 0))
                {
                    colSpan++;
                }

                var rowSpan = Math.Clamp(cell.RowSpan, 1, Math.Min(ContentRules.MaxCellSpan, rows.Count - r));
                if (column + colSpan > ContentRules.MaxTableCells)
                {
                    return null;
                }

                while (covered.Count < column + colSpan)
                {
                    covered.Add(0);
                }

                for (var c = column; c < column + colSpan; c++)
                {
                    covered[c] = rowSpan;
                }

                row.Add(new Placed(cell, column, colSpan, rowSpan));
                column += colSpan;
            }

            placed.Add(row);
            for (var c = 0; c < covered.Count; c++)
            {
                covered[c] = Math.Max(0, covered[c] - 1);
            }
        }

        var empty = placed.Select(row => row.Count == 0).ToArray();
        return
        [
            .. placed
                .Select((row, r) => row.Select(cell => cell with { RowSpan = cell.RowSpan - empty.Skip(r + 1).Take(cell.RowSpan - 1).Count(e => e) }).ToList())
                .Where((_, r) => !empty[r]),
        ];
    }

    private static PmNode Cell(RawCell cell, int colSpan, int rowSpan)
    {
        var attributes = new Dictionary<string, object?>();
        if (colSpan > 1)
        {
            attributes["colspan"] = colSpan;
        }

        if (rowSpan > 1)
        {
            attributes["rowspan"] = rowSpan;
        }

        return PmNode.Element(cell.Header ? "tableHeader" : "tableCell", Shapes.CellContent(cell.Content.ToList()), attributes);
    }
}
