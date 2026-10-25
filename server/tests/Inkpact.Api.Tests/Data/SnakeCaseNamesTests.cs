using Inkpact.Data;

namespace Inkpact.Api.Tests.Data;

public class SnakeCaseNamesTests
{
    [Theory]
    [InlineData("Id", "id")]
    [InlineData("WorkspaceId", "workspace_id")]
    [InlineData("ExternalSourceType", "external_source_type")]
    [InlineData("OidcSubject", "oidc_subject")]
    [InlineData("already_snake", "already_snake")]
    public void Converts_names(string name, string expected) =>
        SnakeCaseNames.ToSnakeCase(name).ShouldBe(expected);
}
