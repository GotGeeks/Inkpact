using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Testing;

/// <summary>Starts the real app against a given (empty) test database.</summary>
/// <param name="connectionString">The test database.</param>
/// <param name="backupOnMigrate">Whether the migrator dumps the database first.</param>
/// <param name="webRoot">Folder with a built web bundle; null keeps the app's default (no bundle in tests).</param>
/// <param name="environment">The hosting environment. Production by default, as the shipped app runs.</param>
/// <param name="keysDirectory">Data Protection keys. Two factories sharing one behave like a restarted app.</param>
/// <param name="cookieSecure">The Auth:CookieSecure setting; null leaves the default (auto).</param>
/// <param name="trustedProxies">Network:TrustedProxies as CIDR ranges; null leaves the default.</param>
/// <param name="remoteIp">The address every request appears to come from; the test server has none by default.</param>
/// <param name="perAddressPerMinute">Login attempts one address may make in a minute; high, so tests that sign in a lot are not throttled.</param>
/// <param name="failuresBeforeLock">Wrong passwords before an email is locked; null leaves the default (5).</param>
/// <param name="time">The clock the app uses; null is the real one.</param>
public class InkpactFactory(
    string connectionString,
    bool backupOnMigrate = false,
    string? webRoot = null,
    string environment = "Production",
    string? keysDirectory = null,
    string? cookieSecure = null,
    string[]? trustedProxies = null,
    string? remoteIp = null,
    int perAddressPerMinute = 1000,
    int? failuresBeforeLock = null,
    TimeProvider? time = null) : WebApplicationFactory<Program>
{
    public const string SetupCode = "TESTCODE";

    private readonly string _keysDirectory = keysDirectory
        ?? Path.Combine(Path.GetTempPath(), $"inkpact-keys-{Guid.NewGuid():N}");

    private readonly bool _ownsKeysDirectory = keysDirectory is null;

    public string KeysDirectory => _keysDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Inkpact", connectionString);
        builder.UseSetting("Database:BackupOnMigrate", backupOnMigrate.ToString());
        builder.UseSetting("DataProtection:KeysDirectory", _keysDirectory);
        builder.UseSetting("Auth:SetupCode", SetupCode);
        builder.UseSetting("Auth:Throttle:PerAddressPerMinute", perAddressPerMinute.ToString());
        if (failuresBeforeLock is not null)
        {
            builder.UseSetting("Auth:Throttle:FailuresBeforeLock", failuresBeforeLock.Value.ToString());
        }

        if (time is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(time));
        }

        if (webRoot is not null)
        {
            builder.UseSetting(WebHostDefaults.WebRootKey, webRoot);
        }

        if (cookieSecure is not null)
        {
            builder.UseSetting("Auth:CookieSecure", cookieSecure);
        }

        if (remoteIp is not null)
        {
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(remoteIp))));
        }

        if (trustedProxies is not null)
        {
            for (var i = 0; i < trustedProxies.Length; i++)
            {
                builder.UseSetting($"Network:TrustedProxies:{i}", trustedProxies[i]);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsKeysDirectory && Directory.Exists(_keysDirectory))
        {
            Directory.Delete(_keysDirectory, recursive: true);
        }
    }
}

/// <summary>Runs before the app's own pipeline, so the forwarded-headers middleware sees a client address.</summary>
internal sealed class RemoteIpStartupFilter(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}
