using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Testing;

/// <summary>A context factory over a test database, for services that are built by hand in a test.</summary>
public sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<InkpactDbContext>
{
    public InkpactDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
}
