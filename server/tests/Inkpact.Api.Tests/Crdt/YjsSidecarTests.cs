using System.Diagnostics;
using System.Text.Json;
using Inkpact.Api.Crdt;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Microsoft.Extensions.Logging;

namespace Inkpact.Api.Tests.Crdt;

/// <summary>
/// The supervision of the sidecar process: calls in flight together, big inputs, a process that dies, hangs or never
/// starts. The real sidecar where its behaviour matters, and small stand-ins (Fixtures/sidecars) that misbehave on purpose.
/// </summary>
public sealed class YjsSidecarTests : IAsyncLifetime
{
    private readonly List<YjsSidecar> _started = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var sidecar in _started)
        {
            await YjsSidecars.StopAsync(sidecar);
        }
    }

    private async Task<YjsSidecar> StartAsync(YjsSidecar sidecar)
    {
        _started.Add(sidecar);
        return await YjsSidecars.StartAsync(sidecar);
    }

    private Task<YjsSidecar> StartRealAsync(Action<SidecarOptions>? configure = null, ILogger<YjsSidecar>? logger = null) =>
        StartAsync(YjsSidecars.Create(configure, logger));

    private Task<YjsSidecar> StartStubAsync(string script, Action<SidecarOptions>? configure = null, ILogger<YjsSidecar>? logger = null) =>
        StartAsync(YjsSidecars.CreateStub(script, configure, logger));

    /// <summary>A Yjs update of one root text with <paramref name="characters"/> characters in it, written by hand.</summary>
    private static byte[] BigUpdate(int characters)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1); // one client
        writer.WriteVarUint(1); // with one struct
        writer.WriteVarUint(77); // client id
        writer.WriteVarUint(0); // first clock
        writer.WriteVarUint(4); // an item holding a string, with no origins
        writer.WriteVarUint(1); // whose parent is a root type, given by name
        writer.WriteVarString("t");
        writer.WriteVarString(new string('x', characters));
        writer.WriteVarUint(0); // an empty delete set
        return writer.ToArray();
    }

    private static async Task<T> EventuallyAsync<T>(Func<T?> read, string what)
        where T : struct
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (read() is { } value)
            {
                return value;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Gave up waiting for {what}");
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Fifty_calls_at_once_each_get_their_own_answer()
    {
        var sidecar = await StartRealAsync();
        var updates = YjsFixture.Load("tiptap-small").UpdateBytes;

        // Fifty different questions in flight together: a mix-up of answers would show as a wrong fingerprint.
        var answers = await Task.WhenAll(Enumerable.Range(1, 50)
            .Select(count => sidecar.FingerprintAsync(updates.Take(count * 5).ToList(), default)));

        for (var i = 0; i < answers.Length; i++)
        {
            answers[i].ShouldBe(await sidecar.FingerprintAsync(updates.Take((i + 1) * 5).ToList(), default), $"call {i + 1}");
        }

        answers.Select(Convert.ToBase64String).Distinct().Count().ShouldBe(50);
    }

    [Fact]
    public async Task An_input_of_20_MB_round_trips()
    {
        var sidecar = await StartRealAsync();
        var update = BigUpdate(20 * 1024 * 1024);

        var diff = await sidecar.DiffAsync(ReadOnlyMemory<byte>.Empty, [update], default);

        diff.Update.Length.ShouldBeGreaterThan(20 * 1024 * 1024);
        diff.StateVector.ShouldNotBeEmpty();
        (await sidecar.CompactAsync([update], default)).Text.ShouldBe("");
    }

    [Fact]
    public async Task An_input_over_the_frame_limit_is_rejected_without_hurting_the_process()
    {
        var sidecar = await StartRealAsync();
        var processId = sidecar.ProcessId;

        // 257 parts of 1 MiB cannot be one frame of at most 256 MiB.
        var tooMuch = Enumerable.Range(0, 257).Select(_ => new byte[1024 * 1024]).ToList();
        await Should.ThrowAsync<YjsRejectedException>(sidecar.FingerprintAsync(tooMuch, default));

        (await sidecar.FingerprintAsync([], default)).ShouldNotBeEmpty();
        sidecar.ProcessId.ShouldBe(processId);
    }

    [Fact]
    public async Task Killing_the_process_fails_the_waiting_calls_and_a_later_call_succeeds_after_the_restart()
    {
        var logger = new CapturingLogger<YjsSidecar>();
        var sidecar = await StartRealAsync(logger: logger);
        var before = sidecar.ProcessId!.Value;
        var updates = YjsFixture.Load("tiptap-10k").UpdateBytes;

        // Enough work to still be going when the process is killed.
        var calls = Enumerable.Range(0, 60).Select(_ => sidecar.FingerprintAsync(updates, default)).ToList();
        await Task.Delay(100);
        sidecar.KillForTest();

        var outcomes = await Task.WhenAll(calls.Select(async call =>
        {
            try
            {
                await call;
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }));
        outcomes.Where(o => o is not null).ShouldNotBeEmpty("some calls were still waiting");
        outcomes.Where(o => o is not null).ShouldAllBe(o => o is YjsUnavailableException);

        // After the backoff of a second the process is back, a new one.
        await sidecar.WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        sidecar.ProcessId.ShouldNotBe(before);
        (await sidecar.FingerprintAsync([], default)).ShouldNotBeEmpty();
        sidecar.Starts.ShouldBe(2);
    }

    [Fact]
    public async Task A_call_over_its_timeout_kills_the_process_and_it_is_restarted()
    {
        var logger = new CapturingLogger<YjsSidecar>();
        var sidecar = await StartRealAsync(options => options.DiffTimeout = TimeSpan.FromMilliseconds(1), logger);
        var before = sidecar.ProcessId!.Value;
        var fixture = YjsFixture.Load("tiptap-10k");

        await Should.ThrowAsync<YjsUnavailableException>(
            sidecar.DiffAsync(fixture.PartialStateVectorBytes, fixture.UpdateBytes, default));

        await sidecar.WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        sidecar.ProcessId.ShouldNotBe(before);
        IsRunning(before).ShouldBeFalse();
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("Killing the Yjs sidecar"));
        // Other calls have their own, longer timeout.
        (await sidecar.CompactAsync(fixture.UpdateBytes.Take(10).ToList(), default)).StateFingerprint.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_call_that_waits_behind_a_slower_one_gives_up_without_killing_the_process()
    {
        var sidecar = await StartRealAsync(options => options.DiffTimeout = TimeSpan.FromMilliseconds(50));
        var before = sidecar.ProcessId!.Value;
        var updates = YjsFixture.Load("tiptap-10k").UpdateBytes;

        // The compaction has the long timeout; the diff behind it runs out of patience long before it is done.
        var compaction = Enumerable.Range(0, 20).Select(_ => sidecar.CompactAsync(updates, default)).ToList();
        await Should.ThrowAsync<YjsUnavailableException>(sidecar.DiffAsync(ReadOnlyMemory<byte>.Empty, updates, default));

        (await Task.WhenAll(compaction)).Length.ShouldBe(20);
        sidecar.ProcessId.ShouldBe(before);
    }

    [Fact]
    public async Task An_undecodable_input_is_rejected_and_the_process_stays_up()
    {
        var sidecar = await StartRealAsync();
        var before = sidecar.ProcessId!.Value;

        await Should.ThrowAsync<YjsRejectedException>(sidecar.CompactAsync([[255, 255, 255, 255]], default));
        await Should.ThrowAsync<YjsRejectedException>(sidecar.DiffAsync(new byte[] { 255, 255, 255 }, YjsFixture.Load("tiptap-small").UpdateBytes, default));

        (await sidecar.FingerprintAsync([], default)).ShouldNotBeEmpty();
        sidecar.ProcessId.ShouldBe(before);
        sidecar.Starts.ShouldBe(1);
    }

    [Fact]
    public async Task A_wrong_node_path_gives_unavailable_and_says_so_in_the_log()
    {
        var logger = new CapturingLogger<YjsSidecar>();
        var sidecar = YjsSidecars.Create(options => options.NodePath = Path.Combine(AppContext.BaseDirectory, "no-such-node"), logger);
        _started.Add(sidecar);

        await sidecar.StartAsync(CancellationToken.None);

        var error = await Should.ThrowAsync<YjsUnavailableException>(sidecar.FingerprintAsync([], default));
        error.Message.ShouldContain("not running");
        await EventuallyAsync(
            () => logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("cannot start") && e.Message.Contains("no-such-node")) ? 1 : (int?)null,
            "the error in the log");
        logger.Entries.ShouldContain(e => e.Message.Contains("keeps running without it"));
        sidecar.Starts.ShouldBe(0);
    }

    [Fact]
    public async Task A_missing_script_gives_unavailable_and_says_so_in_the_log()
    {
        var logger = new CapturingLogger<YjsSidecar>();
        var sidecar = YjsSidecars.Create(options => options.ScriptPath = "no-such-script.ts", logger);
        _started.Add(sidecar);

        await sidecar.StartAsync(CancellationToken.None);

        await Should.ThrowAsync<YjsUnavailableException>(sidecar.FingerprintAsync([], default));
        await EventuallyAsync(
            () => logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("no-such-script.ts")) ? 1 : (int?)null,
            "the error in the log");
    }

    [Fact]
    public async Task A_disabled_sidecar_never_starts_a_process()
    {
        var sidecar = YjsSidecars.Create(options => options.Enabled = false);
        _started.Add(sidecar);

        await sidecar.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        var error = await Should.ThrowAsync<YjsUnavailableException>(sidecar.FingerprintAsync([], default));
        error.Message.ShouldContain("disabled");
        sidecar.Starts.ShouldBe(0);
    }

    [Fact]
    public async Task Starting_the_host_does_not_wait_for_the_process()
    {
        var sidecar = YjsSidecars.Create();
        _started.Add(sidecar);

        var stopwatch = Stopwatch.StartNew();
        await sidecar.StartAsync(CancellationToken.None);
        var startMs = stopwatch.ElapsedMilliseconds;

        // Until the process answers, calls fail at once instead of waiting.
        var early = Record.ExceptionAsync(() => sidecar.FingerprintAsync([], default));
        (await early).ShouldBeOfType<YjsUnavailableException>();
        startMs.ShouldBeLessThan(500);
        await sidecar.WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
    }

    [Fact]
    public async Task Stopping_the_host_ends_the_process()
    {
        var sidecar = await StartRealAsync();
        var processId = sidecar.ProcessId!.Value;
        IsRunning(processId).ShouldBeTrue();

        await sidecar.StopAsync(CancellationToken.None);

        IsRunning(processId).ShouldBeFalse();
        await Should.ThrowAsync<YjsUnavailableException>(sidecar.FingerprintAsync([], default));
    }

    [Fact]
    public async Task The_child_does_not_inherit_the_environment_of_the_server()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Inkpact", "Host=secret;Password=hunter2");
        try
        {
            var sidecar = await StartStubAsync("env-sidecar.mjs");

            var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
                System.Text.Encoding.UTF8.GetString(await sidecar.FingerprintAsync([], default)))!;

            environment.Keys.ShouldNotContain("ConnectionStrings__Inkpact");
            environment.Keys.ShouldNotContain("PATH");
            environment["NODE_ENV"].ShouldBe("production");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Inkpact", null);
        }
    }

    [Fact]
    public async Task The_child_runs_under_the_permission_model_and_can_only_read_its_own_folder()
    {
        var sidecar = await StartStubAsync("probe-sidecar.mjs");

        var result = JsonSerializer.Deserialize<Dictionary<string, string>>(
            System.Text.Encoding.UTF8.GetString(await sidecar.FingerprintAsync([], default)))!;

        result["readOwn"].ShouldBe("allowed");
        result["readOutside"].ShouldBe("ERR_ACCESS_DENIED");
        result["write"].ShouldBe("ERR_ACCESS_DENIED");
        result["spawn"].ShouldBe("ERR_ACCESS_DENIED");
    }

    [Fact]
    public async Task A_sidecar_that_never_answers_is_killed_when_the_call_times_out_and_comes_back()
    {
        var sidecar = await StartStubAsync("silent-sidecar.mjs", options => options.RequestTimeout = TimeSpan.FromMilliseconds(300));
        var before = sidecar.ProcessId!.Value;

        await Should.ThrowAsync<YjsUnavailableException>(sidecar.FingerprintAsync([], default));

        await sidecar.WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        sidecar.ProcessId.ShouldNotBe(before);
        IsRunning(before).ShouldBeFalse();
    }

    [Fact]
    public async Task A_sidecar_that_stops_reading_is_killed_when_a_big_request_cannot_be_written()
    {
        var sidecar = await StartStubAsync("deaf-sidecar.mjs", options => options.RequestTimeout = TimeSpan.FromMilliseconds(500));
        var before = sidecar.ProcessId!.Value;

        // Far more than a pipe holds, to a process that reads nothing.
        await Should.ThrowAsync<YjsUnavailableException>(sidecar.CompactAsync([new byte[32 * 1024 * 1024]], default));

        await sidecar.WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        sidecar.ProcessId.ShouldNotBe(before);
        IsRunning(before).ShouldBeFalse();
    }

    [Fact]
    public async Task A_crash_is_logged_with_the_last_output_and_followed_by_a_restart()
    {
        var logger = new CapturingLogger<YjsSidecar>();
        var sidecar = await StartStubAsync("crashing-sidecar.mjs", logger: logger);

        await Should.ThrowAsync<YjsUnavailableException>(sidecar.FingerprintAsync([], default));

        await EventuallyAsync(
            () => logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("exited with code 3") && e.Message.Contains("this is the crash")) ? 1 : (int?)null,
            "the crash in the log");
        await EventuallyAsync(() => sidecar.Starts >= 2 ? 1 : (int?)null, "a restart");
    }
}
