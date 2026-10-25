var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Every endpoint declares an authorisation policy. Health is the one anonymous endpoint,
// so it says so explicitly. The deny-by-default permission service arrives in run 1.06.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// Lets the integration tests start the app with WebApplicationFactory.
public partial class Program;
