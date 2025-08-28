using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularPipelines.Extensions;
using ModularPipelines.Host;
using Respire.Pipeline.Modules;
using Respire.Pipeline.Modules.LocalMachine;
using Respire.Pipeline.Settings;

public class Program
{
    public static async Task Main(string[] args)
    {
        await PipelineHostBuilder.Create()
            .ConfigureAppConfiguration((_, builder) =>
            {
                builder.AddJsonFile("appsettings.json", optional: true)
                    .AddUserSecrets<Program>()
                    .AddEnvironmentVariables();
            })
            .ConfigureServices((context, collection) =>
            {
                collection.Configure<NuGetSettings>(context.Configuration.GetSection("NuGet"))
                    .Configure<GitHubSettings>(context.Configuration.GetSection("GitHub"));

                if (context.HostingEnvironment.IsDevelopment())
                {
                    collection.AddModule<CreateLocalNugetFolderModule>()
                        .AddModule<AddLocalNugetSourceModule>()
                        .AddModule<UploadPackagesToLocalNuGetModule>();
                }
                else
                {
                    collection.AddModule<UploadPackagesToNugetModule>()
                        .AddModule<CreateGitHubReleaseModule>();
                }
            })
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