using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Api.Versions;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Versions;

/// <summary>
/// <c>GET /api/version</c> (run B.8) in the real app: the version for anyone signed in, the newer release only for the
/// administrator, and nobody signed out. GitHub is the test's: the app is stamped 0.1.0 and told of 0.2.0.
/// </summary>
public class VersionEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string ReleasePage = "https://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0";

    private readonly UpdateCheckTests.FakeGitHub _github = new();
    private InkpactFactory _factory = null!;
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        // The worker waits a day before it asks: the test asks itself, once.
        _factory = new InkpactFactory(
            await postgres.CreateDatabaseAsync(),
            settings: new Dictionary<string, string> { ["UpdateCheck:FirstCheckAfter"] = "1.00:00:00" });
        _app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(AppVersion.Of("0.1.0"));
            services.AddHttpClient(UpdateCheck.ClientName).ConfigurePrimaryHttpMessageHandler(() => _github);
        }));
        _github.Answer(HttpStatusCode.OK, $$"""{"tag_name":"v0.2.0","html_url":"{{ReleasePage}}","draft":false,"prerelease":false}""");
        await _app.Services.GetRequiredService<UpdateCheck>().CheckAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Tells_the_administrator_the_version_and_the_newer_release()
    {
        var admin = _app.CreateClient();
        (await admin.SetupAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await admin.GetAsync("/api/version");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        JsonSerializer.Deserialize<VersionDto>(body, JsonSerializerOptions.Web)
            .ShouldBe(new VersionDto("0.1.0", false, new AvailableUpdate("0.2.0", ReleasePage)));
        _github.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Tells_someone_who_is_not_the_administrator_the_version_only()
    {
        var admin = _app.CreateClient();
        (await admin.SetupAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            db.Users.Add(new User
            {
                Email = "member@example.com",
                DisplayName = "Member",
                PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            (await db.Users.CountAsync(u => u.IsAdmin)).ShouldBe(1);
        }

        var member = _app.CreateClient();
        (await member.LoginAsync("member@example.com")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var version = await member.GetFromJsonAsync<VersionDto>("/api/version", JsonSerializerOptions.Web);

        version.ShouldBe(new VersionDto("0.1.0", false, null));
    }

    [Fact]
    public async Task Says_when_it_is_a_development_build()
    {
        // The factory as the tests build it: not stamped.
        var admin = _factory.CreateClient();
        (await admin.SetupAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var version = await admin.GetFromJsonAsync<VersionDto>("/api/version", JsonSerializerOptions.Web);

        version.ShouldBe(new VersionDto(AppVersion.Development, true, null));
    }

    [Fact]
    public async Task Tells_nobody_who_is_signed_out()
    {
        var response = await _app.CreateClient().GetAsync("/api/version");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("0.1.0");
    }
}
