using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Inkpact.Api.Pages;

namespace Inkpact.Api.Tests.Pages;

public class FractionalIndexTests
{
    private sealed record BetweenCase(string? A, string? B, string Expected);

    private sealed record ManyCase(string? A, string? B, int N, string[] Expected);

    private sealed record ErrorCase(string? A, string? B);

    private sealed record Vectors(string Source, BetweenCase[] Between, ManyCase[] Many, ErrorCase[] Errors);

    private static readonly Lazy<Vectors> Original = new(() =>
        JsonSerializer.Deserialize<Vectors>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fractional-index-vectors.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

    // ---- against the original package ------------------------------------------------------------------------------

    [Fact]
    public void The_vectors_are_the_ones_made_by_the_original_package()
    {
        Original.Value.Source.ShouldContain("fractional-indexing 4.0.0");
        Original.Value.Between.Length.ShouldBeGreaterThan(2000);
        Original.Value.Many.Length.ShouldBeGreaterThan(50);
        Original.Value.Errors.Length.ShouldBeGreaterThan(5);
    }

    [Fact]
    public void Every_key_between_two_others_is_the_key_the_original_gives()
    {
        var wrong = Original.Value.Between
            .Select(c => (Case: c, Actual: FractionalIndex.KeyBetween(c.A, c.B)))
            .Where(r => r.Actual != r.Case.Expected)
            .Select(r => $"({r.Case.A ?? "null"}, {r.Case.B ?? "null"}): expected {r.Case.Expected}, got {r.Actual}")
            .Take(10)
            .ToList();

        wrong.ShouldBeEmpty();
    }

    [Fact]
    public void Every_run_of_keys_is_the_run_the_original_gives()
    {
        foreach (var c in Original.Value.Many)
        {
            FractionalIndex.KeysBetween(c.A, c.B, c.N)
                .ShouldBe(c.Expected, $"{c.N} keys between {c.A ?? "null"} and {c.B ?? "null"}");
        }
    }

    [Fact]
    public void Everything_the_original_refuses_is_refused()
    {
        foreach (var c in Original.Value.Errors)
        {
            Should.Throw<Exception>(() => FractionalIndex.KeyBetween(c.A, c.B), $"({c.A ?? "null"}, {c.B ?? "null"})");
        }
    }

    // ---- known keys --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_key_is_a0()
    {
        FractionalIndex.KeyBetween(null, null).ShouldBe("a0");
    }

    [Theory]
    [InlineData("a0", null, "a1")]
    [InlineData("a0", "a1", "a0V")]
    [InlineData("a0", "a0V", "a0G")]
    [InlineData(null, "a0", "Zz")]
    [InlineData("az", null, "b00")]
    [InlineData("Zz", "a0", "ZzV")]
    public void Some_keys_by_hand(string? a, string? b, string expected)
    {
        FractionalIndex.KeyBetween(a, b).ShouldBe(expected);
    }

    [Fact]
    public void Bounds_the_wrong_way_round_are_put_right()
    {
        FractionalIndex.KeyBetween("a1", "a0").ShouldBe(FractionalIndex.KeyBetween("a0", "a1"));
    }

    [Fact]
    public void No_keys_is_an_empty_list_and_a_negative_count_is_refused()
    {
        FractionalIndex.KeysBetween("a0", "a1", 0).ShouldBeEmpty();
        Should.Throw<ArgumentOutOfRangeException>(() => FractionalIndex.KeysBetween("a0", "a1", -1));
    }

    [Theory]
    [InlineData("a0", "a0")]
    [InlineData("a1", "a1")]
    public void Equal_bounds_are_refused(string a, string b)
    {
        Should.Throw<ArgumentException>(() => FractionalIndex.KeyBetween(a, b));
    }

    // ---- what is a valid key -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a0")]
    [InlineData("a1V")]
    [InlineData("Zz")]
    [InlineData("b00")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void These_are_keys(string key)
    {
        FractionalIndex.IsValid(key).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")] // too short for its own head
    [InlineData("a00")] // a fraction ends in a zero
    [InlineData("!0")] // no such head
    [InlineData("a0 ")] // not a digit
    [InlineData("a0-")]
    [InlineData("é0")]
    [InlineData("A00000000000000000000000000")] // nothing fits before the smallest integer
    [InlineData("b0")] // the integer part of b is three characters long
    public void These_are_not_keys(string? key)
    {
        FractionalIndex.IsValid(key).ShouldBeFalse();
    }

    // ---- properties --------------------------------------------------------------------------------------------------

    /// <summary>Builds a list by inserting at the places the numbers point to, as a person dragging pages would.</summary>
    private static List<string> BuildList(IEnumerable<int> places)
    {
        var keys = new List<string>();
        foreach (var place in places)
        {
            var position = (int)((uint)place % (uint)(keys.Count + 1));
            keys.Insert(position, FractionalIndex.KeyBetween(
                position == 0 ? null : keys[position - 1],
                position == keys.Count ? null : keys[position]));
        }

        return keys;
    }

    private static bool StrictlyIncreasing(IReadOnlyList<string> keys) =>
        keys.Zip(keys.Skip(1)).All(pair => string.CompareOrdinal(pair.First, pair.Second) < 0);

    [Property(MaxTest = 300)]
    public bool Inserting_anywhere_keeps_every_key_valid_distinct_and_in_order(int[] places)
    {
        var keys = BuildList(places);

        return StrictlyIncreasing(keys) && keys.All(FractionalIndex.IsValid);
    }

    [Property(MaxTest = 300)]
    public bool A_key_between_two_keys_lies_strictly_between_them(int[] places, int left)
    {
        var keys = BuildList(places.Append(0).Append(1));
        var i = (int)((uint)left % (uint)(keys.Count - 1));

        var between = FractionalIndex.KeyBetween(keys[i], keys[i + 1]);

        return string.CompareOrdinal(keys[i], between) < 0 && string.CompareOrdinal(between, keys[i + 1]) < 0;
    }

    [Property(MaxTest = 200)]
    public bool Keys_before_the_first_and_after_the_last_sort_outside_the_list(int[] places)
    {
        var keys = BuildList(places.Append(0));

        var before = FractionalIndex.KeyBetween(null, keys[0]);
        var after = FractionalIndex.KeyBetween(keys[^1], null);

        return string.CompareOrdinal(before, keys[0]) < 0 && string.CompareOrdinal(keys[^1], after) < 0;
    }

    [Property(MaxTest = 200)]
    public bool A_run_of_keys_is_distinct_sorted_and_between_the_bounds(int[] places, byte count)
    {
        var keys = BuildList(places.Append(0).Append(1));
        var a = keys[0];
        var b = keys[^1];
        var n = count % 40;

        var run = FractionalIndex.KeysBetween(a, b, n);

        return run.Count == n
            && StrictlyIncreasing(run)
            && run.All(k => string.CompareOrdinal(a, k) < 0 && string.CompareOrdinal(k, b) < 0);
    }

    [Property(MaxTest = 100)]
    public bool A_run_with_an_open_end_is_distinct_sorted_and_on_the_right_side(int[] places, byte count, bool after)
    {
        var keys = BuildList(places.Append(0));
        var n = count % 40;

        var run = after ? FractionalIndex.KeysBetween(keys[^1], null, n) : FractionalIndex.KeysBetween(null, keys[0], n);

        return run.Count == n
            && StrictlyIncreasing(run)
            && run.All(k => after ? string.CompareOrdinal(keys[^1], k) < 0 : string.CompareOrdinal(k, keys[0]) < 0);
    }

    // ---- the keys stay short -----------------------------------------------------------------------------------------

    [Fact]
    public void Adding_at_the_end_a_thousand_times_keeps_keys_short()
    {
        string? last = null;
        for (var i = 0; i < 1000; i++)
        {
            last = FractionalIndex.KeyBetween(last, null);
        }

        // The integer part grows by a character per 62 keys at most, so a thousand keys fit in a handful.
        last!.Length.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public void Adding_at_the_start_a_thousand_times_keeps_keys_short()
    {
        string? first = null;
        for (var i = 0; i < 1000; i++)
        {
            first = FractionalIndex.KeyBetween(null, first);
        }

        first!.Length.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public void Always_inserting_right_after_the_same_key_grows_the_key_by_one_character_per_few_inserts()
    {
        var a = FractionalIndex.KeyBetween(null, null);
        var b = FractionalIndex.KeyBetween(a, null);
        for (var i = 0; i < 200; i++)
        {
            b = FractionalIndex.KeyBetween(a, b);
        }

        // The worst case for this scheme, and still slow growth: about one character for every five or six inserts.
        b.Length.ShouldBeLessThan(60);
    }

    [Fact]
    public void A_long_random_session_does_not_make_keys_balloon()
    {
        var random = new Random(20261025);
        var keys = new List<string>();
        for (var step = 0; step < 3000; step++)
        {
            var position = random.Next(keys.Count + 1);
            keys.Insert(position, FractionalIndex.KeyBetween(
                position == 0 ? null : keys[position - 1],
                position == keys.Count ? null : keys[position]));
        }

        StrictlyIncreasing(keys).ShouldBeTrue();
        keys.Max(k => k.Length).ShouldBeLessThan(20);
        keys.Average(k => k.Length).ShouldBeLessThan(5);
    }
}
