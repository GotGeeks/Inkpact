using Inkpact.Api.Search;

namespace Inkpact.Api.Tests.Search;

public class SearchHeadlineTests
{
    private static string Show(IEnumerable<SearchSegment> segments) =>
        string.Concat(segments.Select(s => s.Match ? $"[{s.Text}]" : s.Text));

    private static string Mark(string text, params string[] words) => Show(SearchHeadline.Mark(text, words, atStart: true, atEnd: true));

    [Fact]
    public void Every_word_that_starts_with_a_search_word_is_marked_whole() =>
        Mark("Meetings and a meeting, met", "meet").ShouldBe("[Meetings] and a [meeting], met");

    [Fact]
    public void Case_and_accents_do_not_matter_and_the_text_keeps_its_own() =>
        Mark("Het CAFÉ en het café", "cafe").ShouldBe("Het [CAFÉ] en het [café]");

    [Fact]
    public void An_accent_in_the_search_word_does_not_matter_either() =>
        Mark("Het cafe", "Café").ShouldBe("Het [cafe]");

    [Fact]
    public void An_accent_written_as_a_letter_and_a_combining_mark_is_marked_with_its_mark() =>
        Mark("een café hier", "cafe").ShouldBe("een [café] hier");

    [Fact]
    public void A_word_joined_to_the_one_before_by_punctuation_is_found() =>
        Mark("see report.pdf or user@example.com", "pdf", "example").ShouldBe("see report.[pdf] or user@[example].com");

    [Fact]
    public void Only_the_start_of_a_word_counts() =>
        Mark("decaf coffee", "caf").ShouldBe("decaf coffee");

    [Fact]
    public void Several_search_words_are_each_marked() =>
        Mark("the weekly plan for the meeting", "meet", "week").ShouldBe("the [weekly] plan for the [meeting]");

    [Fact]
    public void Two_matches_next_to_each_other_are_one_piece() =>
        SearchHeadline.Mark("ab-ac", ["a"], true, true).ShouldBe([new SearchSegment("ab", true), new SearchSegment("-", false), new SearchSegment("ac", true)]);

    [Fact]
    public void A_letter_outside_the_basic_plane_is_kept_whole() =>
        Mark("𠀀𠀀 here", "𠀀").ShouldBe("[𠀀𠀀] here");

    [Fact]
    public void Control_characters_in_the_text_are_text_and_never_marks() =>
        Mark("a \u0001fake\u0002 mark and a match", "match").ShouldBe("a \u0001fake\u0002 mark and a [match]");

    [Fact]
    public void A_piece_from_the_middle_of_a_text_loses_the_partial_words_at_its_ends() =>
        Show(SearchHeadline.Mark("ngs before the needle and after th", ["needle"], atStart: false, atEnd: false))
            .ShouldBe("before the [needle] and after");

    [Fact]
    public void Nothing_to_mark_is_no_segments_and_a_snippet_without_a_match_is_left_out()
    {
        SearchHeadline.Mark(null, ["a"], true, true).ShouldBeEmpty();
        SearchHeadline.Mark("", ["a"], true, true).ShouldBeEmpty();
        SearchHeadline.Mark("text", [], true, true).ShouldBeEmpty();
        SearchHeadline.Mark("nothing here", ["zebra"], true, true).ShouldBe([new SearchSegment("nothing here", false)]);
        SearchHeadline.OnlyWithMatch(SearchHeadline.Mark("nothing here", ["zebra"], true, true)).ShouldBeEmpty();
    }

    // ---- the regular expression Postgres finds the first match with -----------------------------------------------------

    [Fact]
    public void The_pattern_finds_a_word_start_with_every_case_and_accent_of_each_letter()
    {
        var pattern = SearchHeadline.Pattern(["Café", "b2"]);

        pattern.ShouldStartWith(@"\m(?:");
        pattern.ShouldContain("é");
        pattern.ShouldContain("É");
        pattern.ShouldContain("C");
        pattern.ShouldContain("2");
        pattern.Split('|').Length.ShouldBe(2);
    }

    [Fact]
    public void The_pattern_of_a_letter_outside_the_latin_table_has_both_cases() =>
        SearchHeadline.Pattern(["ж"]).ShouldBe(@"\m(?:[жЖ])");

    [Fact]
    public void The_pattern_holds_nothing_but_letters_digits_and_its_own_syntax()
    {
        var pattern = SearchHeadline.Pattern(SearchWords.Split("a&b|c(d) e*f \\g [h]"));

        pattern[@"\m(?:".Length..^1].Replace("[", "").Replace("]", "").Replace("|", "").ShouldAllBe(c => char.IsLetterOrDigit(c));
    }
}
