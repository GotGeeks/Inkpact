using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Inkpact.Api.Tests.Permissions;

/// <summary>
/// The 404-or-403 rule of the role filter, on a small app with a stand-in permission service, so that every role
/// can be tried against every minimum without a database.
/// </summary>
public sealed class RoleFilterTests : IAsyncLifetime
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Workspace = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Page = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(_permissions);
                    services.AddInkpactPolicies();
                    services.AddScoped(_ => _permissions); // the stand-in replaces the real service
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/w/{workspaceId}/viewer", (string workspaceId) => "ok").RequireWorkspaceRole(WorkspaceRole.Viewer);
                        endpoints.MapGet("/w/{workspaceId}/editor", (string workspaceId) => "ok").RequireWorkspaceRole(WorkspaceRole.Editor);
                        endpoints.MapGet("/w/{workspaceId}/owner", (string workspaceId) => "ok").RequireWorkspaceRole(WorkspaceRole.Owner);
                        endpoints.MapGet("/p/{pageId}/editor", (string pageId) => "ok").RequirePageRole(WorkspaceRole.Editor);
                        endpoints.MapGet("/admin", () => "ok").RequireAuthorization(Policies.Admin);
                        endpoints.MapGet("/me", () => "ok").RequireAuthorization(Policies.SignedIn);
                    });
                }))
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> GetAsync(string path, bool signedIn = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (signedIn)
        {
            request.Headers.Add("X-Test-User", User.ToString());
        }

        return _client.SendAsync(request);
    }

    private void GiveRole(WorkspaceRole role)
    {
        _permissions.GetWorkspaceRoleAsync(User, Workspace, Arg.Any<CancellationToken>()).Returns(role);
        _permissions.GetPageRoleAsync(User, Page, Arg.Any<CancellationToken>()).Returns(role);
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer, "viewer", HttpStatusCode.OK)]
    [InlineData(WorkspaceRole.Viewer, "editor", HttpStatusCode.Forbidden)]
    [InlineData(WorkspaceRole.Viewer, "owner", HttpStatusCode.Forbidden)]
    [InlineData(WorkspaceRole.Editor, "viewer", HttpStatusCode.OK)]
    [InlineData(WorkspaceRole.Editor, "editor", HttpStatusCode.OK)]
    [InlineData(WorkspaceRole.Editor, "owner", HttpStatusCode.Forbidden)]
    [InlineData(WorkspaceRole.Owner, "viewer", HttpStatusCode.OK)]
    [InlineData(WorkspaceRole.Owner, "editor", HttpStatusCode.OK)]
    [InlineData(WorkspaceRole.Owner, "owner", HttpStatusCode.OK)]
    [InlineData(WorkspaceRole.None, "viewer", HttpStatusCode.NotFound)]
    [InlineData(WorkspaceRole.None, "editor", HttpStatusCode.NotFound)]
    [InlineData(WorkspaceRole.None, "owner", HttpStatusCode.NotFound)]
    public async Task Every_role_against_every_minimum(WorkspaceRole role, string minimum, HttpStatusCode expected)
    {
        GiveRole(role);

        var response = await GetAsync($"/w/{Workspace}/{minimum}");

        response.StatusCode.ShouldBe(expected);
        if (expected != HttpStatusCode.OK)
        {
            (await response.Content.ReadFromJsonAsync<ApiError>())!.Code.ShouldBe(expected == HttpStatusCode.Forbidden ? "forbidden" : "not_found");
        }
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer, HttpStatusCode.Forbidden)]
    [InlineData(WorkspaceRole.Editor, HttpStatusCode.OK)]
    [InlineData(WorkspaceRole.Owner, HttpStatusCode.OK)]
    [InlineData(WorkspaceRole.None, HttpStatusCode.NotFound)]
    public async Task A_page_endpoint_uses_the_role_on_the_page(WorkspaceRole role, HttpStatusCode expected)
    {
        GiveRole(role);

        (await GetAsync($"/p/{Page}/editor")).StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("33333333-3333-3333-3333-33333333333")]
    public async Task A_malformed_id_looks_like_any_other_missing_thing(string id)
    {
        GiveRole(WorkspaceRole.Owner);

        var response = await GetAsync($"/w/{id}/viewer");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<ApiError>())!.Code.ShouldBe("not_found");
        await _permissions.DidNotReceiveWithAnyArgs().GetWorkspaceRoleAsync(default, default, default);
    }

    [Fact]
    public async Task A_workspace_nobody_knows_gives_the_same_answer_as_one_that_is_not_yours()
    {
        _permissions.GetWorkspaceRoleAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(WorkspaceRole.None);

        var unknown = await GetAsync($"/w/{Guid.NewGuid()}/viewer");
        var notYours = await GetAsync($"/w/{Workspace}/viewer");

        unknown.StatusCode.ShouldBe(notYours.StatusCode);
        (await unknown.Content.ReadAsStringAsync()).ShouldBe(await notYours.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_callers_are_turned_away_before_the_role_is_looked_up()
    {
        GiveRole(WorkspaceRole.Owner);

        var response = await GetAsync($"/w/{Workspace}/viewer", signedIn: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await _permissions.DidNotReceiveWithAnyArgs().GetWorkspaceRoleAsync(default, default, default);
    }

    [Fact]
    public async Task The_admin_policy_follows_the_database_flag()
    {
        _permissions.IsAdminAsync(User, Arg.Any<CancellationToken>()).Returns(false);
        (await GetAsync("/admin")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        _permissions.IsAdminAsync(User, Arg.Any<CancellationToken>()).Returns(true);
        (await GetAsync("/admin")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GetAsync("/admin", signedIn: false)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_signed_in_policy_only_needs_a_user()
    {
        (await GetAsync("/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync("/me", signedIn: false)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>A user from a header, so the test does not need the real cookie and session machinery.</summary>
    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var id))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
