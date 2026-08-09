using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularPipelines;
using ModularPipelines.Extensions;
using ModularPipelines.GitHub.Options;
using Respire.Pipeline.Modules;
using Respire.Pipeline.Modules.LocalMachine;
using Respire.Pipeline.Settings;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Pipeline.CreateBuilder(args);

        builder.Configuration.AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables();

        builder.Services.Configure<NuGetSettings>(builder.Configuration.GetSection("NuGet"))
            .Configure<GitHubSettings>(builder.Configuration.GetSection("GitHub"))
            .Configure<GitVersioningSettings>(builder.Configuration.GetSection("Versioning"));

        builder.Services.Configure<GitHubOptions>(options =>
        {
            options.AccessToken = new[]
                {
                    builder.Configuration["GitHub:AccessToken"],
                    builder.Configuration["GitHub:Token"]
                }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        });

        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddModule<CreateLocalNugetFolderModule>()
                .AddModule<AddLocalNugetSourceModule>()
                .AddModule<UploadPackagesToLocalNuGetModule>();
        }
        else
        {
            builder.Services.AddModule<UploadPackagesToNugetModule>()
                .AddModule<CreateGitHubReleaseModule>();
        }

        await builder
            .AddModule<RunUnitTestsModule>()
            .AddModule<RunBenchmarkModule>() 
            .AddModule<NugetVersionGeneratorModule>()
            .AddModule<BuildProjectsModule>()
            .AddModule<PackProjectsModule>()
            .AddModule<PackageFilesRemovalModule>()
            .AddModule<PackagePathsParserModule>()
            .ExecutePipelineAsync();
    }
}
