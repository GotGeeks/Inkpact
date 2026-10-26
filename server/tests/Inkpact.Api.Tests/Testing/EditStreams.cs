using System.Collections.Concurrent;
using System.Diagnostics;

namespace Inkpact.Api.Tests.Testing;

/// <summary>
/// Seeded streams of real Yjs updates: three clients editing one document, some updates delivered out of causal order.
/// Made by <c>server/sidecar/scripts/edit-stream.ts</c> and cached per (edits, seed), as it takes a second of Node.
/// </summary>
internal static class EditStreams
{
    private static readonly ConcurrentDictionary<(int Edits, int Seed), Lazy<IReadOnlyList<byte[]>>> Cache = new();

    public static IReadOnlyList<byte[]> Get(int edits, int seed) =>
        Cache.GetOrAdd((edits, seed), key => new Lazy<IReadOnlyList<byte[]>>(() => Generate(key.Edits, key.Seed))).Value;

    private static IReadOnlyList<byte[]> Generate(int edits, int seed)
    {
        var sidecarFolder = Path.GetDirectoryName(Path.GetDirectoryName(SidecarFiles.ScriptPath))!;
        var file = Path.Combine(Path.GetTempPath(), $"inkpact-stream-{edits}-{seed}-{Guid.NewGuid():N}.bin");
        try
        {
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = sidecarFolder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { Path.Combine("scripts", "edit-stream.ts"), edits.ToString(), seed.ToString(), file })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"edit-stream.ts failed: {error.GetAwaiter().GetResult()}");
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
        var updates = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = (int)BitConverter.ToUInt32(bytes, offset);
            updates.Add(bytes[(offset + 4)..(offset + 4 + length)]);
            offset += 4 + length;
        }

        return updates;
    }
}
