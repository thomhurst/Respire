using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FacetInterfaceContractTests
{
    [Test]
    [Arguments(typeof(IRespireClient))]
    [Arguments(typeof(IRespireCommandQueue))]
    public async Task OptionalArrayFacetDoesNotRequireExistingImplementations(Type contract)
    {
        var getter = contract.GetProperty(nameof(IRespireClient.Arrays))!.GetMethod!;
        await Assert.That(getter.IsAbstract).IsFalse();
    }

    [Test]
    public async Task LegacyQueueWithoutArrayFacetUsesUnsupportedDefault()
    {
        IRespireCommandQueue queue = new LegacyQueue();
        await Assert.That(() => _ = queue.Arrays).Throws<NotSupportedException>()
            .WithMessage("This queue does not support Redis arrays.");
    }

    // This compile control implements the pre-array contract and deliberately omits Arrays.
    private sealed class LegacyQueue : IRespireCommandQueue
    {
        public IBatchStringCommands Strings => throw new NotSupportedException();
        public IBatchKeyCommands Keys => throw new NotSupportedException();
        public IBatchHashCommands Hashes => throw new NotSupportedException();
        public IBatchListCommands Lists => throw new NotSupportedException();
        public IBatchSetCommands Sets => throw new NotSupportedException();
        public IBatchSortedSetCommands SortedSets => throw new NotSupportedException();
        public IBatchBitmapCommands Bitmaps => throw new NotSupportedException();
        public IBatchHyperLogLogCommands HyperLogLog => throw new NotSupportedException();
        public IBatchGeoCommands Geo => throw new NotSupportedException();
        public IBatchVectorSetCommands VectorSets => throw new NotSupportedException();
        public IBatchScriptCommands Scripts => throw new NotSupportedException();
        public IBatchStreamCommands Streams => throw new NotSupportedException();
        public RespirePending<RespireResult> Execute(RespireCommand command, params RespireValue[] args) => throw new NotSupportedException();
        public RespirePending<string?> GetString(RespireKey key) => throw new NotSupportedException();
        public RespirePending<T?> Get<T>(RespireKey key) => throw new NotSupportedException();
        public RespirePending<RespireGet<T>> TryGet<T>(RespireKey key) => throw new NotSupportedException();
        public RespirePending<byte[]?> GetBytes(RespireKey key) => throw new NotSupportedException();
        public RespirePending<bool> Set(RespireKey key, RespireValue value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always)
            => throw new NotSupportedException();
        public RespirePending<bool> Set<T>(RespireKey key, T value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always)
            => throw new NotSupportedException();
        public RespirePending<long> Delete(params ReadOnlySpan<RespireKey> keys) => throw new NotSupportedException();
        public RespirePending<bool> Exists(RespireKey key) => throw new NotSupportedException();
        public RespirePending<long> Increment(RespireKey key, long by = 1) => throw new NotSupportedException();
        public RespirePending<long> Decrement(RespireKey key, long by = 1) => throw new NotSupportedException();
        public RespirePending<bool> Expire(RespireKey key, RespireExpiry expiry, ExpireWhen when = ExpireWhen.Always)
            => throw new NotSupportedException();
    }

    [Test]
    public async Task SubscriptionOptions_RequireExplicitImplementations()
    {
        var methods = typeof(IRespireClient).GetMethods()
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(RespireSubscriptionOptions)))
            .ToArray();

        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods.All(method => method.IsAbstract)).IsTrue();
    }

    [Test]
    [Arguments(typeof(IServerCommands))]
    [Arguments(typeof(IBatchServerCommands))]
    [Arguments(typeof(IHashCommands))]
    [Arguments(typeof(IArrayCommands))]
    [Arguments(typeof(IBatchArrayCommands))]
    [Arguments(typeof(IBatchHashCommands))]
    [Arguments(typeof(ISortedSetCommands))]
    [Arguments(typeof(IBatchSortedSetCommands))]
    [Arguments(typeof(IStreamCommands))]
    [Arguments(typeof(IStringCommands))]
    [Arguments(typeof(IBatchStringCommands))]
    public async Task CommandContracts_RequireImplementations(Type contract)
    {
        var defaults = contract.GetMethods()
            .Where(method => !method.IsAbstract)
            .Select(method => method.ToString())
            .ToArray();

        await Assert.That(defaults).IsEmpty();
    }
}
