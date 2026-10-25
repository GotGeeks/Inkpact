using System.Net;
using Inkpact.Api.Tests.Testing;

namespace Inkpact.Api.Tests.Auth;

/// <summary>A second factory on the same database and keys behaves like the app after a restart.</summary>
[Collection(PostgresCollection.Name)]
public class RestartTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _keys = Path.Combine(Path.GetTempPath(), $"inkpact-keys-{Guid.NewGuid():N}");
    private string _connectionString = "";

    public async Task InitializeAsync() => _connectionString = await postgres.CreateDatabaseAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_keys))
        {
            Directory.Delete(_keys, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task<string> SignInAndGetCookieAsync(InkpactFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.SetupAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return response.SetCookies().Single().Split(';')[0];
    }

    private static async Task<Inkpact.Api.Auth.AuthState> StateWithCookieAsync(InkpactFactory factory, string cookie)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return await client.GetStateAsync();
    }

    [Fact]
    public async Task A_session_survives_a_restart_with_the_same_keys()
    {
        string cookie;
        await using (var before = new InkpactFactory(_connectionString, keysDirectory: _keys))
        {
            cookie = await SignInAndGetCookieAsync(before);
        }

        await using var after = new InkpactFactory(_connectionString, keysDirectory: _keys);

        (await StateWithCookieAsync(after, cookie)).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_keys_are_written_to_the_data_volume()
    {
        await using var factory = new InkpactFactory(_connectionString, keysDirectory: _keys);
        await SignInAndGetCookieAsync(factory);

        Directory.GetFiles(_keys, "key-*.xml").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Without_the_old_keys_the_session_cookie_is_useless_but_nothing_crashes()
    {
        string cookie;
        await using (var before = new InkpactFactory(_connectionString, keysDirectory: _keys))
        {
            cookie = await SignInAndGetCookieAsync(before);
        }

        var otherKeys = Path.Combine(Path.GetTempPath(), $"inkpact-keys-{Guid.NewGuid():N}");
        try
        {
            await using var after = new InkpactFactory(_connectionString, keysDirectory: otherKeys);

            var state = await StateWithCookieAsync(after, cookie);

            state.User.ShouldBeNull();
            state.SetupRequired.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(otherKeys, recursive: true);
        }
    }
}
