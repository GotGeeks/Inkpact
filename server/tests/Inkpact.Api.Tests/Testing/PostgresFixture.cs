using Npgsql;
using Testcontainers.PostgreSql;

namespace Inkpact.Api.Tests.Testing;

/// <summary>
/// The real Postgres, one container for the whole test run, as a class fixture (<c>IClassFixture&lt;PostgresFixture&gt;</c>),
/// so that test classes run side by side. Each test gets its own empty database from <see cref="CreateDatabaseAsync"/>, so
/// tests never share data.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Shared = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private PostgreSqlContainer? _container;

    public PostgreSqlContainer Container => _container ?? throw new InvalidOperationException("The fixture has not started.");

    public async Task InitializeAsync() => _container = await Shared.Value;

    // The container outlives every class: it is removed when the test run ends.
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Creates an empty database with a random name and returns its connection string. Its connections are pooled: with
    /// classes running side by side, a new connection for every call runs the machine out of ports. An idle one is closed
    /// after two seconds, so the databases of finished tests do not keep connections open on the shared server.
    /// </summary>
    /// <param name="pooled">A larger pool, for a test that makes thousands of calls from many tasks (an import).</param>
    public async Task<string> CreateDatabaseAsync(bool pooled = false)
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(Container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = name,
            Pooling = true,
            MaxPoolSize = pooled ? 30 : 10,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;
    }

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        // Several classes at once each hold a few connections, and an import's pool up to 30. And they make databases and
        // run parallel queries at once: each parallel query or index build takes a shared-memory segment of up to 32 MB,
        // which Docker's default of 64 MB for /dev/shm ran out of once the suite had grown (run B.5b's restore tests).
        var container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithCommand("-c", "max_connections=500")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
                parameters.HostConfig.ShmSize = 512L * 1024 * 1024;
            })
            .Build();
        await container.StartAsync();
        // Testcontainers' reaper removes the container when the process ends too; this is quicker, and works without it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        return container;
    }
}

/// <summary>
/// The timing tests: they run after every other test, one at a time, so nothing running beside them skews what they measure.
/// They also have the <c>Slow</c> category, which <c>pnpm check:push</c> leaves out.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class SlowCollection
{
    public const string Name = "slow";
}
