namespace Inkpact.Api.Tests.Testing;

/// <summary>A temporary folder that looks like the output of the Vite build.</summary>
public sealed class FakeWebRoot : IDisposable
{
    public const string IndexHtml =
        "<!doctype html><html><head><title>Inkpact</title><script type=\"module\" src=\"/assets/app-abc123.js\"></script></head>"
        + "<body><div id=\"app\"></div></body></html>";

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"inkpact-webroot-{Guid.NewGuid():N}");

    public FakeWebRoot(bool withIndex = true)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "assets"));
        File.WriteAllText(System.IO.Path.Combine(Path, "assets", "app-abc123.js"), "console.log('app')");
        File.WriteAllText(System.IO.Path.Combine(Path, "robots.txt"), "User-agent: *");
        if (withIndex)
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "index.html"), IndexHtml);
        }
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
