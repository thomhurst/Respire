using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheCommandSnapshotTests
{
    [Test]
    [NotInParallel]
    public async Task SmallImmutableSnapshotsAllocateNothingWithLargeSnapshotPositiveControl()
    {
        ClientCacheCommandKey[] small =
        [
            new("STRLEN"), new("STRLEN", "key"), new("HGET", "key", "field"),
            new("LRANGE", "key", 0, -1), new("ZRANGE", "key", 0, -1, "WITHSCORES"),
            new("ZRANGE", "key", 0, -1, "BYSCORE", "WITHSCORES"),
        ];
        var large = new ClientCacheCommandKey("MGET", new RespireValue[] { "1", "2", "3", "4", "5", "6" });
        for (var index = 0; index < 32; index++)
        {
            foreach (var key in small) Measure(in key);
            Measure(in large);
        }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
        {
            long actual = 0;
            foreach (var key in small) actual += Measure(in key);
            return (Actual: actual, Control: Measure(in large));
        });
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThan(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(in ClientCacheCommandKey key)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = key.Snapshot();
        // Consume the identity without boxing or keeping caller-owned arrays alive.
        if (!snapshot.Equals(key)) throw new InvalidOperationException("Snapshot identity changed.");
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    public async Task SnapshotOwnsBinaryArgumentsAndNormalizesTokenOffsets(int count)
    {
        var bytes = new byte[count][];
        var tokens = new RespireValue[count + 2];
        tokens[0] = "COMMAND";
        tokens[1] = "OPTION";
        for (var index = 0; index < count; index++)
        {
            bytes[index] = [(byte)index, 255];
            tokens[index + 2] = bytes[index];
        }
        var original = new ClientCacheCommandKey("MGET", tokens, 2);
        var snapshot = original.Snapshot();
        var expected = snapshot.Snapshot();
        var hash = snapshot.GetHashCode();
        await Assert.That(snapshot.Equals(original)).IsTrue();
        await Assert.That(hash).IsEqualTo(original.GetHashCode());
        for (var index = 0; index < count; index++) bytes[index][0] = 99;
        Array.Fill(tokens, (RespireValue)"changed");
        await Assert.That(snapshot.Count).IsEqualTo(count);
        await Assert.That(snapshot.ArgumentCount).IsEqualTo(count);
        await Assert.That(snapshot.Equals(expected)).IsTrue();
        await Assert.That(snapshot.GetHashCode()).IsEqualTo(hash);
        for (var index = 0; index < count; index++)
            await Assert.That(snapshot.GetArgument(index).Equals((RespireValue)new byte[] { (byte)index, 255 })).IsTrue();
    }
}
