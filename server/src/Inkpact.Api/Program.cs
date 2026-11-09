using Inkpact.Api.Attachments;
using Inkpact.Api.Auth;
using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Export;
using Inkpact.Api.Import;
using Inkpact.Api.Links;
using Inkpact.Api.Pages;
using Inkpact.Api.Permissions;
using Inkpact.Api.Search;
using Inkpact.Api.Security;
using Inkpact.Api.Sync;
using Inkpact.Api.Favorites;
using Inkpact.Api.Tags;
using Inkpact.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddInkpactData(builder.Configuration);
builder.Services.AddInkpactAuth(builder.Configuration);
builder.Services.AddInkpactPolicies();
builder.Services.AddCsrfProtection();
builder.Services.AddLoginThrottling(builder.Configuration);
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddScoped<TreeService>();
builder.Services.AddScoped<TagService>();
builder.Services.AddSearch();
builder.Services.AddYjsSidecar(builder.Configuration);
builder.Services.AddCompaction(builder.Configuration);
builder.Services.AddSync(builder.Configuration);
builder.Services.AddAttachments(builder.Configuration);
builder.Services.AddImport(builder.Configuration);
builder.Services.AddExport();

var app = builder.Build();

// Applies pending migrations before the first request. Stops the app with a clear message when the
// database is newer than this version, or when the backup before the migration fails.
await app.Services.MigrateInkpactDatabaseAsync();

// Before everything that looks at the scheme or the client address: behind a proxy, what the browser used.
app.UseForwardedHeaders();

// Traefik forwards a WebSocket upgrade as "wss" (or "ws"), not "https". The socket belongs to the page that opened it,
// so it is that page's scheme that counts: for the session cookie's name and for the sync socket's origin check.
app.Use((context, next) =>
{
    if (string.Equals(context.Request.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Scheme = "https";
    }
    else if (string.Equals(context.Request.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Scheme = "http";
    }

    return next(context);
});

// Next, so that errors and 404s carry the security headers too.
app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errors => errors.Run(context =>
        context.Response.WriteAsJsonAsync(new ApiError("server_error"))));
}

// The web bundle: the Vite build, served from the web root (wwwroot, or ASPNETCORE_WEBROOT).
// File names under /assets carry a content hash, so they can be cached for good.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = SetCacheHeaders });

// A request that no endpoint claims, and no file answered, such as a missing file under /assets, is a plain
// 404. Without this the fallback authorisation policy would answer 401, a misleading answer for a missing file.
// (A catch-all endpoint would not do: static files only serves while no endpoint has been selected.)
app.Use((context, next) =>
{
    if (context.GetEndpoint() is not null)
    {
        return next(context);
    }

    context.Response.StatusCode = StatusCodes.Status404NotFound;
    return Task.CompletedTask;
});

app.UseWebSockets();
app.UseJsonOnlyApi();
app.UseAuthentication();
// Per address, before anything costly; then the CSRF token, which is checked against the signed-in user.
app.UseRateLimiter();
app.UseCsrfProtection();
app.UseAuthorization();

// Every endpoint declares an authorisation policy; one that does not is denied by the fallback policy.
// Health is the one anonymous endpoint, so it says so explicitly. The permission service arrives in run 1.06.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAuthEndpoints();
app.MapWorkspaceEndpoints();
app.MapPageEndpoints();
app.MapSearchEndpoints();
app.MapBacklinkEndpoints();
app.MapFavoriteEndpoints();
app.MapTagEndpoints();
app.MapAttachmentEndpoints();
app.MapImportEndpoints();
app.MapExportEndpoints();
app.MapSyncEndpoints();

// An unknown API path is a JSON 404 with an error code, never the HTML shell.
app.MapFallback("/api/{**path}", () => Results.Json(new ApiError("not_found"), statusCode: StatusCodes.Status404NotFound))
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
