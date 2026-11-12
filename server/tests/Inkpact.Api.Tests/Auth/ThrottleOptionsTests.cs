using Inkpact.Api.Auth;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Inkpact.Api.Tests.Auth;

public class ThrottleOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => $"Auth:Throttle:{v.Key}", v => (string?)v.Value))
            .Build();

    [Fact]
    public void Nothing_configured_gives_the_defaults()
    {
        var options = ThrottleOptions.From(Config());

        options.PerAddressPerMinute.ShouldBe(10);
        options.FailuresBeforeLock.ShouldBe(5);
        options.FirstLock.ShouldBe(TimeSpan.FromMinutes(1));
        options.MaxLock.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Configured_values_are_taken()
    {
        var options = ThrottleOptions.From(Config(
            ("PerAddressPerMinute", "30"), ("FailuresBeforeLock", "3"), ("FirstLockSeconds", "10"), ("MaxLockSeconds", "120")));

        options.PerAddressPerMinute.ShouldBe(30);
        options.FailuresBeforeLock.ShouldBe(3);
        options.FirstLock.ShouldBe(TimeSpan.FromSeconds(10));
        options.MaxLock.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Theory]
    [InlineData("PerAddressPerMinute", "0")]
    [InlineData("PerAddressPerMinute", "ten")]
    [InlineData("FailuresBeforeLock", "-1")]
    [InlineData("FirstLockSeconds", "1.5")]
    [InlineData("MaxLockSeconds", "0")]
    public void A_value_that_is_no_whole_number_of_one_or_more_is_refused_by_name(string key, string value)
    {
        Should.Throw<InvalidOperationException>(() => ThrottleOptions.From(Config((key, value))))
            .Message.ShouldContain($"Auth:Throttle:{key}");
    }

    [Fact]
    public void A_longest_lock_shorter_than_the_first_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => ThrottleOptions.From(Config(("FirstLockSeconds", "60"), ("MaxLockSeconds", "30"))))
            .Message.ShouldContain("MaxLockSeconds");
    }
}
