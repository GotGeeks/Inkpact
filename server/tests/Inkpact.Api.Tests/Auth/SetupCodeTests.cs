using Inkpact.Api.Auth;

namespace Inkpact.Api.Tests.Auth;

public class SetupCodeTests
{
    [Fact]
    public void A_generated_code_has_eight_characters_from_the_unambiguous_alphabet()
    {
        var code = SetupCode.FromConfiguration(null).Value;

        code.Length.ShouldBe(8);
        code.ShouldAllBe(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c));
    }

    [Fact]
    public void Generated_codes_differ()
    {
        var codes = Enumerable.Range(0, 20).Select(_ => SetupCode.FromConfiguration("").Value).Distinct();

        codes.Count().ShouldBeGreaterThan(15);
    }

    [Fact]
    public void A_configured_code_is_used_as_given_but_trimmed_and_upper_cased()
    {
        SetupCode.FromConfiguration("  my-secret ").Value.ShouldBe("MY-SECRET");
    }

    [Theory]
    [InlineData("ABCD2345", true)]
    [InlineData("abcd2345", true)]
    [InlineData(" abcd2345 ", true)]
    [InlineData("ABCD2346", false)]
    [InlineData("ABCD234", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Matching_ignores_case_and_surrounding_spaces(string? attempt, bool expected)
    {
        SetupCode.Create("ABCD2345").Matches(attempt).ShouldBe(expected);
    }
}

public class EmailNormalizationTests
{
    [Theory]
    [InlineData("jos@example.com", "jos@example.com")]
    [InlineData("  Jos@Example.COM ", "jos@example.com")]
    [InlineData("first.last+tag@sub.example.org", "first.last+tag@sub.example.org")]
    public void Valid_addresses_are_trimmed_and_lower_cased(string input, string expected)
    {
        AuthEndpoints.NormalizeEmail(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jos")]
    [InlineData("jos@")]
    [InlineData("@example.com")]
    [InlineData("jos@localhost")]
    [InlineData("Jos Nienhuis <jos@example.com>")]
    [InlineData("jos@example.com, other@example.com")]
    [InlineData("jos@exa mple.com")]
    public void Invalid_addresses_are_refused(string? input)
    {
        AuthEndpoints.NormalizeEmail(input).ShouldBeNull();
    }

    [Fact]
    public void A_very_long_address_is_refused()
    {
        AuthEndpoints.NormalizeEmail(new string('a', 320) + "@example.com").ShouldBeNull();
    }
}
