using Inkpact.Api.Search;

namespace Inkpact.Api.Tests.Search;

public class SearchWordsTests
{
    [Theory]
    [InlineData("weekly meeting", new[] { "weekly", "meeting" })]
    [InlineData("  weekly   meeting  ", new[] { "weekly", "meeting" })]
    [InlineData("Café CAFÉ", new[] { "Café", "CAFÉ" })]
    [InlineData("e-mail", new[] { "e", "mail" })]
    [InlineData("don't", new[] { "don", "t" })]
    [InlineData("snake_case", new[] { "snake", "case" })]
    [InlineData("2026 plan b2", new[] { "2026", "plan", "b2" })]
    [InlineData("日本語 テスト", new[] { "日本語", "テスト" })]
    public void A_text_is_split_into_runs_of_letters_and_digits(string text, string[] words) =>
        SearchWords.Split(text).ShouldBe(words);

    [Fact]
    public void An_accent_typed_as_a_letter_and_a_combining_mark_is_one_letter() =>
        SearchWords.Split("café").ShouldBe(["café"]);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&|!():*\\<>'\"")]
    [InlineData("😀 😀")]
    [InlineData("--- ...")]
    public void A_text_without_words_has_no_query(string text)
    {
        SearchWords.Split(text).ShouldBeEmpty();
        SearchWords.ToTsQuery(text).ShouldBeNull();
    }

    [Fact]
    public void Nothing_typed_is_no_query() => SearchWords.ToTsQuery(null).ShouldBeNull();

    [Theory]
    [InlineData("a & b | !c", "a:* & b:* & c:*")]
    [InlineData("foo:* <-> bar", "foo:* & bar:*")]
    [InlineData("(x) 'y' \\z", "x:* & y:* & z:*")]
    public void Query_syntax_in_the_text_never_reaches_the_query(string text, string query) =>
        SearchWords.ToTsQuery(text).ShouldBe(query);

    [Fact]
    public void Each_word_is_a_prefix_and_all_of_them_must_match() =>
        SearchWords.ToTsQuery("vergad week").ShouldBe("vergad:* & week:*");

    [Fact]
    public void Only_the_first_eight_words_count()
    {
        var words = SearchWords.Split("one two three four five six seven eight nine ten");

        words.Count.ShouldBe(SearchWords.MaxWords);
        words[^1].ShouldBe("eight");
    }

    [Fact]
    public void A_word_is_cut_at_its_longest()
    {
        var word = SearchWords.Split(new string('a', 100)).Single();

        word.Length.ShouldBe(SearchWords.MaxWordLength);
    }

    [Fact]
    public void A_word_is_not_cut_in_the_middle_of_a_character_outside_the_basic_plane()
    {
        // 𠀀 is one letter of two UTF-16 units.
        var word = SearchWords.Split(string.Concat(Enumerable.Repeat("\U00020000", 70))).Single();

        word.EnumerateRunes().Count().ShouldBe(SearchWords.MaxWordLength);
        word.Length.ShouldBe(SearchWords.MaxWordLength * 2);
    }

    [Fact]
    public void A_very_long_paste_is_only_looked_at_in_part()
    {
        var text = string.Concat(Enumerable.Repeat("word ", 100_000));

        SearchWords.Split(text).Count.ShouldBe(SearchWords.MaxWords);
    }

    [Fact]
    public void A_lone_surrogate_is_not_a_letter_and_does_not_throw() =>
        SearchWords.Split("abc\ud800def").ShouldBe(["abc", "def"]);
}
