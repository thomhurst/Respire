using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
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
        var expected = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key == "ExpectedRuntimeAssembly")
            .Select(attribute => attribute.Value!)
            .ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0)
            throw new InvalidOperationException("No expected runtime assemblies were generated from package references.");

        var paths = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Where(path => Path.GetFileName(path).StartsWith("Respire", StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith("Aspire.Respire", StringComparison.Ordinal))
            .Where(path => Path.GetFileNameWithoutExtension(path) != typeof(Program).Assembly.GetName().Name)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);
        var missing = expected.Except(paths.Keys, StringComparer.Ordinal).Order().ToArray();
        var unexpected = paths.Keys.Except(expected, StringComparer.Ordinal).Order().ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
            throw new InvalidOperationException(
                $"Runtime assembly set mismatch. Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", unexpected)}].");

        // Inspect the actual NuGet runtime assets, including packages that a snippet
        // does not use directly. The analyzer is a compiler asset, not a runtime library.
        foreach (var (name, path) in paths)
        {
            var assembly = System.Reflection.Assembly.LoadFrom(path);
            if (assembly.GetName().Name != name)
                throw new InvalidOperationException($"Assembly name does not match {path}.");

            var publicTypes = assembly.GetExportedTypes();
            // Aspire companions expose host extensions in Microsoft's conventional namespace.
            // Keep this exception limited to the four explicitly audited integration packages.
            var aspirePackage = name is "Aspire.Respire" or "Aspire.Respire.DistributedCaching"
                or "Aspire.Respire.HybridCaching" or "Aspire.Respire.OutputCaching";
            if (!publicTypes.Any(type => type.Namespace == name
                || (aspirePackage && IsHostExtension(type))))
                throw new InvalidOperationException($"{name} has no public types in its root namespace.");

            foreach (var type in publicTypes)
            {
                if (type.Namespace != name && !(type.Namespace?.StartsWith(name + ".", StringComparison.Ordinal) ?? false)
                    && !(aspirePackage && IsHostExtension(type)))
                    throw new InvalidOperationException($"{type.FullName} is outside the {name} namespace hierarchy.");
            }
        }
    }

    private static bool IsHostExtension(Type type)
        => type.Namespace == "Microsoft.Extensions.Hosting" && type.IsAbstract && type.IsSealed
            && type.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), inherit: false);
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
    protected static readonly HostApplicationBuilder builder = Host.CreateApplicationBuilder();
    protected static readonly DocumentationHealthState healthState = new();
    protected static readonly RespireMessage message = default;

    protected static void Process(ReadOnlySpan<byte> _) { }

    protected static ValueTask HandleAsync(string? _) => ValueTask.CompletedTask;

    protected static ValueTask InspectAsync(string _) => ValueTask.CompletedTask;

    protected static ValueTask ProcessAsync(string _, CancellationToken __) => ValueTask.CompletedTask;

    protected static ValueTask RunReportAsync(CancellationToken _ = default) => ValueTask.CompletedTask;
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
