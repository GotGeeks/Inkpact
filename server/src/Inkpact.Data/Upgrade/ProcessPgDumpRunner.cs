using System.ComponentModel;
using System.Diagnostics;

namespace Inkpact.Data.Upgrade;

/// <summary>Runs the pg_dump executable from the PATH. The container image must include the Postgres client.</summary>
public class ProcessPgDumpRunner : IPgDumpRunner
{
    public async Task RunAsync(PgDumpRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("pg_dump")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in BuildArguments(request))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The password goes through the environment, never the command line.
        if (request.Password is not null)
        {
            startInfo.Environment["PGPASSWORD"] = request.Password;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Win32Exception exception)
        {
            throw new BackupFailedException(
                "Could not start pg_dump. Install the PostgreSQL client tools, or set Database:BackupOnMigrate to false.",
                exception);
        }

        using (process)
        {
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new BackupFailedException($"pg_dump failed with exit code {process.ExitCode}: {(await standardError).Trim()}");
            }
        }
    }

    internal static IReadOnlyList<string> BuildArguments(PgDumpRequest request) =>
    [
        "--format=custom",
        "--no-password",
        $"--host={request.Host}",
        $"--port={request.Port}",
        $"--username={request.Username}",
        $"--dbname={request.Database}",
        $"--file={request.OutputPath}",
    ];
}
