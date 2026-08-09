using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<RunBenchmarkModule>]
public class PackProjectsModule : Module<CommandResult[]>
{
    private readonly IConfiguration _configuration;

    public PackProjectsModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Packing NuGet packages...");

        var packageProjects = _configuration.GetSection("PackageProjects").Get<string[]>() ?? new[]
        {
            "../src/Respire/Respire.csproj",
            "../src/Respire.Extensions.DependencyInjection/Respire.Extensions.DependencyInjection.csproj"
        };

        var version = NugetVersionGeneratorModule.GetGeneratedVersion();
        var results = new List<CommandResult>();
        
        foreach (var project in packageProjects)
        {
            var result = await context.DotNet().Pack(new DotNetPackOptions
            {
                ProjectSolution = project,
                Configuration = "Release",
                NoBuild = true,
                Output = "../artifacts/packages",
                Properties = NugetVersionGeneratorModule.CreateVersionProperties(version)
            }, cancellationToken: cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}
