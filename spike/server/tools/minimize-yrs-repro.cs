#:project ../Spike.Crdt/Spike.Crdt.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Delta debugging: smallest ordered list of updates where yrs and Yjs (sidecar) disagree on the text.
using System.Text.Json;
using Spike.Crdt;

// Run from the repository root: dotnet run spike/server/tools/minimize-yrs-repro.cs [fixture]
var root = Path.GetFullPath("spike");
var fixtureName = args.Length > 0 ? args[0] : "tiptap-small";
var json = JsonDocument.Parse(File.ReadAllText($"{root}/server/Spike.Crdt.Tests/Fixtures/{fixtureName}.json"));
var all = json.RootElement.GetProperty("updates").EnumerateArray().Select(u => Convert.FromBase64String(u.GetString()!)).ToList();

var yrs = new YrsCodec();
using var yjs = new YjsSidecarCodec($"{root}/client/sidecar/yjs-sidecar.ts");

bool Diverges(List<byte[]> updates)
{
    try { return yrs.ExtractText(yrs.Merge(updates)) != yjs.ExtractText(yjs.Merge(updates)); }
    catch { return false; }
}

// Find a shuffled order that diverges.
var random = new Random(7);
List<byte[]> current = [];
for (var attempt = 0; attempt < 50; attempt++)
{
    var shuffled = all.OrderBy(_ => random.Next()).ToList();
    if (Diverges(shuffled)) { current = shuffled; break; }
}
if (current.Count == 0) { Console.WriteLine("no diverging order found"); return; }
Console.WriteLine($"start: {current.Count} updates diverge");

// ddmin: remove chunks while the divergence persists.
var granularity = 2;
while (current.Count >= 2)
{
    var chunk = (int)Math.Ceiling(current.Count / (double)granularity);
    var reduced = false;
    for (var start = 0; start < current.Count; start += chunk)
    {
        var complement = current.Take(start).Concat(current.Skip(start + chunk)).ToList();
        if (complement.Count > 0 && Diverges(complement))
        {
            current = complement;
            granularity = Math.Max(granularity - 1, 2);
            reduced = true;
            break;
        }
    }
    if (!reduced)
    {
        if (granularity >= current.Count) break;
        granularity = Math.Min(granularity * 2, current.Count);
    }
}

Console.WriteLine($"minimal: {current.Count} updates");
Console.WriteLine($"  yrs: {JsonSerializer.Serialize(yrs.ExtractText(yrs.Merge(current)))}");
Console.WriteLine($"  Yjs: {JsonSerializer.Serialize(yjs.ExtractText(yjs.Merge(current)))}");
var outPath = $"{root}/server/Spike.Crdt.Tests/Fixtures/yrs-out-of-order-repro.json";
File.WriteAllText(outPath, JsonSerializer.Serialize(new
{
    description = $"Minimal ordered update list (from {fixtureName}, shuffled) where yrs and Yjs give different text",
    updates = current.Select(Convert.ToBase64String),
    yjsText = yjs.ExtractText(yjs.Merge(current)),
    yrsText = yrs.ExtractText(yrs.Merge(current)),
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"written {outPath}");
