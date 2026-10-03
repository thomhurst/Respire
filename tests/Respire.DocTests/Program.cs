using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Respire;

namespace Respire.DocTests;

internal static class Program
{
    public static void Main()
    {
        VerifyPackageNamespaces();
        SnippetCatalog.Report();
    }

    private static void VerifyPackageNamespaces()
    {
        // Inspect the actual NuGet runtime assets, including packages that a snippet
        // does not use directly. The analyzer is a compiler asset, not a runtime library.
        foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "Respire*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == typeof(Program).Assembly.GetName().Name)
                continue;

            var assembly = System.Reflection.Assembly.LoadFrom(path);
            if (assembly.GetName().Name != name)
                throw new InvalidOperationException($"Assembly name does not match {path}.");

            var publicTypes = assembly.GetExportedTypes();
            if (!publicTypes.Any(type => type.Namespace == name))
                throw new InvalidOperationException($"{name} has no public types in its root namespace.");

            foreach (var type in publicTypes)
            {
                if (type.Namespace != name && !(type.Namespace?.StartsWith(name + ".", StringComparison.Ordinal) ?? false))
                    throw new InvalidOperationException($"{type.FullName} is outside the {name} namespace hierarchy.");
            }
        }
    }
}

#pragma warning disable CS0162, CS0169, CS0219, CS0414, CS0649, CS1998

internal abstract class SnippetContext
{
    protected static readonly IRespireClient redis = null!;
    protected static readonly RespireLock mutex = null!;
    protected static readonly CancellationToken cancellationToken = default;
    protected static readonly CancellationToken stoppingToken = default;
    protected static readonly CancellationToken token = default;
    protected static readonly string requestId = "request";
    protected static readonly string payload = "{}";
    protected static readonly string json = "{}";
    protected static readonly string orderJson = "{}";
    protected static readonly string sessionId = "session";
    protected static readonly RespireKey[] keys = ["key"];
    protected static readonly long userId = 1;
    protected static readonly DateTimeOffset midnight = DateTimeOffset.UtcNow.AddDays(1);
    protected static readonly RespireOptions options = new();
    protected static readonly IConfiguration configuration = new ConfigurationBuilder().Build();
    protected static readonly ILogger logger = NullLogger.Instance;
    protected static readonly ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
    protected static readonly DocumentationBuilder builder = new();
    protected static readonly DocumentationHealthState healthState = new();
    protected static readonly RespireMessage message = default;

    protected static void Process(ReadOnlySpan<byte> _) { }

    protected static ValueTask HandleAsync(string? _) => ValueTask.CompletedTask;

    protected static ValueTask InspectAsync(string _) => ValueTask.CompletedTask;

    protected static ValueTask ProcessAsync(string _, CancellationToken __) => ValueTask.CompletedTask;

    protected static ValueTask RunReportAsync(CancellationToken _ = default) => ValueTask.CompletedTask;
}

internal sealed class DocumentationBuilder
{
    public IServiceCollection Services { get; } = new ServiceCollection();

    public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
}

internal sealed class DocumentationHealthState
{
    public void Update(RespireEndpoint _, RespireConnectionState __, Exception? ___) { }
}

internal sealed record User(string Name = "Ada", int Age = 36);

internal sealed record Order;

internal sealed record OrderCreated;

internal sealed record CachedJob;

internal sealed record Session;
