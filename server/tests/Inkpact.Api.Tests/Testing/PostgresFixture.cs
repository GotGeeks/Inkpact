using Npgsql;
using Testcontainers.PostgreSql;

namespace Inkpact.Api.Tests.Testing;

/// <summary>
/// One real Postgres container for every test class that uses <see cref="PostgresCollection"/>.
/// Each test gets its own empty database from <see cref="CreateDatabaseAsync"/>, so tests never share data.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public PostgreSqlContainer Container => _container;

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database with a random name and returns its connection string.</summary>
    /// <param name="pooled">Connections are not pooled by default, so a test never leaves connections open on the shared server. A test that makes thousands of short calls (an import) asks for a pool: without one every call is a new connection, and the machine runs out of ports.</param>
    public async Task<string> CreateDatabaseAsync(bool pooled = false)
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = pooled, MaxPoolSize = 30 }
            .ConnectionString;
    }
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
