using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inkpact.Api.Tests.Testing;

/// <summary>Starts the real app against a given (empty) test database.</summary>
public class InkpactFactory(string connectionString, bool backupOnMigrate = false) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Inkpact", connectionString);
        builder.UseSetting("Database:BackupOnMigrate", backupOnMigrate.ToString());
    }
}
