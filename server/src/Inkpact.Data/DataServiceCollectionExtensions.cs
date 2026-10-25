using Inkpact.Data.Upgrade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "Inkpact";

    public static IServiceCollection AddInkpactData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is missing. Set ConnectionStrings:{ConnectionStringName}.");
        }

        services.AddDbContext<InkpactDbContext>(options => options.UseNpgsql(connectionString));
        services.Configure<DatabaseOptions>(options =>
        {
            // Read by hand so this project does not need the configuration binder package.
            var section = configuration.GetSection(DatabaseOptions.SectionName);
            if (bool.TryParse(section[nameof(DatabaseOptions.BackupOnMigrate)], out var backupOnMigrate))
            {
                options.BackupOnMigrate = backupOnMigrate;
            }

            if (section[nameof(DatabaseOptions.BackupDirectory)] is { Length: > 0 } directory)
            {
                options.BackupDirectory = directory;
            }
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPgDumpRunner, ProcessPgDumpRunner>();
        services.AddSingleton<IDatabaseBackup, PgDumpBackup>();
        services.AddScoped<IMigrationStore, EfMigrationStore>();
        services.AddScoped<DatabaseMigrator>();
        return services;
    }

    /// <summary>Migrates the database. Call before the app starts serving requests.</summary>
    public static async Task MigrateInkpactDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);
    }
}
