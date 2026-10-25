using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Inkpact.Data;

/// <summary>Used by <c>dotnet ef</c> only. It never connects to a database to create a migration.</summary>
internal class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<InkpactDbContext>
{
    public InkpactDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<InkpactDbContext>()
            .UseNpgsql("Host=localhost;Database=inkpact_design")
            .Options);
}
