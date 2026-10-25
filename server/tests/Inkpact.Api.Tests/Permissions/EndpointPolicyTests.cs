using Inkpact.Api.Permissions;
using Inkpact.Api.Tests.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Inkpact.Api.Tests.Permissions;

/// <summary>
/// The guard against forgetting to say who may call an endpoint. Every endpoint of the real app must either name one
/// of the policies, or be on the list below that a person has looked at and agreed is public. A new endpoint that
/// is neither makes this test fail, and the message names it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EndpointPolicyTests(PostgresFixture postgres)
{
    /// <summary>Method and route pattern of every endpoint that anyone may call without signing in. Add to it only with a reason.</summary>
    internal static readonly IReadOnlyList<string> ReviewedAnonymousEndpoints =
    [
        "GET /api/health", // the container health check
        "GET /api/auth/state", // says whether to show setup or login, and hands out the CSRF token
        "POST /api/auth/setup", // first-run account, protected by the setup code and closed afterwards
        "POST /api/auth/login", // throttled per address and per email
        "ANY /api/{**path}", // JSON 404 for unknown API paths
        "GET,HEAD {*path:nonfile}", // the web app shell: no data in it
    ];

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return $"{(methods is { Count: > 0 } ? string.Join(",", methods) : "ANY")} {endpoint.RoutePattern.RawText}";
    }

    /// <summary>What is wrong with these endpoints, one line each. Empty when all is as it should be.</summary>
    internal static List<string> Problems(IEnumerable<Endpoint> endpoints, IReadOnlyCollection<string> allowedAnonymous)
    {
        var problems = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var name = Describe(endpoint);
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();

            if (anonymous)
            {
                if (!allowedAnonymous.Contains(name))
                {
                    problems.Add($"{name} is anonymous but not on the reviewed list");
                }

                continue;
            }

            if (policies.Count == 0)
            {
                problems.Add($"{name} names no policy");
            }
            else if (policies.Any(p => p is null || !Policies.All.Contains(p)))
            {
                problems.Add($"{name} does not use a named policy (found: {string.Join(", ", policies.Select(p => p ?? "default"))})");
            }

            if (endpoint.Metadata.GetMetadata<RoleRequirement>() is { } role
                && !endpoint.RoutePattern.Parameters.Any(p => p.Name == role.RouteParameter))
            {
                problems.Add($"{name} needs a role by route parameter '{role.RouteParameter}', which its route does not have");
            }
        }

        return problems;
    }

    [Fact]
    public async Task Every_endpoint_of_the_app_names_a_policy_or_is_reviewed_as_anonymous()
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync());
        _ = factory.CreateClient(); // starts the app
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        endpoints.OfType<RouteEndpoint>().Count().ShouldBeGreaterThan(8); // it really looked at the app

        Problems(endpoints, ReviewedAnonymousEndpoints).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_anonymous_entry_on_the_reviewed_list_still_exists()
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync());
        _ = factory.CreateClient();
        var described = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(Describe).ToList();

        // A stale entry would silently allow a future endpoint with the same name.
        ReviewedAnonymousEndpoints.Where(entry => !described.Contains(entry)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_list_of_policies_is_complete()
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync());
        _ = factory.CreateClient();
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var name in Policies.All)
        {
            (await provider.GetPolicyAsync(name)).ShouldNotBeNull($"policy {name} is not registered");
        }

        (await provider.GetFallbackPolicyAsync()).ShouldNotBeNull();
    }

    // ---- the test itself must be able to fail ---------------------------------------------------------------------

    private static async Task<List<string>> ProblemsOfAsync(Action<IEndpointRouteBuilder> map, params string[] allowedAnonymous)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddInkpactPolicies();
                    services.AddAuthentication();
                })
                .Configure(app => app.UseRouting().UseEndpoints(map)))
            .StartAsync();

        return Problems(host.Services.GetRequiredService<EndpointDataSource>().Endpoints, allowedAnonymous);
    }

    [Fact]
    public async Task An_endpoint_without_a_policy_is_reported()
    {
        var problems = await ProblemsOfAsync(endpoints => endpoints.MapGet("/api/forgotten", () => "oops"));

        problems.ShouldBe(["GET /api/forgotten names no policy"]);
    }

    [Fact]
    public async Task A_bare_require_authorization_is_not_enough()
    {
        var problems = await ProblemsOfAsync(endpoints => endpoints.MapGet("/api/lazy", () => "oops").RequireAuthorization());

        problems.ShouldHaveSingleItem().ShouldContain("GET /api/lazy does not use a named policy");
    }

    [Fact]
    public async Task A_policy_that_does_not_exist_in_the_list_is_reported()
    {
        var problems = await ProblemsOfAsync(endpoints => endpoints.MapGet("/api/odd", () => "oops").RequireAuthorization("my-own-policy"));

        problems.ShouldHaveSingleItem().ShouldContain("my-own-policy");
    }

    [Fact]
    public async Task An_anonymous_endpoint_that_nobody_reviewed_is_reported()
    {
        var problems = await ProblemsOfAsync(endpoints => endpoints.MapGet("/api/open", () => "hello").AllowAnonymous());

        problems.ShouldBe(["GET /api/open is anonymous but not on the reviewed list"]);
    }

    [Fact]
    public async Task An_anonymous_endpoint_on_the_list_is_fine()
    {
        var problems = await ProblemsOfAsync(endpoints => endpoints.MapGet("/api/open", () => "hello").AllowAnonymous(), "GET /api/open");

        problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task Named_policies_and_role_endpoints_are_fine()
    {
        var problems = await ProblemsOfAsync(endpoints =>
        {
            endpoints.MapGet("/api/a", () => "a").RequireAuthorization(Policies.SignedIn);
            endpoints.MapGet("/api/b", () => "b").RequireAuthorization(Policies.Admin);
            endpoints.MapGet("/api/w/{workspaceId}", (string workspaceId) => workspaceId).RequireWorkspaceRole(Inkpact.Data.Entities.WorkspaceRole.Viewer);
            endpoints.MapGet("/api/p/{pageId}", (string pageId) => pageId).RequirePageRole(Inkpact.Data.Entities.WorkspaceRole.Editor);
        });

        problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_role_requirement_whose_route_has_no_such_parameter_is_reported()
    {
        var problems = await ProblemsOfAsync(endpoints =>
            endpoints.MapGet("/api/w/{id}", (string id) => id).RequireWorkspaceRole(Inkpact.Data.Entities.WorkspaceRole.Viewer));

        problems.ShouldHaveSingleItem().ShouldContain("route parameter 'workspaceId'");
    }

    [Fact]
    public async Task Every_problem_is_listed_not_only_the_first()
    {
        var problems = await ProblemsOfAsync(endpoints =>
        {
            endpoints.MapGet("/api/one", () => "1");
            endpoints.MapPost("/api/two", () => "2");
            endpoints.MapDelete("/api/three", () => "3").AllowAnonymous();
        });

        problems.Count.ShouldBe(3);
    }
}
