using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<PackProjectsModule>]
public class VerifyCoreInternalsDependenciesModule : Module<string>
{
    protected override async Task<string?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var output = await VerifyAsync(Path.GetFullPath(".."), Path.GetFullPath("../artifacts/packages"),
            NugetVersionGeneratorModule.GetGeneratedVersion(), cancellationToken);
        context.Logger.LogInformation("{ValidationOutput}", output);
        return output;
    }

    internal static async Task<string> VerifyAsync(string repositoryRoot, string packagesPath, string version,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(repositoryRoot, "scripts", "Verify-CoreInternalsDependencies.ps1"),
                     "-PackagesPath", packagesPath, "-Version", version })
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException("Failed to start core dependency validation.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Core dependency validation failed (exit {process.ExitCode}): {error}{output}");
        return output;
    }
}
