using System.Net;
using System.Security.Cryptography;
using System.Text;
using Inkpact.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Inkpact.Api.Auth;

public static class AuthenticationSetup
{
    public const string CookieName = "inkpact";
    public const string HostCookieName = "__Host-inkpact";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    public static IServiceCollection AddInkpactAuth(this IServiceCollection services, IConfiguration config)
    {
        var securePolicy = ResolveCookieSecurePolicy(config["Auth:CookieSecure"]);

        services.AddSingleton<PostgresTicketStore>();
        services.AddSingleton<ITicketStore>(provider => provider.GetRequiredService<PostgresTicketStore>());
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton(SetupCode.FromConfiguration(config["Auth:SetupCode"]));
        services.AddHostedService<SessionCleanupWorker>();
        services.AddHostedService<SetupCodeAnnouncer>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => ConfigureCookie(options, securePolicy));
        // The store is a singleton that needs the database; options are built after the container exists.
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((options, store) => options.SessionStore = store);

        AddDataProtection(services, config);
        AddForwardedHeaders(services, config);
        return services;
    }

    /// <summary>
    /// "auto" (the default) sets Secure whenever the browser came in over HTTPS, also behind a proxy that
    /// terminates TLS, and still works on a plain-HTTP LAN address. "true" always sets it; "false" never does.
    /// </summary>
    internal static CookieSecurePolicy ResolveCookieSecurePolicy(string? setting) =>
        setting?.Trim().ToLowerInvariant() switch
        {
            "true" => CookieSecurePolicy.Always,
            "false" => CookieSecurePolicy.None,
            _ => CookieSecurePolicy.SameAsRequest,
        };

    private static void ConfigureCookie(CookieAuthenticationOptions options, CookieSecurePolicy securePolicy)
    {
        options.Cookie.Name = CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = securePolicy;
        options.Cookie.Path = "/";
        options.CookieManager = new SessionCookieManager(securePolicy);
        options.ExpireTimeSpan = SessionLifetime;
        options.SlidingExpiration = true;

        // An API, not a website: no redirects to a login page, the client decides what to show.
        options.Events.OnRedirectToLogin = context => WriteStatus(context.Response, StatusCodes.Status401Unauthorized, "not_authenticated");
        options.Events.OnRedirectToAccessDenied = context => WriteStatus(context.Response, StatusCodes.Status403Forbidden, "forbidden");
    }

    private static Task WriteStatus(HttpResponse response, int status, string code)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new ApiError(code));
    }

    private static void AddDataProtection(IServiceCollection services, IConfiguration config)
    {
        var keysDirectory = config["DataProtection:KeysDirectory"] is { Length: > 0 } configured ? configured : "data/keys";
        var directory = new DirectoryInfo(keysDirectory);
        directory.Create();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var builder = services.AddDataProtection().SetApplicationName("Inkpact").PersistKeysToFileSystem(directory);

        // Optional. Without it the keys sit unencrypted on the data volume: anyone who can read that volume
        // can already read every note, so a certificate stored next to the keys would add nothing.
        if (config["DataProtection:CertificatePath"] is { Length: > 0 } certificatePath)
        {
            var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath, config["DataProtection:CertificatePassword"]);
            builder.ProtectKeysWithCertificate(certificate);
        }
    }

    private static void AddForwardedHeaders(IServiceCollection services, IConfiguration config)
    {
        // Behind Traefik the app sees HTTP from the proxy; X-Forwarded-Proto says what the browser used.
        // Only trusted addresses may set it. The default covers loopback and the private ranges, which is
        // where Docker and Unraid networks live. Nobody else can reach the app directly in a normal install.
        var trusted = config.GetSection("Network:TrustedProxies").Get<string[]>()
            ?? ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in trusted)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
    }
}

/// <summary>
/// Uses the "__Host-" cookie name whenever the browser is on HTTPS (that prefix makes browsers insist on Secure,
/// Path=/ and no Domain), and a plain name on HTTP, so an install on a LAN address still works.
/// </summary>
public sealed class SessionCookieManager(CookieSecurePolicy securePolicy) : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    private string NameFor(HttpContext context) =>
        context.Request.IsHttps && securePolicy != CookieSecurePolicy.None
            ? AuthenticationSetup.HostCookieName
            : AuthenticationSetup.CookieName;

    public string? GetRequestCookie(HttpContext context, string key) => _inner.GetRequestCookie(context, NameFor(context));

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        _inner.AppendResponseCookie(context, NameFor(context), value, options);

    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        _inner.DeleteCookie(context, NameFor(context), options);
}

/// <summary>
/// The code that proves whoever creates the first account can read the container's logs. Without it, the first
/// visitor to a fresh install that is reachable from the internet would become its administrator.
/// </summary>
public sealed class SetupCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private SetupCode(string value) => Value = value;

    public string Value { get; }

    public static SetupCode FromConfiguration(string? configured) =>
        new(string.IsNullOrWhiteSpace(configured) ? Generate() : Normalize(configured));

    public static SetupCode Create(string value) => new(Normalize(value));

    private static string Generate() =>
        string.Create(8, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    public bool Matches(string? attempt)
    {
        var expected = Encoding.UTF8.GetBytes(Value);
        var given = Encoding.UTF8.GetBytes(Normalize(attempt ?? ""));
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}

/// <summary>Writes the setup code to the log at startup, but only while there is no account yet.</summary>
public sealed class SetupCodeAnnouncer(IServiceProvider services, SetupCode code, ILogger<SetupCodeAnnouncer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        if (!await db.Users.AnyAsync(cancellationToken))
        {
            logger.LogWarning(
                "FIRST-RUN SETUP: open Inkpact in a browser and create the administrator account with the setup code {SetupCode}",
                code.Value);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
