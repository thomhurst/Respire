using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Enums;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Keva.Pipeline.Modules;

[DependsOn<RunBenchmarkModule>]
public class PackProjectsModule : Module<CommandResult[]>
{
    protected override async Task<CommandResult[]> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Packing NuGet packages...");

        var packageProjects = context.Configuration.GetSection("PackageProjects").Get<string[]>() ?? new[]
        {
            "../src/Keva/Keva.csproj",
            "../src/Keva.Extensions.DependencyInjection/Keva.Extensions.DependencyInjection.csproj"
        };

        var version = Environment.GetEnvironmentVariable("KEVA_VERSION") ?? "1.0.0-dev";
        var results = new List<CommandResult>();
        
        foreach (var project in packageProjects)
        {
            var result = await context.DotNet().Pack(new DotNetPackOptions
            {
                ProjectSolution = project,
                Configuration = Configuration.Release,
                NoBuild = true,
                OutputDirectory = "../artifacts/packages",
                Properties = new[]
                {
                    new KeyValue("Version", version),
                    new KeyValue("PackageVersion", version),
                    new KeyValue("AssemblyVersion", version.Split('-')[0]), // Remove pre-release suffix for assembly version
                    new KeyValue("FileVersion", version.Split('-')[0])
                }
            }, cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}