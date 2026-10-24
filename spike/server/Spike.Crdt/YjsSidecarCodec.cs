using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Spike.Crdt;

/// <summary>
/// <see cref="ICrdtCodec"/> through a Node sidecar running Yjs itself (spike/client/sidecar/yjs-sidecar.ts).
/// Prototype: newline-delimited JSON over the child's stdin/stdout, one request at a time.
/// </summary>
public sealed class YjsSidecarCodec : ICrdtCodec, IDisposable
{
    private readonly Process _process;
    private readonly Lock _lock = new();
    private int _nextId;

    public YjsSidecarCodec(string sidecarScript)
    {
        _process = Process.Start(new ProcessStartInfo("node", [sidecarScript])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            // Node writes UTF-8; without this, Windows decodes stdout in the console code page.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        }) ?? throw new InvalidOperationException("Could not start the Yjs sidecar");
    }

    public byte[] Merge(IEnumerable<byte[]> updates) =>
        Bytes(Call(new JsonObject { ["op"] = "merge", ["updates"] = new JsonArray([.. updates.Select(Base64)]) }));

    public byte[] Diff(byte[] state, byte[] stateVector) =>
        Bytes(Call(new JsonObject { ["op"] = "diff", ["state"] = Base64(state), ["stateVector"] = Base64(stateVector) }));

    public byte[] StateVector(byte[] state) =>
        Bytes(Call(new JsonObject { ["op"] = "stateVector", ["state"] = Base64(state) }));

    public string ExtractText(byte[] state) =>
        Call(new JsonObject { ["op"] = "text", ["state"] = Base64(state) });

    /// <summary>The sidecar process' current and peak working set, for the findings.</summary>
    public (long Current, long Peak) Memory
    {
        get
        {
            _process.Refresh();
            return (_process.WorkingSet64, _process.PeakWorkingSet64);
        }
    }

    public void Dispose()
    {
        _process.StandardInput.Close();
        if (!_process.WaitForExit(5000))
        {
            _process.Kill();
        }

        _process.Dispose();
    }

    private string Call(JsonObject request)
    {
        lock (_lock)
        {
            var id = ++_nextId;
            request["id"] = id;
            _process.StandardInput.WriteLine(request.ToJsonString());
            _process.StandardInput.Flush();

            var line = _process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("Yjs sidecar exited");
            var response = JsonNode.Parse(line)!.AsObject();
            if (response["id"]!.GetValue<int>() != id)
            {
                throw new InvalidOperationException("Yjs sidecar answered out of order");
            }

            return response["ok"]!.GetValue<bool>()
                ? response["result"]!.GetValue<string>()
                : throw new InvalidDataException($"Yjs sidecar: {response["error"]}");
        }
    }

    private static JsonNode Base64(byte[] bytes) => JsonValue.Create(Convert.ToBase64String(bytes));

    private static byte[] Bytes(string base64) => Convert.FromBase64String(base64);
}
