using Inkpact.Api.Import;

namespace Inkpact.Api.Tests.Import;

public sealed class JexItemTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void A_note_has_a_title_a_body_and_properties()
    {
        var item = JexItem.Parse($"My note\n\nFirst line\n\nSecond line\n\nid: {Id}\nparent_id: \nis_todo: 1\ntype_: 1")!;

        item.Title.ShouldBe("My note");
        item.Body.ShouldBe("First line\n\nSecond line");
        item.Id.ShouldBe(Id);
        item.Type.ShouldBe(JexTypes.Note);
        item.Flag("is_todo").ShouldBeTrue();
        item.Get("parent_id").ShouldBeEmpty();
    }

    [Fact]
    public void A_notebook_has_a_title_and_properties_and_no_body()
    {
        var item = JexItem.Parse($"Projects\n\nid: {Id}\nparent_id: \ntype_: 2")!;

        item.Title.ShouldBe("Projects");
        item.Body.ShouldBeEmpty();
        item.Type.ShouldBe(JexTypes.Folder);
    }

    [Fact]
    public void A_body_that_looks_like_properties_is_still_the_body()
    {
        // The properties are read from the end, up to the blank line, so what the body says cannot change them.
        var body = "type_: 2\nid: 00000000000000000000000000000000\n\nis_todo: 1\ntype_: 4";
        var item = JexItem.Parse($"Tricky\n\n{body}\n\nid: {Id}\ntype_: 1")!;

        item.Body.ShouldBe(body);
        item.Id.ShouldBe(Id);
        item.Type.ShouldBe(JexTypes.Note);
        item.Flag("is_todo").ShouldBeFalse();
    }

    [Fact]
    public void An_empty_body_and_an_empty_title_are_read()
    {
        var empty = JexItem.Parse($"Only a title\n\n\n\nid: {Id}\ntype_: 1")!;
        var untitled = JexItem.Parse($"\n\nthe body\n\nid: {Id}\ntype_: 1")!;

        empty.Title.ShouldBe("Only a title");
        empty.Body.ShouldBeEmpty();
        untitled.Title.ShouldBeEmpty();
        untitled.Body.ShouldBe("the body");
    }

    [Fact]
    public void Windows_line_ends_and_a_trailing_newline_are_read()
    {
        var item = JexItem.Parse($"Title\r\n\r\nBody\r\n\r\nid: {Id}\r\ntype_: 1\r\n")!;

        item.Title.ShouldBe("Title");
        item.Id.ShouldBe(Id);
        item.Type.ShouldBe(JexTypes.Note);
        item.Body.TrimEnd('\r').ShouldBe("Body");
    }

    [Fact]
    public void A_value_may_contain_a_colon()
    {
        var item = JexItem.Parse($"T\n\nid: {Id}\nsource_url: https://example.com:8080/a\ntype_: 1")!;

        item.Get("source_url").ShouldBe("https://example.com:8080/a");
    }

    [Theory]
    [InlineData("")]
    [InlineData("just a title")]
    [InlineData("Title\n\nbody without any property lines")]
    public void A_file_without_properties_is_not_an_item(string text)
    {
        JexItem.Parse(text).ShouldBeNull();
    }

    [Fact]
    public void A_file_with_a_hostile_number_of_properties_is_not_an_item()
    {
        var text = "T\n\n" + string.Join('\n', Enumerable.Range(0, 100_000).Select(i => $"k{i}: v")) + "\ntype_: 1";

        JexItem.Parse(text).ShouldBeNull();
    }

    [Fact]
    public void A_missing_type_is_type_zero()
    {
        JexItem.Parse($"T\n\nid: {Id}")!.Type.ShouldBe(0);
    }

    [Theory]
    [InlineData("1793358000000", 1793358000000L)]
    [InlineData("2026-01-02T03:04:05.000Z", 1767323045000L)]
    [InlineData("2026-01-02T05:04:05.000+02:00", 1767323045000L)]
    public void A_time_is_milliseconds_or_iso_text(string value, long unixMilliseconds)
    {
        JexItem.ParseTime(value)!.Value.ToUnixTimeMilliseconds().ShouldBe(unixMilliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("99999999999999999")]
    [InlineData("yesterday")]
    [InlineData("1960-01-01T00:00:00Z")]
    public void Zero_nothing_and_what_is_not_a_time_are_no_time(string value)
    {
        JexItem.ParseTime(value).ShouldBeNull();
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("", false)]
    public void An_id_is_32_lower_case_hex_digits(string value, bool valid)
    {
        JexItem.IsId(value).ShouldBe(valid);
    }
}
