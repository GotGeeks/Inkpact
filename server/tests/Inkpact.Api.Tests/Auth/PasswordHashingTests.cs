using Geralt;
using Inkpact.Api.Auth;

namespace Inkpact.Api.Tests.Auth;

public class PasswordHashingTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        var hash = _hasher.Hash("correct horse battery");

        _hasher.Verify(hash, "correct horse battery").ShouldBeTrue();
    }

    [Theory]
    [InlineData("correct horse batterY")]
    [InlineData("correct horse battery ")]
    [InlineData("")]
    public void Another_password_does_not_verify(string attempt)
    {
        var hash = _hasher.Hash("correct horse battery");

        _hasher.Verify(hash, attempt).ShouldBeFalse();
    }

    [Fact]
    public void The_hash_is_argon2id_with_the_owasp_parameters()
    {
        var hash = _hasher.Hash("correct horse battery");

        hash.ShouldStartWith("$argon2id$v=19$m=47104,t=1,p=1$");
        hash.ShouldNotContain("correct");
        hash.ShouldNotContain("\0");
    }

    [Fact]
    public void The_same_password_gets_a_different_hash_every_time()
    {
        _hasher.Hash("correct horse battery").ShouldNotBe(_hasher.Hash("correct horse battery"));
    }

    [Fact]
    public void Passwords_with_any_characters_work()
    {
        var password = "pässwörd-密码-🔑-with spaces";

        var hash = _hasher.Hash(password);

        _hasher.Verify(hash, password).ShouldBeTrue();
        _hasher.Verify(hash, "passwörd-密码-🔑-with spaces").ShouldBeFalse();
    }

    [Fact]
    public void A_current_hash_does_not_need_a_rehash()
    {
        _hasher.NeedsRehash(_hasher.Hash("correct horse battery")).ShouldBeFalse();
    }

    [Fact]
    public void A_hash_made_with_weaker_parameters_needs_a_rehash()
    {
        Span<char> weak = stackalloc char[Argon2id.HashSize];
        Argon2id.ComputeHash(weak, "correct horse battery"u8, iterations: 1, memorySize: Argon2id.MinMemorySize);
        var hash = weak[..weak.IndexOf('\0')].ToString();

        _hasher.Verify(hash, "correct horse battery").ShouldBeTrue();
        _hasher.NeedsRehash(hash).ShouldBeTrue();
    }

    [Fact]
    public void Verifying_the_dummy_does_not_throw_and_takes_real_time()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _hasher.VerifyDummy("anything");

        // Argon2id with 46 MiB is never instant; a skipped hash would be well under a millisecond.
        watch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(2));
    }

    [Fact]
    public async Task Many_hashes_at_once_all_finish_correctly()
    {
        var hashes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => _hasher.Hash($"password number {i:D4}!"))));

        for (var i = 0; i < hashes.Length; i++)
        {
            _hasher.Verify(hashes[i], $"password number {i:D4}!").ShouldBeTrue();
        }
    }
}

public class PasswordPolicyTests
{
    [Fact]
    public void Twelve_characters_is_enough()
    {
        PasswordPolicy.Validate("abcdefghijkl", "a@example.com").ShouldBeNull();
    }

    [Fact]
    public void Eleven_characters_is_too_short()
    {
        PasswordPolicy.Validate("abcdefghijk", "a@example.com").ShouldBe("password_too_short");
    }

    [Fact]
    public void There_are_no_composition_rules()
    {
        PasswordPolicy.Validate("aaaaaaaaaaaa", "a@example.com").ShouldBeNull();
        PasswordPolicy.Validate("            ", "a@example.com").ShouldBeNull();
    }

    [Fact]
    public void A_hundred_and_twenty_eight_characters_is_the_limit()
    {
        PasswordPolicy.Validate(new string('a', 128), "a@example.com").ShouldBeNull();
        PasswordPolicy.Validate(new string('a', 129), "a@example.com").ShouldBe("password_too_long");
    }

    [Fact]
    public void Length_counts_characters_not_bytes()
    {
        // Twelve Chinese characters are 36 bytes in UTF-8 but twelve characters to a person.
        PasswordPolicy.Validate("密码密码密码密码密码密码", "a@example.com").ShouldBeNull();
        // Eleven emoji, one of them a four-part family, are eleven characters, not more.
        PasswordPolicy.Validate("🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑👨‍👩‍👧‍👦", "a@example.com").ShouldBe("password_too_short");
    }

    [Fact]
    public void The_email_is_not_a_password()
    {
        PasswordPolicy.Validate("jos.nienhuis@example.com", "Jos.Nienhuis@example.com").ShouldBe("password_same_as_email");
    }
}
