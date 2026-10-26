using System.Text.Json;

namespace Inkpact.Api.Tests.Testing;

/// <summary>
/// A fixture written by real Yjs: the updates of a TipTap document built by three clients, and what Yjs made of them.
/// See Fixtures/yjs/README.md.
/// </summary>
internal sealed record YjsFixture(
    string[] Updates,
    string ExpectedText,
    Dictionary<string, ulong> ExpectedStateVector,
    int PartialCount,
    string PartialStateVector,
    int JsStateBytes)
{
    /// <summary>The fixtures that are a long, random editing session; the contract holds for each of them.</summary>
    public static TheoryData<string> Names => ["tiptap-small", "tiptap-10k", "no-format-2k", "insert-only-2k"];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<byte[]> UpdateBytes => [.. Updates.Select(Convert.FromBase64String)];

    /// <summary>The state vector of a client that has the first <see cref="PartialCount"/> updates.</summary>
    public byte[] PartialStateVectorBytes => Convert.FromBase64String(PartialStateVector);

    public static YjsFixture Load(string name) =>
        JsonSerializer.Deserialize<YjsFixture>(File.ReadAllText(PathOf(name)), Options)
        ?? throw new InvalidOperationException($"Empty fixture {name}");

    /// <summary>A fixture of another shape, such as the repro of one Yjs finding.</summary>
    public static JsonElement Read(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(PathOf(name)));
        return json.RootElement.Clone();
    }

    public static IReadOnlyList<byte[]> Bytes(JsonElement array) =>
        [.. array.EnumerateArray().Select(item => Convert.FromBase64String(item.GetString()!))];

    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "yjs", $"{name}.json");

    /// <summary>
    /// The lines of a text without the empty ones and the spaces at their ends. The sidecar's text extract and
    /// ProseMirror's agree on everything else, which is all search needs.
    /// </summary>
    public static string[] Lines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0)];

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
