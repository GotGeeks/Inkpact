using System.Text.Json;

namespace Spike.Crdt.Tests;

/// <summary>A fixture written by real Yjs (spike/client/scripts/crdt-fixtures.ts).</summary>
public sealed record Fixture(
    string[] Updates,
    string ExpectedText,
    string ExpectedXml,
    Dictionary<string, ulong> ExpectedStateVector,
    int PartialCount,
    string PartialStateVector,
    int JsMergeBytes,
    int JsStateBytes,
    double JsMergeMs)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<byte[]> UpdateBytes => [.. Updates.Select(Convert.FromBase64String)];

    public sealed record SurrogateSplit(string[] UpdatesBase64, string ExpectedText)
    {
        public IReadOnlyList<byte[]> Updates => [.. UpdatesBase64.Select(Convert.FromBase64String)];
    }

    public static SurrogateSplit LoadSurrogateSplit()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "surrogate-split.json")));
        return new SurrogateSplit(
            [.. json.RootElement.GetProperty("updates").EnumerateArray().Select(u => u.GetString()!)],
            json.RootElement.GetProperty("expectedText").GetString()!);
    }

    /// <summary>The minimal yrs divergence found by delta debugging (written by the minimiser in run 0.4).</summary>
    public sealed record Repro(IReadOnlyList<byte[]> Updates, string YjsText, string YrsText);

    public static Repro LoadRepro()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "yrs-out-of-order-repro.json")));
        var root = json.RootElement;
        return new Repro(
            [.. root.GetProperty("updates").EnumerateArray().Select(u => Convert.FromBase64String(u.GetString()!))],
            root.GetProperty("yjsText").GetString()!,
            root.GetProperty("yrsText").GetString()!);
    }

    public static Fixture Load(string name) =>
        JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.json")), Options)
        ?? throw new InvalidOperationException($"Empty fixture {name}");

    /// <summary>Output for the round-trip check in Yjs: spike/client/scripts/crdt-verify.ts reads it.</summary>
    public static string OutputDirectory
    {
        get
        {
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "crdt-out"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>Decodes a v1 state vector: varuint count, then (varuint client, varuint clock) pairs.</summary>
    public static Dictionary<string, ulong> DecodeStateVector(byte[] stateVector)
    {
        var position = 0;
        ulong ReadVarUint()
        {
            ulong value = 0;
            for (var shift = 0; ; shift += 7)
            {
                var b = stateVector[position++];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    return value;
                }
            }
        }

        var result = new Dictionary<string, ulong>();
        var count = ReadVarUint();
        for (ulong i = 0; i < count; i++)
        {
            var client = ReadVarUint();
            result[client.ToString()] = ReadVarUint();
        }

        return result;
    }
}
