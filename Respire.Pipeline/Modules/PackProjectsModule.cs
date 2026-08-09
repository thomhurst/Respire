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

        var version = Environment.GetEnvironmentVariable("KEVA_VERSION") ?? "1.0.0-dev";
        var results = new List<CommandResult>();
        
        foreach (var project in packageProjects)
        {
            var result = await context.DotNet().Pack(new DotNetPackOptions
            {
                ProjectSolution = project,
                Configuration = "Release",
                NoBuild = true,
                Output = "../artifacts/packages",
                Properties = new[]
                {
                    new KeyValue("Version", version),
                    new KeyValue("PackageVersion", version),
                    new KeyValue("AssemblyVersion", version.Split('-')[0]), // Remove pre-release suffix for assembly version
                    new KeyValue("FileVersion", version.Split('-')[0])
                }
            }, cancellationToken: cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}
