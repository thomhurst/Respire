using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Respire.Commands;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheCommandMetadataTests
{
    [Test]
    [Arguments("verb")]
    [Arguments("layout")]
    [Arguments("policy")]
    [Arguments("catalog")]
    public async Task ColdInitializationWorksFromEveryMetadataEntryPoint(string first)
    {
        var context = new AssemblyLoadContext($"cache-metadata-{Guid.NewGuid()}", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(RespireClient).Assembly.Location);
            if (first == "verb")
                assembly.GetType("Respire.Commands.Verb")!.GetConstructor([typeof(string), typeof(bool)])!
                    .Invoke(["SET", true]);
            else if (first == "layout")
                assembly.GetType("Respire.Commands.RawCommandKeyLayouts")!
                    .GetMethod("GetMutationKind", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["SET"]);
            else if (first == "policy")
                assembly.GetType("Respire.CommandCacheMutationMetadata")!
                    .GetMethod("Get", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["SET"]);
            var root = assembly.GetType("Respire.RespireCommands")!;
            var count = 0;
            foreach (var group in root.GetNestedTypes(BindingFlags.Public))
            foreach (var field in group.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var descriptor = field.GetValue(null)!;
                var type = descriptor.GetType();
                var name = (string)type.GetProperty("Name")!.GetValue(descriptor)!;
                await Assert.That(string.IsNullOrWhiteSpace(name)).IsFalse();
                var verb = type.GetProperty("Verb", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(descriptor)!;
                var metadata = verb.GetType().GetField("CacheMetadata")!.GetValue(verb)!;
                var policy = metadata.GetType().GetProperty("Policy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadata)!;
                await Assert.That(policy.ToString()).IsEqualTo(type.GetProperty("CacheMutation")!.GetValue(descriptor)!.ToString()).Because(name);
                count++;
            }
            await Assert.That(count).IsEqualTo(RespireCommands.All.Length);
        }
        finally { context.Unload(); }
    }

    [Test]
    public async Task EveryCatalogAndRegisteredLayoutRetainsItsCacheClassification()
    {
        foreach (var descriptor in RespireCommands.All.ToArray())
        {
            var metadata = descriptor.Verb.CacheMetadata;
            await Assert.That(metadata.IsInitialized).IsTrue().Because(descriptor.Name);
            await Assert.That(metadata.Policy).IsEqualTo(descriptor.CacheMutation).Because(descriptor.Name);
            await Assert.That(metadata.CacheableRead).IsEqualTo(ClientSideCacheCoordinator.CanCacheOperation(descriptor.Name)).Because(descriptor.Name);
            await Assert.That(metadata.MutationKind).IsEqualTo(RawCommandKeyLayouts.GetMutationKind(descriptor.Name)).Because(descriptor.Name);
            var typed = new CmdN(descriptor.Verb, []);
            await Assert.That(typed.GetCacheMutation(descriptor.Name)).IsEqualTo(metadata.Policy).Because(descriptor.Name);
        }
        foreach (var entry in RawCommandKeyLayouts.MutationClassifications)
        {
            var metadata = new Verb(entry.Operation).CacheMetadata;
            await Assert.That(metadata.MutationKind).IsEqualTo(entry.Mutation).Because(entry.Operation);
        }
        await Assert.That(Unsafe.SizeOf<ClientCacheCommandMetadata>()).IsEqualTo(4);
    }

    [Test]
    public async Task FixedOptionVerbsAndLowercaseNamesRetainCanonicalPolicies()
    {
        foreach (var verb in new[] { Verbs.ScriptFlush, Verbs.ScriptFlushSync, Verbs.ScriptFlushAsync })
        {
            var command = new Cmd(verb);
            await Assert.That(command.GetCacheMutation("SCRIPT FLUSH")).IsEqualTo(RespireCacheMutation.ReadOnly);
        }
        await Assert.That(new Verb("get").CacheMetadata.CacheableRead).IsTrue();
        await Assert.That(new Verb("set").CacheMetadata.Policy).IsEqualTo(RespireCacheMutation.Mutation);
        var custom = new Cmd1(new Verb("CUSTOM.READ"), "key");
        await Assert.That(custom.GetClientCacheMetadata("CUSTOM.READ").CacheableRead).IsFalse();
        await Assert.That(custom.GetCacheMutation("CUSTOM.READ")).IsEqualTo(RespireCacheMutation.Unknown);
    }

    [Test]
    [Arguments(RespireCacheMutation.Unknown)]
    [Arguments(RespireCacheMutation.ReadOnly)]
    [Arguments(RespireCacheMutation.SingleKey)]
    [Arguments(RespireCacheMutation.MultiKey)]
    public async Task ExplicitCatalogAndDynamicPoliciesOverrideInferredPolicy(RespireCacheMutation policy)
    {
        var descriptor = RespireCommand.Create("get", policy);
        var catalog = new CatalogCommand(descriptor, ["key"]);
        var dynamic = new DynamicCommand(["GET", "key"], 1, cacheMutation: policy,
            hasExplicitCacheMutation: true, cacheMetadata: descriptor.Verb.CacheMetadata);
        await Assert.That(catalog.GetCacheMutation("GET")).IsEqualTo(policy);
        await Assert.That(dynamic.GetCacheMutation("GET")).IsEqualTo(policy);
        // Existing read eligibility is independent of the caller's mutation declaration.
        await Assert.That(catalog.GetClientCacheMetadata("GET").CacheableRead).IsTrue();
        await Assert.That(dynamic.GetClientCacheMetadata("GET").CacheableRead).IsTrue();
    }

    [Test]
    public async Task CatalogOperationMismatchAndUnpreparedDynamicCommandsUseConservativeFallback()
    {
        var catalog = new CatalogCommand(RespireCommands.String.GET, ["key"]);
        await Assert.That(catalog.GetCacheMutation("SET")).IsEqualTo(RespireCacheMutation.Mutation);
        await Assert.That(catalog.GetClientCacheMetadata("SET").CacheableRead).IsFalse();
        var dynamic = new DynamicCommand(["CUSTOM.WRITE", "key"], 1);
        var cache = new ClientSideCacheCoordinator(new());
        var fence = cache.BeforeCommand("CUSTOM.WRITE", in dynamic);
        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.All);
        cache.CompleteMutation(in fence);
    }

    [Test]
    [Arguments("HELLO")]
    [Arguments("RESET")]
    [Arguments("SELECT")]
    [Arguments("CLIENT CACHING")]
    [Arguments("CLIENT TRACKING")]
    public async Task ProtocolAndTrackingDisruptionCannotBeOverriddenByReadOnlyPolicy(string operation)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var command = new DynamicCommand([operation, "key"], 1,
            cacheMutation: RespireCacheMutation.ReadOnly, hasExplicitCacheMutation: true,
            cacheMetadata: ClientCacheCommandMetadata.Get(operation));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
        {
            cache.BeforeCommand(operation, in command);
            return Task.CompletedTask;
        });
    }

    [Test]
    [Arguments("TRACKING")]
    [Arguments("CACHING")]
    public async Task ClientSubcommandsRemainArgumentDependent(string subcommand)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var command = new DynamicCommand(["CLIENT", subcommand.ToLowerInvariant(), "ON"], -1,
            cacheMetadata: ClientCacheCommandMetadata.Get("CLIENT"));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
        {
            cache.BeforeCommand("CLIENT", in command);
            return Task.CompletedTask;
        });
        var harmless = new DynamicCommand(["CLIENT", "LIST"], -1, cacheMutation: RespireCacheMutation.ReadOnly,
            hasExplicitCacheMutation: true, cacheMetadata: ClientCacheCommandMetadata.Get("CLIENT"));
        await Assert.That(cache.BeforeCommand("CLIENT", in harmless).IsRequired).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MalformedCountedDestinationFallsBackToFullCacheEvenWithExplicitMultiKey(bool explicitMultiKey)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var command = new DynamicCommand(["ZUNIONSTORE", "destination", long.MaxValue, "source"], 1,
            cacheMutation: explicitMultiKey ? RespireCacheMutation.MultiKey : RespireCacheMutation.Unknown,
            hasExplicitCacheMutation: explicitMultiKey, cacheMetadata: Verbs.ZUnionStore.CacheMetadata);
        var fence = cache.BeforeCommand("ZUNIONSTORE", in command);
        await Assert.That(fence.Kind).IsEqualTo(ClientSideCacheCoordinator.MutationFenceKind.All);
        cache.CompleteMutation(in fence);
    }

    [Test]
    [NotInParallel]
    public async Task WarmTypedCacheClassificationAllocatesNothingWithPositiveControl()
    {
        _ = MeasureClassification(false);
        _ = MeasureClassification(true);
        var measurements = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Normal: MeasureClassification(false), Control: MeasureClassification(true)));
        await Assert.That(measurements.Normal.Bytes).IsEqualTo(0);
        await Assert.That(measurements.Control.Bytes).IsGreaterThanOrEqualTo(37_000);
        await Assert.That(measurements.Normal.Total).IsEqualTo(10_000);
        await Assert.That(measurements.Control.Total).IsEqualTo(measurements.Normal.Total);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Bytes, int Total) MeasureClassification(bool allocate)
    {
        var set = new SetCommand("key", "value", default, SetWhen.Always, false);
        var get = new Cmd1(Verbs.Get, "key");
        // Keep command argument allocation outside the measured interval.
        var del = new CmdN(Verbs.Del, ["a", "b"]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        for (var index = 0; index < 1_000; index++)
        {
            total += Classify(in set, "SET") + Classify(in get, "GET") + Classify(in del, "DEL");
            if (get.GetClientCacheMetadata("GET").CacheableRead) total++;
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, total);
    }

    private static int Classify<TCommand>(in TCommand command, string operation) where TCommand : struct, IRespCommand
        => (int)command.GetCacheMutation(operation);
}
