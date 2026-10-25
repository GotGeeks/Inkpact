using Inkpact.Api.Security;
using Inkpact.Data;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddInkpactData(builder.Configuration);

var app = builder.Build();

// Applies pending migrations before the first request. Stops the app with a clear message when the
// database is newer than this version, or when the backup before the migration fails.
await app.Services.MigrateInkpactDatabaseAsync();

// First, so that errors and 404s carry the security headers too.
app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errors => errors.Run(context =>
        context.Response.WriteAsJsonAsync(new { code = "server_error" })));
}

// The web bundle: the Vite build, served from the web root (wwwroot, or ASPNETCORE_WEBROOT).
// File names under /assets carry a content hash, so they can be cached for good.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = SetCacheHeaders });

// Every endpoint declares an authorisation policy. Health is the one anonymous endpoint,
// so it says so explicitly. The deny-by-default permission service arrives in run 1.06.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// An unknown API path is a JSON 404 with an error code, never the HTML shell.
app.MapFallback("/api/{**path}", () => Results.Json(new { code = "not_found" }, statusCode: StatusCodes.Status404NotFound))
    .AllowAnonymous();

// Every other path that is not a file belongs to the client-side router. The shell contains no data,
// so it is anonymous by design.
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = SetCacheHeaders })
    .AllowAnonymous();

app.Run();

static void SetCacheHeaders(StaticFileResponseContext context)
{
    var headers = context.Context.Response.GetTypedHeaders();
    var hashed = context.Context.Request.Path.StartsWithSegments("/assets");
    headers.CacheControl = hashed
        ? new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365), Extensions = { new NameValueHeaderValue("immutable") } }
        : new CacheControlHeaderValue { NoCache = true };
}

// Lets the integration tests start the app with WebApplicationFactory.
public partial class Program;
