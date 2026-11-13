using System.Reflection;

namespace Inkpact.Api.Versions;

/// <summary>
/// The version this build of Inkpact is (run B.8): what the image was stamped with (the Dockerfile's
/// <c>INKPACT_VERSION</c>, as the assembly's informational version), such as <c>0.1.0</c> for a release or
/// <c>0.1.0+edge.abc1234</c> for a build of main. A build that was not stamped says <c>0.0.0-dev</c>
/// (server/Directory.Build.props), and never asks for updates.
/// </summary>
public sealed record AppVersion(string Text, ReleaseVersion Release)
{
    public const string Development = "0.0.0-dev";

    public static AppVersion Of(string? text) =>
        ReleaseVersion.TryParse(text, out var release)
            ? new AppVersion(text!, release)
            : new AppVersion(Development, new ReleaseVersion(0, 0, 0, "dev"));

    public static AppVersion FromAssembly(Assembly assembly) =>
        Of(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
}
