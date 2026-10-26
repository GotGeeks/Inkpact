using System.Diagnostics;
using Inkpact.Api.Crdt;
using Inkpact.Api.Tests.Testing;

namespace Inkpact.Api.Tests.Crdt;

public sealed class SidecarPermissionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inkpact-permissions-{Guid.NewGuid():N}");
    private readonly List<string> _links = [];

    public void Dispose()
    {
        // The links first, on their own: a recursive delete would meet them after their targets are gone.
        foreach (var link in _links.Where(Directory.Exists))
        {
            Directory.Delete(link);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A link to a folder, the way pnpm makes it: a junction on Windows, where a symbolic link needs a privilege.</summary>
    private void Link(string path, string target)
    {
        _links.Add(path);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target]) { CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        mklink.ExitCode.ShouldBe(0);
    }

    private string Folder(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Script(string package)
    {
        var folder = Folder(Path.Combine(package, "src"));
        File.WriteAllText(Path.Combine(package, "package.json"), "{}");
        var script = Path.Combine(folder, "main.ts");
        File.WriteAllText(script, "");
        return script;
    }

    [Fact]
    public void A_sidecar_with_its_packages_inside_may_read_its_own_folder_only()
    {
        var script = Script(Path.Combine(_root, "app", "sidecar"));
        Folder(Path.Combine("app", "sidecar", "node_modules", "yjs"));

        SidecarPermissions.ReadablePaths(script).ShouldBe([Path.Combine(_root, "app", "sidecar")]);
    }

    [Fact]
    public void Packages_linked_into_a_pnpm_store_make_the_store_readable_too()
    {
        // The workspace layout: node_modules/yjs is a link into the store at the root, and so is a scoped package.
        var script = Script(Path.Combine(_root, "repo", "server", "sidecar"));
        var store = Folder(Path.Combine("repo", "node_modules", ".pnpm"));
        var yjs = Folder(Path.Combine("repo", "node_modules", ".pnpm", "yjs@1.0.0", "node_modules", "yjs"));
        var types = Folder(Path.Combine("repo", "node_modules", ".pnpm", "@types+node@1.0.0", "node_modules", "@types", "node"));
        var modules = Folder(Path.Combine("repo", "server", "sidecar", "node_modules"));
        Link(Path.Combine(modules, "yjs"), yjs);
        Folder(Path.Combine("repo", "server", "sidecar", "node_modules", "@types"));
        Link(Path.Combine(modules, "@types", "node"), types);

        SidecarPermissions.ReadablePaths(script)
            .ShouldBe([Path.Combine(_root, "repo", "server", "sidecar"), store]);
    }

    [Fact]
    public void A_script_without_a_package_json_next_to_it_may_read_only_its_own_folder()
    {
        var folder = Folder("loose");
        var script = Path.Combine(folder, "main.mjs");
        File.WriteAllText(script, "");

        SidecarPermissions.ReadablePaths(script).ShouldBe([folder]);
    }

    [Fact]
    public void The_repository_sidecar_reads_its_folder_and_the_store_and_nothing_else()
    {
        var paths = SidecarPermissions.ReadablePaths(SidecarFiles.ScriptPath);

        paths[0].ShouldBe(Path.GetFullPath(Path.Combine(SidecarFiles.ScriptPath, "..", "..")));
        paths.Skip(1).ShouldAllBe(path => Path.GetFileName(path) == ".pnpm");
    }
}
