using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using ModularPipelines.Options;

namespace Respire.Pipeline.Modules;

[DependsOn<BuildProjectsModule>]
public class VerifyPackageLocalInitializationModule : Module<CommandResult>
{
    protected override async Task<CommandResult?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var repositoryRoot = Path.GetFullPath("..");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "artifacts"));
        return await context.Shell.Command.ExecuteCommandLineTool(CreateOptions(repositoryRoot),
            new CommandExecutionOptions { WorkingDirectory = repositoryRoot, ThrowOnNonZeroExitCode = true },
            cancellationToken);
    }

    internal static GenericCommandLineToolOptions CreateOptions(string repositoryRoot) => new("pwsh")
    {
        Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(repositoryRoot, "scripts", "Verify-PackageLocalInitialization.ps1"),
            "-RepositoryRoot", repositoryRoot, "-ReportPath",
            Path.Combine(repositoryRoot, "artifacts", "local-initialization-policy.json")]
    };
}
