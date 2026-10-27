using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace Inkpact.Api.Crdt;

/// <summary>
/// The Yjs sidecar: a small Node process running Yjs, which is the only decoder of Yjs updates in the app (the YDotNet
/// alternative was rejected in the spike, see <c>docs/spike-findings.md</c>). This class starts it as a child process,
/// restarts it when it dies or hangs, and speaks to it in binary frames over stdin and stdout. The app never waits for
/// it: it starts in the background, and until it is up, or when Node is missing, calls fail with
/// <see cref="YjsUnavailableException"/> and the callers fall back to the stored log.
/// </summary>
public sealed class YjsSidecar(SidecarOptions options, string contentRoot, TimeProvider time, ILogger<YjsSidecar> logger)
    : BackgroundService, IYjsCodec
{
    private const string ProtocolVersion = "1";

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    /// <summary>A process that ran this long was healthy, so its death starts the backoff over.</summary>
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private volatile SidecarSession? _ready;
    private string? _node;
    private int _starts;

    /// <summary>The process serving right now, for tests that kill it.</summary>
    internal int? ProcessId => Ready?.ProcessId;

    /// <summary>The session that can take calls, if there is one. A process that was killed or has exited is not one.</summary>
    private SidecarSession? Ready => _ready is { IsUsable: true } session ? session : null;

    /// <summary>How many processes were started, successfully or not.</summary>
    internal int Starts => Volatile.Read(ref _starts);

    internal void KillForTest() => Ready?.Kill("a test killed it");

    /// <summary>Completes once a process has answered its start-up check.</summary>
    internal async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        while (Ready is null)
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    public async Task<YjsDiff> DiffAsync(
        ReadOnlyMemory<byte> stateVector, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken)
    {
        var parts = new ReadOnlyMemory<byte>[updates.Count + 1];
        parts[0] = stateVector;
        for (var i = 0; i < updates.Count; i++)
        {
            parts[i + 1] = updates[i];
        }

        var answer = await CallAsync(SidecarOp.Diff, parts, options.DiffTimeout, cancellationToken);
        return answer is [var update, var serverStateVector] ? new YjsDiff(update, serverStateVector) : throw Unexpected(answer);
    }

    public async Task<YjsCompaction> CompactAsync(IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(SidecarOp.Compact, Parts(inputs), options.RequestTimeout, cancellationToken);
        return answer is [var state, var stateVector, var replayFingerprint, var stateFingerprint, var text]
            ? new YjsCompaction(state, stateVector, replayFingerprint, stateFingerprint, Encoding.UTF8.GetString(text))
            : throw Unexpected(answer);
    }

    public async Task<byte[]> FingerprintAsync(IReadOnlyList<byte[]> updates, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(SidecarOp.Fingerprint, Parts(updates), options.RequestTimeout, cancellationToken);
        return answer is [var fingerprint] ? fingerprint : throw Unexpected(answer);
    }

    public async Task<byte[]> BuildAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(SidecarOp.Build, [content], options.RequestTimeout, cancellationToken);
        return answer is [var update] ? update : throw Unexpected(answer);
    }

    private static ReadOnlyMemory<byte>[] Parts(IReadOnlyList<byte[]> updates)
    {
        var parts = new ReadOnlyMemory<byte>[updates.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = updates[i];
        }

        return parts;
    }

    private static YjsUnavailableException Unexpected(byte[][] answer) =>
        new($"The Yjs sidecar answered with {answer.Length} parts, which this server does not expect");

    private Task<byte[][]> CallAsync(
        SidecarOp op, IReadOnlyList<ReadOnlyMemory<byte>> parts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var session = Ready
            ?? throw new YjsUnavailableException(options.Enabled ? "The Yjs sidecar is not running" : "The Yjs sidecar is disabled");
        return session.CallAsync(op, parts, timeout, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hand the thread back first: starting the host never waits for Node.
        await Task.Yield();

        if (!options.Enabled)
        {
            logger.LogInformation("The Yjs sidecar is disabled; syncing from the stored log");
            return;
        }

        var backoff = MinBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = time.GetTimestamp();
            try
            {
                await RunProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SidecarStartException ex)
            {
                logger.LogError(
                    "The Yjs sidecar cannot start: {Reason}. The app keeps running without it: subscribers get the stored log, and compaction waits. Trying again in {Delay}",
                    ex.Message, backoff);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The Yjs sidecar failed. Trying again in {Delay}", backoff);
            }

            if (time.GetElapsedTime(started) >= HealthyAfter)
            {
                backoff = MinBackoff;
            }

            try
            {
                await Task.Delay(backoff, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>Starts a process and serves until it ends, or until the host stops. A process that ends on its own is not an error here.</summary>
    private async Task RunProcessAsync(CancellationToken stoppingToken)
    {
        var script = Path.GetFullPath(options.ScriptPath, contentRoot);
        if (!File.Exists(script))
        {
            throw new SidecarStartException($"the script {script} does not exist");
        }

        var node = ResolveNode();
        Interlocked.Increment(ref _starts);

        SidecarSession session;
        try
        {
            session = SidecarSession.Start(CreateStartInfo(node, script), time, logger);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new SidecarStartException($"{node} did not start ({ex.Message})");
        }

        try
        {
            await CheckStartedAsync(session, stoppingToken);
            _ready = session;
            await session.Exited.WaitAsync(stoppingToken);

            // Still here: the process ended without us asking, or because a call killed it.
            if (session.KillReason is null)
            {
                await session.ErrorsReadAsync();
                logger.LogError(
                    "The Yjs sidecar (process {ProcessId}) exited with code {ExitCode}. Its last output:{NewLine}{Output}",
                    session.ProcessId, session.ExitCode, Environment.NewLine, session.LastErrorLines);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _ready, null, session);

            // The process may be alive still: the host is stopping, or its start-up check failed.
            await session.StopAsync(StopGrace);
            await session.EndAsync();
            session.Dispose();
        }
    }

    private async Task CheckStartedAsync(SidecarSession session, CancellationToken cancellationToken)
    {
        byte[][] answer;
        try
        {
            answer = await session.CallAsync(SidecarOp.Ping, [], options.RequestTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is YjsUnavailableException or YjsRejectedException)
        {
            throw new SidecarStartException(
                $"the process did not answer its start-up check ({ex.Message}). Its output:{Environment.NewLine}{session.LastErrorLines}");
        }

        if (answer is not [var protocol, var yjs] || Encoding.UTF8.GetString(protocol) != ProtocolVersion)
        {
            throw new SidecarStartException(
                $"it speaks another protocol than version {ProtocolVersion}; the server and the sidecar are from different versions");
        }

        logger.LogInformation(
            "The Yjs sidecar is running (process {ProcessId}, Yjs {YjsVersion})", session.ProcessId, Encoding.UTF8.GetString(yjs));
    }

    private ProcessStartInfo CreateStartInfo(string node, string script)
    {
        var info = new ProcessStartInfo(node)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };

        // A child inherits the server's environment by default, and in Docker that holds the connection string.
        // The sidecar needs none of it.
        info.Environment.Clear();
        info.Environment["NODE_ENV"] = "production";
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
        {
            info.Environment["SystemRoot"] = systemRoot;
        }

        info.ArgumentList.Add($"--max-old-space-size={options.MaxHeapMb}");
        info.ArgumentList.Add("--disallow-code-generation-from-strings");
        info.ArgumentList.Add("--permission");
        foreach (var path in SidecarPermissions.ReadablePaths(script))
        {
            info.ArgumentList.Add($"--allow-fs-read={path}");
        }

        info.ArgumentList.Add(script);
        return info;
    }

    /// <summary>The full path of Node, from the configuration or the PATH. Found once; looked for again while it is missing.</summary>
    private string ResolveNode()
    {
        if (_node is not null)
        {
            return _node;
        }

        if (options.NodePath.Length > 0)
        {
            return File.Exists(options.NodePath)
                ? _node = Path.GetFullPath(options.NodePath)
                : throw new SidecarStartException($"Sidecar:NodePath {options.NodePath} does not exist");
        }

        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return _node = candidate;
            }
        }

        throw new SidecarStartException($"Node.js ({name}) is not on the PATH and Sidecar:NodePath is not set");
    }

    /// <summary>The sidecar cannot be started; the message says why, for the log.</summary>
    private sealed class SidecarStartException(string message) : Exception(message);
}
