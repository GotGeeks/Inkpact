using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Tests.Testing;

namespace Inkpact.Api.Tests.Security;

[Collection(PostgresCollection.Name)]
public class SpaServingTests(PostgresFixture postgres)
{
    private async Task<(InkpactFactory Factory, HttpClient Client, FakeWebRoot WebRoot)> StartAsync(bool withIndex = true)
    {
        var webRoot = new FakeWebRoot(withIndex);
        var factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), webRoot: webRoot.Path);
        return (factory, factory.CreateClient(), webRoot);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/workspace/notes/123")]
    [InlineData("/login")]
    public async Task The_shell_is_served_for_the_root_and_for_client_side_routes(string path)
    {
        var (factory, client, webRoot) = await StartAsync();
        using var _ = webRoot;
        await using var __ = factory;

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await response.Content.ReadAsStringAsync()).ShouldBe(FakeWebRoot.IndexHtml);
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
    }

    [Fact]
    public async Task Hashed_assets_are_cached_for_a_year_and_marked_immutable()
    {
        var (factory, client, webRoot) = await StartAsync();
        using var _ = webRoot;
        await using var __ = factory;

        var response = await client.GetAsync("/assets/app-abc123.js");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cacheControl = response.Headers.CacheControl!;
        cacheControl.Public.ShouldBeTrue();
        cacheControl.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        response.Headers.CacheControl!.ToString().ShouldContain("immutable");
    }

    [Fact]
    public async Task Other_static_files_are_always_revalidated()
    {
        var (factory, client, webRoot) = await StartAsync();
        using var _ = webRoot;
        await using var __ = factory;

        var response = await client.GetAsync("/robots.txt");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/api/pages/123/children")]
    [InlineData("/api")]
    public async Task An_unknown_api_path_is_a_json_404_and_never_the_shell(string path)
    {
        var (factory, client, webRoot) = await StartAsync();
        using var _ = webRoot;
        await using var __ = factory;

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        if (path != "/api")
        {
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
            var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            body.ShouldNotBeNull();
            body["code"].ShouldBe("not_found");
        }

        (await response.Content.ReadAsStringAsync()).ShouldNotContain("<html");
    }

    [Fact]
    public async Task A_missing_asset_is_a_404_and_not_the_shell()
    {
        var (factory, client, webRoot) = await StartAsync();
        using var _ = webRoot;
        await using var __ = factory;

        var response = await client.GetAsync("/assets/missing.js");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("<html");
    }

    [Fact]
    public async Task Without_a_built_bundle_the_app_still_starts_and_answers_404()
    {
        var (factory, client, webRoot) = await StartAsync(withIndex: false);
        using var _ = webRoot;
        await using var __ = factory;

        (await client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_api_health_endpoint_is_never_cached()
    {
        var (factory, client, webRoot) = await StartAsync();
        using var _ = webRoot;
        await using var __ = factory;

        var response = await client.GetAsync("/api/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }
}
