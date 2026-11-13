using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inkpact.Api.Versions;

/// <summary>Whether and how often the app asks GitHub for a newer release, from the <c>UpdateCheck</c> section.</summary>
public sealed class UpdateCheckOptions
{
    public const string SectionName = "UpdateCheck";

    /// <summary>False: nothing asks GitHub (<c>UpdateCheck__Enabled=false</c>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long after the start the first question is asked.</summary>
    public TimeSpan FirstCheckAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often it is asked again.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long one question may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public static UpdateCheckOptions From(IConfiguration config)
    {
        var options = new UpdateCheckOptions();
        config.GetSection(SectionName).Bind(options);
        if (options.FirstCheckAfter < TimeSpan.Zero || options.FirstCheckAfter.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new InvalidOperationException("UpdateCheck:FirstCheckAfter must be zero or more, and no longer than the timer supports.");
        }

        if (options.Interval < TimeSpan.FromMilliseconds(1) || options.Interval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new InvalidOperationException("UpdateCheck:Interval must be positive and no longer than the timer supports.");
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("UpdateCheck:Timeout must be positive and at most five minutes.");
        }

        return options;
    }
}

/// <summary>A release newer than the running app: its version and its release notes on GitHub.</summary>
public sealed record AvailableUpdate(string Version, string Url);

/// <summary>
/// Asks GitHub for Inkpact's latest release and keeps the answer, in memory only (run B.8). The browser never asks
/// GitHub itself: the CSP keeps it on the app's own origin, and the server asks once for everyone.
/// <para>
/// What is sent: a GET of the public releases API, with <c>User-Agent: Inkpact/&lt;version&gt;</c> and the last ETag.
/// Nothing about the install, its users or its notes. An answer counts only when its tag is a version above the app's
/// and its page is a release page of this repository; anything else is ignored. A failed question keeps the last good
/// answer and is logged with its status code only.
/// </para>
/// </summary>
public sealed class UpdateCheck(
    IHttpClientFactory clients,
    AppVersion version,
    UpdateCheckOptions options,
    ILogger<UpdateCheck> logger)
{
    public const string ClientName = "github-releases";
    public const string Repository = "GotGeeks/Inkpact";
    public static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>Larger answers are refused: a release's notes are a few kilobytes.</summary>
    public const int MaxAnswerBytes = 1024 * 1024;

    private static readonly string ReleasePagePrefix = $"/{Repository}/releases/";

    private readonly Lock _gate = new();
    private string? _etag;
    private AvailableUpdate? _update;

    /// <summary>The newer release the last good answer named; null when there is none, or no answer yet.</summary>
    public AvailableUpdate? Update
    {
        get
        {
            lock (_gate)
            {
                return _update;
            }
        }
    }

    /// <summary>Whether this build asks at all: switched on, and a stamped build (a developer's build never asks).</summary>
    public bool Active => options.Enabled && !version.Release.IsDevelopment;

    /// <summary>Asks once. Never throws for an answer it cannot use; only a cancellation ends it early.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!Active)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Inkpact", version.Release.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        string? etag;
        lock (_gate)
        {
            etag = _etag;
        }

        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The update check got {Status} from GitHub; the last answer stays", (int)response.StatusCode);
                return;
            }

            if (response.Content.Headers.ContentLength > MaxAnswerBytes)
            {
                logger.LogWarning("The update check's answer was too large; the last answer stays");
                return;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var release = await ReadAsync(body, timeout.Token);
            if (release is null)
            {
                logger.LogWarning("The update check's answer could not be used; the last answer stays");
                return;
            }

            var update = UpdateFrom(release);
            lock (_gate)
            {
                _update = update;
                _etag = response.Headers.ETag?.ToString();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The update check timed out; the last answer stays");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("The update check could not reach GitHub ({Error}); the last answer stays", ex.HttpRequestError);
        }
        catch (IOException ex)
        {
            // The connection broke while the answer was read, after its headers (HttpIOException is an IOException).
            logger.LogWarning(
                "The update check's answer was cut off ({Error}); the last answer stays",
                ex is HttpIOException http ? http.HttpRequestError : HttpRequestError.Unknown);
        }
    }

    /// <summary>The release, read from at most <see cref="MaxAnswerBytes"/>; null when it is not one.</summary>
    private static async Task<GitHubRelease?> ReadAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxAnswerBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize<GitHubRelease>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The update the release is, or null: not newer, not a release, or not this repository's page.</summary>
    private AvailableUpdate? UpdateFrom(GitHubRelease release)
    {
        if (release.Draft || release.PreRelease
            || !ReleaseVersion.TryParse(release.TagName, out var latest)
            || latest.IsPreRelease
            || !(latest > version.Release)
            || !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page)
            || page.Scheme != Uri.UriSchemeHttps
            || !string.Equals(page.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !page.IsDefaultPort
            || !page.AbsolutePath.StartsWith(ReleasePagePrefix, StringComparison.Ordinal)
            || page.Query.Length > 0
            || page.Fragment.Length > 0
            || !string.IsNullOrEmpty(page.UserInfo))
        {
            return null;
        }

        return new AvailableUpdate(latest.ToString(), page.AbsoluteUri);
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool PreRelease);
}

/// <summary>
/// Asks a while after the start, then once per interval. A round that fails in a way the check does not expect is logged
/// and the next round comes as usual: a background service that throws stops the whole app, and an update notice is never
/// worth that.
/// </summary>
internal sealed class UpdateCheckWorker(
    UpdateCheck check,
    UpdateCheckOptions options,
    TimeProvider time,
    ILogger<UpdateCheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!check.Active)
        {
            return;
        }

        try
        {
            await Task.Delay(options.FirstCheckAfter, time, stoppingToken);
            using var timer = new PeriodicTimer(options.Interval, time);
            do
            {
                try
                {
                    await check.CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // The type only: an exception's message can hold the address or part of an answer.
                    logger.LogError("The update check failed ({Error}); it asks again at the next round", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }
}

public static class VersionSetup
{
    public static IServiceCollection AddVersions(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(AppVersion.FromAssembly(typeof(VersionSetup).Assembly));
        services.AddSingleton(UpdateCheckOptions.From(config));
        // No automatic redirects: the releases API answers directly, and a redirect elsewhere is not followed. No request
        // log of the factory's own (a line per request with its address): the check logs its outcome itself.
        services.AddHttpClient(UpdateCheck.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<UpdateCheck>();
        services.AddHostedService<UpdateCheckWorker>();
        return services;
    }
}
