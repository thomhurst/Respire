using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace Respire.Pipeline.Modules;

[DependsOn<BuildProjectsModule>]
public class RunUnitTestsModule : Module<CommandResult[]>
{
    private readonly IConfiguration _configuration;

    public RunUnitTestsModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Running unit tests...");

        var testProjects = _configuration.GetSection("TestProjects").Get<string[]>() ?? new[]
        {
            "../tests/Respire.Tests/Respire.Tests.csproj"
        };

        var results = new List<CommandResult>();
        
        foreach (var project in testProjects)
        {
            var result = await context.DotNet().Test(new DotNetTestOptions
            {
                Project = project,
                Configuration = "Release",
                NoBuild = true
            }, cancellationToken: cancellationToken);
            
            results.Add(result);
        }

        return results.ToArray();
    }
}
