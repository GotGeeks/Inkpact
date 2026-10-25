using Inkpact.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInkpactData(builder.Configuration);

var app = builder.Build();

// Applies pending migrations before the first request. Stops the app with a clear message when the
// database is newer than this version, or when the backup before the migration fails.
await app.Services.MigrateInkpactDatabaseAsync();

// Every endpoint declares an authorisation policy. Health is the one anonymous endpoint,
// so it says so explicitly. The deny-by-default permission service arrives in run 1.06.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// Lets the integration tests start the app with WebApplicationFactory.
public partial class Program;
