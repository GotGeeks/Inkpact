using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inkpact.Api.Tests.Testing;

/// <summary>Starts the real app against a given (empty) test database.</summary>
/// <param name="connectionString">The test database.</param>
/// <param name="backupOnMigrate">Whether the migrator dumps the database first.</param>
/// <param name="webRoot">Folder with a built web bundle; null keeps the app's default (no bundle in tests).</param>
/// <param name="environment">The hosting environment. Production by default, as the shipped app runs.</param>
public class InkpactFactory(
    string connectionString,
    bool backupOnMigrate = false,
    string? webRoot = null,
    string environment = "Production") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Inkpact", connectionString);
        builder.UseSetting("Database:BackupOnMigrate", backupOnMigrate.ToString());
        if (webRoot is not null)
        {
            builder.UseSetting(WebHostDefaults.WebRootKey, webRoot);
        }
    }
}
