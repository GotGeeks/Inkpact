using System.Collections.Concurrent;
using System.Diagnostics;
using Spike.Crdt;
using Spike.Server.Protocol;

namespace Spike.Server.Tests;

/// <summary>Thrown by a compaction stage hook to simulate the process dying at that point.</summary>
public sealed class SimulatedCrash(CompactionStage stage) : Exception($"Simulated crash after {stage}");

/// <summary>A slow test that only runs when INKPACT_STRESS=1 (the 50,000-edit gate of run 0.5).</summary>
public sealed class StressFactAttribute : FactAttribute
{
    public StressFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("INKPACT_STRESS") != "1")
        {
            Skip = "Slow stress test: set INKPACT_STRESS=1 to run it";
        }
    }
}

internal static class TestSidecar
{
    private static readonly string ClientDirectory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "client"));

    public static string ClientPath => ClientDirectory;

    /// <summary>A new Yjs sidecar process. Each caller owns and disposes it.</summary>
    public static YjsSidecarCodec Create() => new(Path.Combine(ClientDirectory, "sidecar", "yjs-sidecar.ts"));
}

/// <summary>
/// Random concurrent TipTap edits written by real Yjs (spike/client/scripts/compaction-stream.ts),
/// in the order a server would receive them. The same (edits, seed) gives the same stream.
/// </summary>
internal static class UpdateStream
{
    private static readonly ConcurrentDictionary<(int Edits, int Seed), Lazy<IReadOnlyList<byte[]>>> Cache = new();

    public static IReadOnlyList<byte[]> Get(int edits, int seed) =>
        Cache.GetOrAdd((edits, seed), key => new Lazy<IReadOnlyList<byte[]>>(() => Generate(key.Edits, key.Seed))).Value;

    private static IReadOnlyList<byte[]> Generate(int edits, int seed)
    {
        var file = Path.Combine(Path.GetTempPath(), $"inkpact-stream-{edits}-{seed}-{Guid.NewGuid():N}.bin");
        try
        {
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = TestSidecar.ClientPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "scripts/compaction-stream.ts", edits.ToString(), seed.ToString(), file })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"compaction-stream.ts failed: {error}");
            }

            return Parse(File.ReadAllBytes(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static List<byte[]> Parse(byte[] bytes)
    {
        List<byte[]> updates = [];
        for (var position = 0; position < bytes.Length;)
        {
            var length = (int)BitConverter.ToUInt32(bytes, position);
            updates.Add(bytes[(position + 4)..(position + 4 + length)]);
            position += 4 + length;
        }

        return updates;
    }
}

internal static class DocumentAssert
{
    /// <summary>Two states are the same document when text and state vector agree.</summary>
    public static void Same(ICrdtCodec codec, byte[] expected, byte[] actual, string because)
    {
        Assert.True(
            codec.ExtractText(expected) == codec.ExtractText(actual),
            $"Plain text differs: {because}");
        Assert.True(
            StateVector(codec, expected).OrderBy(p => p.Key).SequenceEqual(StateVector(codec, actual).OrderBy(p => p.Key)),
            $"State vector differs: {because}");
    }

    public static Dictionary<ulong, ulong> StateVector(ICrdtCodec codec, byte[] state)
    {
        var reader = new Lib0Reader(codec.StateVector(state));
        var vector = new Dictionary<ulong, ulong>();
        for (var count = reader.ReadVarUint(); count > 0; count--)
        {
            vector[reader.ReadVarUint()] = reader.ReadVarUint();
        }

        return vector;
    }
}
