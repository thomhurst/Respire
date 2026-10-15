using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.PublicApi.Tests;

public class PublicTypeNamingTests
{
    // Host-discovered analyzer/code-fix types are tooling, not native client API types.
    // Keep this list explicit so adding an exception requires a deliberate review.
    private static readonly HashSet<string> Exceptions = new(StringComparer.Ordinal)
    {
        "Respire.Analyzers.BlockingSubscriptionHandlerAnalyzer",
        "Respire.Analyzers.DiagnosticIds",
        "Respire.Analyzers.PendingReadBeforeFlushAnalyzer",
        "Respire.Analyzers.PendingResultSuppressor",
        "Respire.Analyzers.ShortLivedClientAnalyzer",
        "Respire.Analyzers.StackExchangeMigrationAnalyzer",
        "Respire.Analyzers.StackExchangeMigrationCodeFixProvider",
        "Respire.Analyzers.SyncOverAsyncAnalyzer",
        "Respire.Analyzers.SyncOverAsyncCodeFixProvider",
        "Respire.Analyzers.UnboundedReadAnalyzer",
        "Respire.Analyzers.UndisposedPooledResultAnalyzer",
        "Respire.Analyzers.UndisposedPooledResultCodeFixProvider",
        "Aspire.Respire.AspireRespireClientBuilder",
        "Microsoft.Extensions.Hosting.AspireRespireExtensions",
        "Microsoft.Extensions.Hosting.AspireRespireDistributedCachingExtensions",
        "Microsoft.Extensions.Hosting.AspireRespireHybridCachingExtensions",
        "Microsoft.Extensions.Hosting.AspireRespireOutputCachingExtensions",
    };

    [Test]
    public async Task ShippingPublicTypesUseNativePrefix()
    {
        var types = GetShippingTypes();
        var invalid = types.Where(type => !HasNativePrefix(type.Name, type.IsInterface)
                                         && !Exceptions.Contains(type.FullName!))
            .Select(type => type.FullName).Order().ToArray();
        await Assert.That(invalid).IsEmpty();
        // An exception must still identify a real top-level public type.
        await Assert.That(Exceptions.Except(types.Select(type => type.FullName!)).ToArray()).IsEmpty();
    }

    [Test]
    [Arguments("RespireStreamReadOptions", false, true)]
    [Arguments("IRespireStringCommands", true, true)]
    [Arguments("IStringCommands", true, false)]
    [Arguments("RespProtocol", false, false)]
    [Arguments("StreamReadOptions", false, false)]
    [Arguments("SubscriptionOverflow", false, false)]
    public async Task PrefixRuleRejectsLegacyAndGenericNames(string name, bool isInterface, bool expected)
        => await Assert.That(HasNativePrefix(name, isInterface)).IsEqualTo(expected);

    [Test]
    public async Task CommandOutcomeWrappersUseResultSuffix()
    {
        Type[] wrappers =
        [
            typeof(RespireGetResult<>), typeof(RespireTtlResult), typeof(RespireExpiryTimeResult),
            typeof(Json.RespireJsonGetResult<>), typeof(RespireArrayAggregateResult),
            typeof(RespireSortedSetRankResult), typeof(RespireGeoSearchResult),
            typeof(RespireIncrementResult<>), typeof(RespireListPopResult), typeof(RespireAofAcknowledgementResult),
        ];
        await Assert.That(wrappers.All(type => type.Name.Split('`')[0].EndsWith("Result", StringComparison.Ordinal))).IsTrue();
        string[] legacy = ["RespireGet`1", "RespireTtl", "RespireExpiryTime", "RespireJsonValue`1", "RespireArrayAggregate", "SortedSetRank", "RespireAofAcknowledgement"];
        await Assert.That(GetShippingTypes().Select(type => type.Name).Intersect(legacy).ToArray()).IsEmpty();
    }

    private static bool HasNativePrefix(string name, bool isInterface) =>
        name.StartsWith(isInterface ? "IRespire" : "Respire", StringComparison.Ordinal);

    private static Type[] GetShippingTypes()
    {
        using var manifest = typeof(PublicTypeNamingTests).Assembly.GetManifestResourceStream("ShippingAssemblies")
                             ?? throw new InvalidOperationException("The shipping assembly manifest is missing.");
        using var reader = new StreamReader(manifest);
        var names = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) throw new InvalidOperationException("The shipping inventory must not be empty.");
        return names.Select(name => System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, name + ".dll")))
            .SelectMany(assembly => assembly.GetExportedTypes()).Where(type => !type.IsNested).ToArray();
    }
}
