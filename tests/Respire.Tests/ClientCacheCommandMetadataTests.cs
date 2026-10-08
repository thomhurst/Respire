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
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamReadMetadataMatchesInvocationKind(bool group)
    {
        var command = new StreamReadCommand(["key"], ["0-0"], null, null,
            group: group ? "group" : null, consumer: group ? "consumer" : null);
        var operation = group ? "XREADGROUP" : "XREAD";
        var expected = ClientCacheCommandMetadata.Get(operation);
        var actual = command.GetClientCacheMetadata(operation);
        await Assert.That(actual.IsInitialized).IsTrue();
        await Assert.That(actual.Policy).IsEqualTo(expected.Policy);
        await Assert.That(actual.ArgumentLayout).IsEqualTo(expected.ArgumentLayout);
        await Assert.That(actual.MutationKind).IsEqualTo(expected.MutationKind);
        await Assert.That(actual.CacheableRead).IsEqualTo(expected.CacheableRead);
        await Assert.That(command.GetCacheMutation(operation)).IsEqualTo(expected.Policy);
    }

    [Test]
    [Arguments("SCRIPT  LOAD", 2, "$6\r\nSCRIPT\r\n$4\r\nLOAD\r\n")]
    [Arguments("  CLIENT   LIST  ", 2, "$6\r\nCLIENT\r\n$4\r\nLIST\r\n")]
    [Arguments("ABCDEFGHIJ", 1, "$10\r\nABCDEFGHIJ\r\n")]
    public async Task SharedTokenizerPreservesWireBytes(string command, int count, string expected)
    {
        var verb = new Verb(command);
        await Assert.That(verb.Tokens).IsEqualTo(count);
        await Assert.That(System.Text.Encoding.ASCII.GetString(verb.Bulk)).IsEqualTo(expected);
    }

    [Test]
    public async Task CopiedVerbsPublishCompleteMetadataToConcurrentFirstUsers()
    {
        var verb = new Verb("ZUNIONSTORE");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(0, 64).Select(async _ =>
        {
            var copy = new Cmd(verb);
            await start.Task;
            return copy.GetClientCacheMetadata("ZUNIONSTORE");
        }).ToArray();
        start.SetResult();
        foreach (var value in await Task.WhenAll(readers))
        {
            await Assert.That(value.IsInitialized).IsTrue();
            await Assert.That(value.Policy).IsEqualTo(CommandCacheMutationMetadata.Get("ZUNIONSTORE"));
            await Assert.That(value.ArgumentLayout).IsEqualTo(RawCommandKeyLayouts.LayoutKind.CountedWithDestination);
            await Assert.That(value.MutationKind).IsEqualTo(RawCommandKeyLayouts.MutationKind.FirstArgument);
            await Assert.That(value.CacheableRead).IsFalse();
        }
        await Assert.That(default(Verb).CacheMetadata.IsInitialized).IsFalse();
    }

    [Test]
    [NotInParallel]
    [Arguments("verbs")]
    [Arguments("stream-read")]
    public async Task CacheDisabledCommandsLeaveCacheTablesColdUntilFirstClassification(string firstCommand)
    {
        // Warm the measurement and reflection machinery in a separate collectible context.
        using var warm = new ColdMetadataContext(firstCommand);
        _ = MeasureFirstAndRepeatedClassification(warm.GetMetadata);
        _ = MeasureFirstAndRepeatedClassification(warm.GetMetadata);
        using var cold = new ColdMetadataContext(firstCommand);
        var result = AllocationMeasurement.WithoutConcurrentGc(() =>
            MeasureFirstAndRepeatedClassification(cold.GetMetadata));
        await Assert.That(result.First - result.Repeated).IsGreaterThan(16_384);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long First, long Repeated) MeasureFirstAndRepeatedClassification(MethodInfo method)
    {
        object[] arguments = ["SET"];
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = method.Invoke(null, arguments);
        var first = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        _ = method.Invoke(null, arguments);
        return (first, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class ColdMetadataContext : IDisposable
    {
        private readonly AssemblyLoadContext _context = new($"cold-cache-{Guid.NewGuid()}", isCollectible: true);
        internal MethodInfo GetMetadata { get; }

        internal ColdMetadataContext(string firstCommand)
        {
            var assembly = _context.LoadFromAssemblyPath(typeof(RespireClient).Assembly.Location);
            if (firstCommand == "verbs")
            {
                // Materialize all normal typed verbs without invoking a cache-specific API.
                _ = assembly.GetType("Respire.Commands.Verbs")!.GetField("Set")!.GetValue(null);
            }
            else
            {
                var stream = assembly.GetType("Respire.Commands.StreamReadCommand")!;
                _ = stream.GetProperty("ReadKind")!.GetValue(Activator.CreateInstance(stream));
            }
            GetMetadata = assembly.GetType("Respire.Commands.ClientCacheCommandMetadata")!
                .GetMethod("Get", BindingFlags.Static | BindingFlags.NonPublic)!;
        }

        public void Dispose() => _context.Unload();
    }

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
                var metadata = verb.GetType().GetProperty("CacheMetadata")!.GetValue(verb)!;
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
        await Assert.That(ClientCacheCommandMetadata.GetForWireVerb("script flush async").Policy)
            .IsEqualTo(Verbs.ScriptFlush.CacheMetadata.Policy);
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
