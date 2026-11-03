using Inkpact.Api.Search;

namespace Inkpact.Api.Tests.Search;

public class SearchHeadlineTests
{
    private const string Start = "\u0001";
    private const string Stop = "\u0002";
    private const string End = "\u0003";

    private static string Show(IEnumerable<SearchSegment> segments) =>
        string.Concat(segments.Select(s => s.Match ? $"[{s.Text}]" : s.Text));

    [Fact]
    public void A_match_in_the_middle_is_marked() =>
        Show(SearchHeadline.Segments($"Weekly {Start}meeting{Stop} notes")).ShouldBe("Weekly [meeting] notes");

    [Fact]
    public void A_match_at_the_start_and_at_the_end_is_marked()
    {
        Show(SearchHeadline.Segments($"{Start}Weekly{Stop} notes {Start}meeting{Stop}")).ShouldBe("[Weekly] notes [meeting]");
    }

    [Fact]
    public void Two_matches_next_to_each_other_are_one_piece() =>
        SearchHeadline.Segments($"{Start}one{Stop}{Start}two{Stop}").ShouldBe([new SearchSegment("onetwo", true)]);

    [Fact]
    public void A_text_without_a_match_is_one_plain_piece() =>
        SearchHeadline.Segments("Just a title").ShouldBe([new SearchSegment("Just a title", false)]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_is_no_segments(string? headline) => SearchHeadline.Segments(headline).ShouldBeEmpty();

    [Fact]
    public void The_end_of_a_fragment_becomes_a_gap_between_the_two()
    {
        var segments = SearchHeadline.Segments($"before {Start}sync{Stop} after{End}and {Start}sync{Stop} again");

        Show(segments).ShouldBe("before [sync] after … and [sync] again");
        segments.Count(s => s.Match).ShouldBe(2);
    }

    [Fact]
    public void A_gap_does_not_leave_a_marked_piece_open() =>
        SearchHeadline.Segments($"{Start}a{End}b").Select(s => s.Match).ShouldBe([true, false]);

    [Fact]
    public void A_mark_that_is_never_closed_runs_to_the_end() =>
        Show(SearchHeadline.Segments($"x {Start}open")).ShouldBe("x [open]");

    [Fact]
    public void A_snippet_without_a_match_is_left_out_because_the_page_matched_by_something_else()
    {
        var plain = SearchHeadline.Segments("The start of a page that matched by its title.");

        SearchHeadline.OnlyWithMatch(plain).ShouldBeEmpty();
        SearchHeadline.OnlyWithMatch(SearchHeadline.Segments($"a {Start}b{Stop}")).Count.ShouldBe(2);
    }
}
