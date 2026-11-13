using System.Text.RegularExpressions;
using System.Xml.Linq;
using Inkpact.Api.Health;

namespace Inkpact.Api.Tests.Deploy;

/// <summary>
/// The Unraid template (run B.8, <c>deploy/unraid/inkpact.xml</c>) against the compose file and the image: the same
/// image, folder, port, hardening and settings, so that an Unraid install runs as the tested stack does. Unraid itself is
/// in the test pass.
/// </summary>
public partial class UnraidTemplateTests
{
    private static readonly string Root = FindRoot();
    private static readonly XElement Template = XDocument.Load(Path.Combine(Root, "deploy", "unraid", "inkpact.xml")).Root!;
    private static readonly string Compose = File.ReadAllText(Path.Combine(Root, "deploy", "docker-compose.yml"));

    private static IEnumerable<XElement> Configs(string type) =>
        Template.Elements("Config").Where(c => (string?)c.Attribute("Type") == type);

    private static string Text(string element) => Template.Element(element)?.Value.Trim() ?? "";

    [Fact]
    public void Runs_the_image_the_compose_file_runs_from_this_repository()
    {
        // edge until the first release, then latest: both move together (run B.8, "After the run").
        var compose = ComposeImage().Match(Compose);
        compose.Success.ShouldBeTrue("the compose file names its default image");

        Template.Name.LocalName.ShouldBe("Container");
        Text("Repository").ShouldBe(compose.Groups["image"].Value);
        Text("Repository").ShouldStartWith("ghcr.io/gotgeeks/inkpact:");
        Text("Name").ShouldBe("Inkpact");
        Text("TemplateURL").ShouldBe("https://raw.githubusercontent.com/GotGeeks/Inkpact/main/deploy/unraid/inkpact.xml");
        Text("Privileged").ShouldBe("false");
    }

    [Fact]
    public void Its_icon_is_a_file_of_this_repository()
    {
        const string prefix = "https://raw.githubusercontent.com/GotGeeks/Inkpact/main/";
        var icon = Text("Icon");

        icon.ShouldStartWith(prefix);
        File.Exists(Path.Combine(Root, icon[prefix.Length..])).ShouldBeTrue(icon);
    }

    [Fact]
    public void Mounts_data_and_publishes_the_port_the_image_listens_on()
    {
        var path = Configs("Path").ShouldHaveSingleItem();
        ((string?)path.Attribute("Target")).ShouldBe("/data");
        path.Value.ShouldBe("/mnt/user/appdata/inkpact");

        var port = Configs("Port").ShouldHaveSingleItem();
        ((string?)port.Attribute("Target")).ShouldBe("8080");
        File.ReadAllText(Path.Combine(Root, "Dockerfile")).ShouldContain("EXPOSE 8080");
        Text("WebUI").ShouldBe("http://[IP]:[PORT:8080]/");
    }

    [Fact]
    public void Hardens_the_container_as_the_compose_file_does()
    {
        var extra = Text("ExtraParams").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        extra.ShouldBe(["--read-only", "--tmpfs", "/tmp", "--cap-drop=ALL", "--security-opt=no-new-privileges:true"], ignoreOrder: true);
        Compose.ShouldContain("read_only: true");
        Compose.ShouldContain("- /tmp");
        Compose.ShouldContain("- ALL");
        Compose.ShouldContain("- no-new-privileges:true");
    }

    [Fact]
    public void Offers_the_settings_the_compose_file_names_and_no_others()
    {
        // Every setting in the compose file's app service, set or commented out: NAME__PART: value.
        var compose = ComposeSetting().Matches(Compose).Select(m => m.Groups["name"].Value).ToHashSet();
        var template = Configs("Variable").Select(c => (string)c.Attribute("Target")!).ToHashSet();

        template.ShouldBe(compose, ignoreOrder: true);
    }

    [Fact]
    public void Hides_the_secrets_and_only_them()
    {
        Template.Elements("Config")
            .Where(c => (string?)c.Attribute("Mask") == "true")
            .Select(c => (string)c.Attribute("Target")!)
            .ShouldBe(["ConnectionStrings__Inkpact", "Auth__SetupCode"], ignoreOrder: true);
    }

    [Fact]
    public void Shows_each_setting_s_default_as_its_value()
    {
        foreach (var config in Template.Elements("Config"))
        {
            config.Value.ShouldBe((string?)config.Attribute("Default"), (string?)config.Attribute("Name"));
        }
    }

    [Fact]
    public void Says_what_to_do_before_the_first_start()
    {
        var requires = Text("Requires");

        requires.ShouldContain($"chown -R {DataDirectories.ImageUserId}:{DataDirectories.ImageUserId} /mnt/user/appdata/inkpact");
        requires.ShouldContain("PostgreSQL 18");
        requires.ShouldContain("CREATE DATABASE inkpact OWNER inkpact");
    }

    [GeneratedRegex(@"image: \$\{INKPACT_IMAGE:-(?<image>[^}]+)\}")]
    private static partial Regex ComposeImage();

    [GeneratedRegex(@"^\s*#?\s*(?<name>[A-Z][A-Za-z0-9]*(?:__[A-Za-z0-9]+)+):", RegexOptions.Multiline)]
    private static partial Regex ComposeSetting();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "deploy", "unraid", "inkpact.xml")))
            {
                return directory.FullName;
            }
        }

        throw new FileNotFoundException("deploy/unraid/inkpact.xml was not found above the test assembly");
    }
}
