namespace Inkpact.Api.Tests.Testing;

/// <summary>Where the sidecar's script is in the repository. It needs <c>pnpm install</c> to have run, as the tests start the real thing.</summary>
internal static class SidecarFiles
{
    public static string ScriptPath { get; } = Find();

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "sidecar", "src", "main.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("server/sidecar/src/main.ts was not found above the test assembly");
    }
}
