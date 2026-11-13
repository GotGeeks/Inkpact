using Inkpact.Api.Versions;

namespace Inkpact.Api.Tests.Versions;

/// <summary>The versions the app is stamped with and release tags name, and their order (semver precedence).</summary>
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.1.0", 0, 1, 0, "")]
    [InlineData("v0.1.0", 0, 1, 0, "")]
    [InlineData("12.34.56", 12, 34, 56, "")]
    [InlineData("0.1.0+edge.abc1234", 0, 1, 0, "")]
    [InlineData("1.0.0-rc.1", 1, 0, 0, "rc.1")]
    [InlineData("1.0.0-alpha-2.x+build.5", 1, 0, 0, "alpha-2.x")]
    [InlineData("0.0.0-dev", 0, 0, 0, "dev")]
    public void Reads_a_version(string text, int major, int minor, int patch, string pre)
    {
        ReleaseVersion.TryParse(text, out var version).ShouldBeTrue();
        version.ShouldBe(new ReleaseVersion(major, minor, patch, pre));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-a..b")]
    [InlineData(" 1.0.0")]
    [InlineData("V1.0.0")]
    [InlineData("1.0.0\n")]
    [InlineData("9999999999.0.0")]
    [InlineData("latest")]
    // Digits of other scripts: \d would take them, and int.Parse would then throw.
    [InlineData("v1.0.١")]
    [InlineData("१.0.0")]
    [InlineData("1.０.0")]
    public void Refuses_what_is_not_a_version(string? text)
    {
        ReleaseVersion.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void Refuses_a_very_long_text()
    {
        ReleaseVersion.TryParse("1.0.0-" + new string('a', 200), out _).ShouldBeFalse();
    }

    [Fact]
    public void Orders_by_semver_precedence()
    {
        // From semver.org's example, and the build metadata that does not count.
        string[] ordered =
        [
            "0.0.0-dev", "0.9.9", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "1.10.0", "2.0.0",
        ];
        var versions = ordered.Select(Parse).ToList();
        for (var i = 0; i < versions.Count - 1; i++)
        {
            (versions[i] < versions[i + 1]).ShouldBeTrue($"{ordered[i]} < {ordered[i + 1]}");
            (versions[i + 1] > versions[i]).ShouldBeTrue($"{ordered[i + 1]} > {ordered[i]}");
        }

        // Sorted from the wrong way round, through IComparable.
        versions.AsEnumerable().Reverse().Order().ShouldBe(versions);
    }

    [Fact]
    public void An_edge_build_ranks_the_same_as_the_release_it_is_built_on()
    {
        var edge = Parse("0.1.0+edge.abc1234");
        edge.CompareTo(Parse("v0.1.0")).ShouldBe(0);
        (Parse("0.2.0") > edge).ShouldBeTrue();
        edge.ToString().ShouldBe("0.1.0");
    }

    [Theory]
    [InlineData("0.0.0-dev", true)]
    [InlineData("0.0.0-dev+x", true)]
    // An edge build before the first release: stamped, so it asks, and hears of 0.1.0.
    [InlineData("0.0.0+edge.abc1234", false)]
    [InlineData("0.0.0", false)]
    [InlineData("0.0.0-rc.1", false)]
    [InlineData("0.0.1", false)]
    [InlineData("0.1.0+edge.abc", false)]
    public void Knows_a_build_that_was_not_stamped(string text, bool development)
    {
        Parse(text).IsDevelopment.ShouldBe(development);
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+edge.abc1234", "0.1.0+edge.abc1234")]
    [InlineData(null, AppVersion.Development)]
    [InlineData("not a version", AppVersion.Development)]
    public void The_app_says_its_stamped_version_or_that_it_is_a_development_build(string? stamped, string said)
    {
        var version = AppVersion.Of(stamped);
        version.Text.ShouldBe(said);
        version.Release.IsDevelopment.ShouldBe(said == AppVersion.Development);
    }

    [Fact]
    public void An_unstamped_assembly_is_a_development_build()
    {
        // The test run builds the app without INKPACT_VERSION: server/Directory.Build.props says 0.0.0-dev.
        AppVersion.FromAssembly(typeof(AppVersion).Assembly).Text.ShouldBe(AppVersion.Development);
    }

    private static ReleaseVersion Parse(string text)
    {
        ReleaseVersion.TryParse(text, out var version).ShouldBeTrue(text);
        return version;
    }
}
