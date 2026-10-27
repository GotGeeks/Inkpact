using FsCheck.Xunit;
using Inkpact.Api.Import;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// Whatever the spans, the table the import writes is one the editor's table plugin has nothing to repair in: every column
/// of every row taken by exactly one cell, as its <c>TableMap</c> reads a table. A repair would be written to the page by
/// every client that opened it.
/// </summary>
public sealed class TableGridTests
{
    [Property(MaxTest = 2000)]
    public bool Every_column_of_every_row_is_taken_by_exactly_one_cell_and_no_cell_is_lost(int[] picks)
    {
        var rows = Rows(picks, out var cellCount);

        var table = TableGrid.Build(rows);

        if (cellCount == 0)
        {
            return table is null;
        }

        return table is not null && IsRegular(table) && Texts(table).Order().SequenceEqual(Enumerable.Range(0, cellCount).Select(Name).Order());
    }

    [Fact]
    public void A_column_span_that_runs_into_a_row_span_from_above_stops_before_it()
    {
        var table = TableGrid.Build(
        [
            [Cell("a"), Cell("b", rowSpan: 2)],
            [Cell("c", colSpan: 2)],
        ])!;

        IsRegular(table).ShouldBeTrue();
        Spans(table).ShouldBe(["a 1x1", "b 1x2", "c 1x1"]);
    }

    [Fact]
    public void A_row_with_no_cells_of_its_own_is_left_out_and_the_spans_across_it_are_shorter()
    {
        var table = TableGrid.Build(
        [
            [Cell("A", rowSpan: 2), Cell("B", rowSpan: 2)],
            [],
            [Cell("C"), Cell("D")],
        ])!;

        IsRegular(table).ShouldBeTrue();
        table.Content!.Count.ShouldBe(2);
        Spans(table).ShouldBe(["A 1x1", "B 1x1", "C 1x1", "D 1x1"]);
    }

    [Fact]
    public void A_row_span_keeps_the_rows_it_spans_that_have_cells()
    {
        var table = TableGrid.Build(
        [
            [Cell("A", rowSpan: 3), Cell("B")],
            [],
            [Cell("C")],
        ])!;

        IsRegular(table).ShouldBeTrue();
        Spans(table).ShouldBe(["A 1x2", "B 1x1", "C 1x1"]);
    }

    private static string Name(int index) => $"c{index}";

    private static RawCell Cell(string text, int colSpan = 1, int rowSpan = 1) =>
        new(false, [Shapes.Paragraph(PmNode.TextNode(text))], colSpan, rowSpan);

    /// <summary>Rows of cells with spans from 1 to 3 (now and then a larger one), and now and then a row without cells.</summary>
    private static List<IReadOnlyList<RawCell>> Rows(int[] picks, out int cellCount)
    {
        var rows = new List<IReadOnlyList<RawCell>>();
        var row = new List<RawCell>();
        cellCount = 0;
        foreach (var pick in picks.Select(p => (int)((uint)p % 1000)))
        {
            if (pick % 5 == 0)
            {
                rows.Add(row);
                row = [];
                continue;
            }

            var colSpan = pick % 47 == 0 ? 200 : 1 + (pick / 5 % 3);
            var rowSpan = pick % 53 == 0 ? 200 : 1 + (pick / 15 % 3);
            row.Add(Cell(Name(cellCount++), colSpan, rowSpan));
        }

        rows.Add(row);
        return rows;
    }

    /// <summary>The table as ProseMirror's <c>TableMap</c> reads it: no two cells in one place, no hole, no span past the end.</summary>
    private static bool IsRegular(PmNode table)
    {
        const int MaxWidth = 16_384;
        var rows = table.Content!;
        var taken = new List<bool[]>();
        var width = -1;
        for (var r = 0; r < rows.Count; r++)
        {
            while (taken.Count <= r)
            {
                taken.Add(new bool[MaxWidth]);
            }

            var column = 0;
            foreach (var cell in rows[r].Content ?? [])
            {
                while (taken[r][column])
                {
                    column++;
                }

                var colSpan = Attribute(cell, "colspan");
                var rowSpan = Attribute(cell, "rowspan");
                if (r + rowSpan > rows.Count)
                {
                    return false;
                }

                for (var y = r; y < r + rowSpan; y++)
                {
                    while (taken.Count <= y)
                    {
                        taken.Add(new bool[MaxWidth]);
                    }

                    for (var x = column; x < column + colSpan; x++)
                    {
                        if (taken[y][x])
                        {
                            return false;
                        }

                        taken[y][x] = true;
                    }
                }

                column += colSpan;
            }

            var rowWidth = Array.LastIndexOf(taken[r], true) + 1;
            if ((width >= 0 && rowWidth != width) || taken[r].Take(rowWidth).Any(t => !t))
            {
                return false;
            }

            width = rowWidth;
        }

        return true;
    }

    private static int Attribute(PmNode cell, string name) =>
        cell.Attrs is { } attributes && attributes.TryGetValue(name, out var value) ? (int)value! : 1;

    private static IEnumerable<string> Texts(PmNode table) =>
        table.Content!.SelectMany(row => row.Content ?? []).SelectMany(cell => cell.Content ?? []).SelectMany(block => block.Content ?? [])
            .Select(text => text.Text!);

    private static IEnumerable<string> Spans(PmNode table) =>
        table.Content!.SelectMany(row => row.Content ?? []).Where(cell => cell.Content![0].Content is not null)
            .Select(cell => $"{cell.Content![0].Content![0].Text} {Attribute(cell, "colspan")}x{Attribute(cell, "rowspan")}");
}
