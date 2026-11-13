using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Inkpact.Api.Tests.Testing;
using Inkpact.Api.Versions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Inkpact.Api.Tests.Versions;

/// <summary>
/// The update check (run B.8) against a GitHub that answers what each test says: what it asks, which answers count as an
/// update, and that nothing it cannot use replaces what it knew. Nothing here reaches the network.
/// </summary>
public class UpdateCheckTests
{
    private const string ReleasePage = "https://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0";

    private readonly FakeGitHub _github = new();
    private readonly CapturingLogger<UpdateCheck> _logger = new();
    private readonly CapturingLogger<UpdateCheckWorker> _workerLogger = new();

    private UpdateCheck Check(string version = "0.1.0", UpdateCheckOptions? options = null) =>
        new(_github.Factory(), AppVersion.Of(version), options ?? new UpdateCheckOptions(), _logger);

    private static string Release(string tag = "v0.2.0", string page = ReleasePage, bool draft = false, bool prerelease = false) =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{page}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"body":"Notes"}""";

    [Fact]
    public async Task Asks_the_releases_api_and_names_a_newer_release()
    {
        _github.Answer(HttpStatusCode.OK, Release());
        var check = Check();

        await check.CheckAsync(CancellationToken.None);

        check.Update.ShouldBe(new AvailableUpdate("0.2.0", ReleasePage));
        var request = _github.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri.ShouldBe(new Uri("https://api.github.com/repos/GotGeeks/Inkpact/releases/latest"));
        // Who asks: the app and its version, and nothing about the install.
        request.Headers.UserAgent.ToString().ShouldBe("Inkpact/0.1.0");
        request.Headers.Accept.ToString().ShouldBe("application/vnd.github+json");
        request.Headers.Authorization.ShouldBeNull();
        request.Headers.Select(h => h.Key).Order().ShouldBe(["Accept", "User-Agent", "X-GitHub-Api-Version"]);
    }

    [Theory]
    [InlineData("v0.1.0")] // the same
    [InlineData("v0.0.9")] // older
    [InlineData("v0.2.0-rc.1")] // a pre-release tag
    [InlineData("latest")] // not a version
    [InlineData("v0.2.0\n")]
    public async Task Names_no_update_for_a_release_that_is_not_newer(string tag)
    {
        _github.Answer(HttpStatusCode.OK, Release(tag));
        var check = Check();

        await check.CheckAsync(CancellationToken.None);

        check.Update.ShouldBeNull();
    }

    [Fact]
    public async Task An_edge_build_is_told_of_the_next_release_and_not_of_its_own()
    {
        _github.Answer(HttpStatusCode.OK, Release("v0.1.0"));
        var check = Check("0.1.0+edge.abc1234");
        await check.CheckAsync(CancellationToken.None);
        check.Update.ShouldBeNull();

        _github.Answer(HttpStatusCode.OK, Release("v0.1.1"));
        await check.CheckAsync(CancellationToken.None);
        check.Update.ShouldBe(new AvailableUpdate("0.1.1", ReleasePage));
    }

    [Fact]
    public async Task An_edge_build_made_before_the_first_release_is_told_of_it()
    {
        _github.Answer(HttpStatusCode.OK, Release("v0.1.0"));
        var check = Check("0.0.0+edge.abc1234");

        await check.CheckAsync(CancellationToken.None);

        check.Active.ShouldBeTrue();
        check.Update.ShouldBe(new AvailableUpdate("0.1.0", ReleasePage));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Names_no_update_for_a_draft_or_a_pre_release(bool draft, bool prerelease)
    {
        _github.Answer(HttpStatusCode.OK, Release(draft: draft, prerelease: prerelease));
        var check = Check();

        await check.CheckAsync(CancellationToken.None);

        check.Update.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0")]
    [InlineData("https://evil.example/GotGeeks/Inkpact/releases/tag/v0.2.0")]
    [InlineData("https://github.com.evil.example/GotGeeks/Inkpact/releases/tag/v0.2.0")]
    [InlineData("https://github.com/someone/Inkpact/releases/tag/v0.2.0")]
    [InlineData("https://github.com/GotGeeks/Inkpact/issues/1")]
    [InlineData("https://github.com:8443/GotGeeks/Inkpact/releases/tag/v0.2.0")]
    [InlineData("https://user@github.com/GotGeeks/Inkpact/releases/tag/v0.2.0")]
    [InlineData("https://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0?x=1")]
    [InlineData("https://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0#x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/GotGeeks/Inkpact/releases/tag/v0.2.0")]
    [InlineData("")]
    public async Task Names_no_update_whose_page_is_not_a_release_page_of_this_repository(string page)
    {
        _github.Answer(HttpStatusCode.OK, Release(page: page));
        var check = Check();

        await check.CheckAsync(CancellationToken.None);

        check.Update.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}")] // the rate limit
    [InlineData(HttpStatusCode.NotFound, "{}")] // no release yet
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":42}")]
    public async Task Keeps_the_last_good_answer_when_an_answer_cannot_be_used(HttpStatusCode status, string body)
    {
        var check = Check();
        _github.Answer(HttpStatusCode.OK, Release());
        await check.CheckAsync(CancellationToken.None);

        _github.Answer(status, body);
        await check.CheckAsync(CancellationToken.None);

        check.Update.ShouldBe(new AvailableUpdate("0.2.0", ReleasePage));
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Keeps_the_last_good_answer_when_github_cannot_be_reached_or_is_too_slow()
    {
        var check = Check(options: new UpdateCheckOptions { Timeout = TimeSpan.FromMilliseconds(200) });
        _github.Answer(HttpStatusCode.OK, Release());
        await check.CheckAsync(CancellationToken.None);

        _github.Fail(new HttpRequestException(HttpRequestError.NameResolutionError));
        await check.CheckAsync(CancellationToken.None);
        _github.Hang();
        await check.CheckAsync(CancellationToken.None);

        check.Update.ShouldBe(new AvailableUpdate("0.2.0", ReleasePage));
        _logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(2);
    }

    [Fact]
    public async Task Keeps_the_last_good_answer_when_the_connection_breaks_while_the_answer_is_read()
    {
        var check = Check();
        _github.Answer(HttpStatusCode.OK, Release());
        await check.CheckAsync(CancellationToken.None);

        // The headers came; the body stops half way, as when GitHub or a proxy resets the connection.
        _github.AnswerCutOff();
        await check.CheckAsync(CancellationToken.None);

        check.Update.ShouldBe(new AvailableUpdate("0.2.0", ReleasePage));
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldContain("cut off (ResponseEnded)");
    }

    [Fact]
    public async Task A_cancellation_ends_it()
    {
        _github.Hang();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => Check().CheckAsync(stop.Token));
    }

    [Fact]
    public async Task Refuses_an_answer_larger_than_a_release_can_be()
    {
        var check = Check();
        var huge = Release().Replace("\"Notes\"", $"\"{new string('x', UpdateCheck.MaxAnswerBytes)}\"", StringComparison.Ordinal);
        _github.Answer(HttpStatusCode.OK, huge);
        await check.CheckAsync(CancellationToken.None);
        check.Update.ShouldBeNull();

        // Also when the answer does not say its length up front.
        _github.Answer(HttpStatusCode.OK, huge, withLength: false);
        await check.CheckAsync(CancellationToken.None);
        check.Update.ShouldBeNull();
    }

    [Fact]
    public async Task Asks_again_with_the_etag_and_keeps_the_answer_on_not_modified()
    {
        var check = Check();
        _github.Answer(HttpStatusCode.OK, Release(), etag: "\"abc\"");
        await check.CheckAsync(CancellationToken.None);

        _github.Answer(HttpStatusCode.NotModified, "");
        await check.CheckAsync(CancellationToken.None);

        _github.Requests[1].Headers.IfNoneMatch.ToString().ShouldBe("\"abc\"");
        check.Update.ShouldBe(new AvailableUpdate("0.2.0", ReleasePage));
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Forgets_the_update_when_the_newest_release_is_no_longer_newer()
    {
        var check = Check();
        _github.Answer(HttpStatusCode.OK, Release());
        await check.CheckAsync(CancellationToken.None);

        // The release was pulled, say: the newest is the app's own again.
        _github.Answer(HttpStatusCode.OK, Release("v0.1.0"));
        await check.CheckAsync(CancellationToken.None);

        check.Update.ShouldBeNull();
    }

    [Fact]
    public async Task Asks_nothing_when_switched_off_or_in_a_development_build()
    {
        _github.Answer(HttpStatusCode.OK, Release());

        var off = Check(options: new UpdateCheckOptions { Enabled = false });
        await off.CheckAsync(CancellationToken.None);
        var development = Check(AppVersion.Development);
        await development.CheckAsync(CancellationToken.None);

        _github.Requests.ShouldBeEmpty();
        off.Active.ShouldBeFalse();
        development.Active.ShouldBeFalse();
    }

    [Fact]
    public async Task Logs_no_address_and_nothing_of_the_answer()
    {
        var check = Check();
        _github.Answer(HttpStatusCode.Forbidden, "{\"message\":\"marker-of-the-body\"}");
        await check.CheckAsync(CancellationToken.None);
        _github.Answer(HttpStatusCode.OK, Release(page: "https://evil.example/marker-of-the-page"));
        await check.CheckAsync(CancellationToken.None);
        _github.Answer(HttpStatusCode.OK, "{\"tag_name\":\"marker-of-the-tag\"");
        await check.CheckAsync(CancellationToken.None);

        _logger.Entries.ShouldNotBeEmpty();
        foreach (var (_, message) in _logger.Entries)
        {
            message.ShouldNotContain("marker", Case.Insensitive);
            message.ShouldNotContain("github.com", Case.Insensitive);
            message.ShouldNotContain("http", Case.Insensitive);
        }
    }

    // ---- the worker ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_worker_asks_after_the_first_wait_and_then_every_interval()
    {
        _github.Answer(HttpStatusCode.OK, Release());
        var options = new UpdateCheckOptions { FirstCheckAfter = TimeSpan.Zero, Interval = TimeSpan.FromMilliseconds(50) };
        var check = Check(options: options);
        using var worker = new UpdateCheckWorker(check, options, TimeProvider.System, _workerLogger);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _github.Requests.Count >= 3);
        await worker.StopAsync(CancellationToken.None);

        check.Update.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_worker_keeps_asking_after_a_failure_the_check_does_not_expect_and_the_app_keeps_running()
    {
        // Something the check does not catch, in every round: the worker must not end (which would stop the host).
        _github.Fail(new InvalidOperationException("marker of the failure"));
        var options = new UpdateCheckOptions { FirstCheckAfter = TimeSpan.Zero, Interval = TimeSpan.FromMilliseconds(50) };
        using var worker = new UpdateCheckWorker(Check(options: options), options, TimeProvider.System, _workerLogger);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _github.Requests.Count >= 3);

        worker.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse();
        await worker.StopAsync(CancellationToken.None);
        worker.ExecuteTask.IsFaulted.ShouldBeFalse();
        _workerLogger.Entries.ShouldNotBeEmpty();
        _workerLogger.Entries.ShouldAllBe(e => e.Level == LogLevel.Error && e.Message.Contains("InvalidOperationException") && !e.Message.Contains("marker"));
    }

    [Fact]
    public async Task The_worker_waits_before_its_first_question()
    {
        _github.Answer(HttpStatusCode.OK, Release());
        var options = new UpdateCheckOptions { FirstCheckAfter = TimeSpan.FromHours(1) };
        using var worker = new UpdateCheckWorker(Check(options: options), options, TimeProvider.System, _workerLogger);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        _github.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_worker_does_nothing_when_switched_off()
    {
        _github.Answer(HttpStatusCode.OK, Release());
        var options = new UpdateCheckOptions { Enabled = false, FirstCheckAfter = TimeSpan.Zero, Interval = TimeSpan.FromMilliseconds(10) };
        using var worker = new UpdateCheckWorker(Check(options: options), options, TimeProvider.System, _workerLogger);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        _github.Requests.ShouldBeEmpty();
    }

    // ---- the settings ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_settings_default_to_a_minute_after_start_then_daily()
    {
        var options = UpdateCheckOptions.From(new ConfigurationBuilder().Build());

        options.Enabled.ShouldBeTrue();
        options.FirstCheckAfter.ShouldBe(TimeSpan.FromMinutes(1));
        options.Interval.ShouldBe(TimeSpan.FromHours(24));
        options.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("UpdateCheck:Interval", "00:00:00")]
    [InlineData("UpdateCheck:Interval", "-00:01:00")]
    [InlineData("UpdateCheck:Interval", "60.00:00:00")]
    [InlineData("UpdateCheck:FirstCheckAfter", "-00:00:01")]
    [InlineData("UpdateCheck:Timeout", "00:00:00")]
    [InlineData("UpdateCheck:Timeout", "00:10:00")]
    public void Refuses_settings_that_cannot_work_at_start_up(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        Should.Throw<InvalidOperationException>(() => UpdateCheckOptions.From(config)).Message.ShouldContain(key);
    }

    [Fact]
    public void Is_switched_off_with_one_setting()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["UpdateCheck:Enabled"] = "false" })
            .Build();

        UpdateCheckOptions.From(config).Enabled.ShouldBeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The condition was not met within ten seconds.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>GitHub, as each test says it answers. Every answer stays until the next is set.</summary>
    internal sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpRequestMessage> _requests = new();
        private Func<CancellationToken, Task<HttpResponseMessage>> _respond = _ => throw new InvalidOperationException("No answer set.");

        public IReadOnlyList<HttpRequestMessage> Requests => [.. _requests];

        public IHttpClientFactory Factory()
        {
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(UpdateCheck.ClientName).Returns(_ => new HttpClient(this, disposeHandler: false));
            return factory;
        }

        public void Answer(HttpStatusCode status, string body, string? etag = null, bool withLength = true) =>
            _respond = _ =>
            {
                HttpContent content = withLength
                    ? new StringContent(body, Encoding.UTF8, "application/json")
                    : new UnknownLengthContent(Encoding.UTF8.GetBytes(body));
                var response = new HttpResponseMessage(status) { Content = content };
                if (etag is not null)
                {
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
                }

                return Task.FromResult(response);
            };

        public void Fail(Exception error) => _respond = _ => Task.FromException<HttpResponseMessage>(error);

        /// <summary>A 200 whose body breaks off part way, as .NET reports a connection reset after the headers.</summary>
        public void AnswerCutOff() =>
            _respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new CutOffContent() });

        public void Hang() => _respond = async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request);
            return _respond(cancellationToken);
        }
    }

    /// <summary>A body that ends early: a stream that gives a few bytes, then throws what SocketsHttpHandler throws.</summary>
    private sealed class CutOffContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Read as a stream only.");

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new CutOffStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class CutOffStream : Stream
        {
            private bool _gave;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_gave)
                {
                    throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
                }

                _gave = true;
                "{\"tag_"u8.CopyTo(buffer.Span);
                return ValueTask.FromResult(6);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>A body that does not say its length up front, as a chunked answer.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
