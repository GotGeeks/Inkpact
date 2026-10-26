namespace Inkpact.Api.Crdt;

/// <summary>
/// What the sidecar's Node process may read. It runs with Node's permission model on, which denies everything else:
/// reading other files (the server's configuration, <c>/proc/&lt;pid&gt;/environ</c>), writing, starting processes. A bug in
/// the Yjs decoder, reached with bytes a user sent, then cannot do much more than answer wrongly.
/// </summary>
internal static class SidecarPermissions
{
    private const string PnpmStore = ".pnpm";

    /// <summary>
    /// The sidecar's package folder (the script's, with its <c>node_modules</c>), and where the links in that
    /// <c>node_modules</c> lead. pnpm does not copy packages in: it links them to a store shared by the workspace, which
    /// lies outside the folder. A deployed sidecar has the store inside it, and then nothing more is needed.
    /// </summary>
    public static IReadOnlyList<string> ReadablePaths(string script)
    {
        var packageFolder = FindPackageFolder(script);
        var paths = new List<string> { packageFolder };

        var modules = new DirectoryInfo(Path.Combine(packageFolder, "node_modules"));
        if (modules.Exists)
        {
            foreach (var package in modules.EnumerateDirectories().SelectMany(Packages))
            {
                if (package.LinkTarget is not null && package.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    var readable = StoreOf(target.FullName) ?? target.FullName;
                    if (!IsInside(readable, packageFolder))
                    {
                        paths.Add(readable);
                    }
                }
            }
        }

        return [.. paths.Distinct()];
    }

    /// <summary>The folders of <c>node_modules/@scope</c> hold packages; any other folder is one.</summary>
    private static IEnumerable<DirectoryInfo> Packages(DirectoryInfo entry) =>
        entry.Name.StartsWith('@') && entry.LinkTarget is null ? entry.EnumerateDirectories() : [entry];

    /// <summary>The folder with the package.json: the script's own, or its parent when the script is in <c>src</c>.</summary>
    private static string FindPackageFolder(string script)
    {
        var scriptFolder = Path.GetDirectoryName(script)!;
        var parent = Path.GetDirectoryName(scriptFolder);
        return parent is not null && !File.Exists(Path.Combine(scriptFolder, "package.json")) && File.Exists(Path.Combine(parent, "package.json"))
            ? parent
            : scriptFolder;
    }

    /// <summary>The pnpm store a package path lies in, if it does.</summary>
    private static string? StoreOf(string path)
    {
        for (var folder = new DirectoryInfo(path); folder is not null; folder = folder.Parent)
        {
            if (folder.Name == PnpmStore)
            {
                return folder.FullName;
            }
        }

        return null;
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || path == folder;
}
