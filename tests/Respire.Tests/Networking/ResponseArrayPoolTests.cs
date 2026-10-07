using System.Buffers;
using System.Runtime.CompilerServices;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ResponseArrayPoolTests
{
    [Test]
    public async Task WarmPayloadBurstsDoNotAllocate()
        => await CheckWarmBursts(RespirePools.CreateResponsePayloadPool());

    [Test]
    public async Task WarmElementBurstsDoNotAllocate()
        => await CheckWarmBursts(RespirePools.CreateValueArrayPool());

    private static async Task CheckWarmBursts<T>(ArrayPool<T> pool)
    {
        var burst = new T[200][];
        Measure(pool, burst, false);
        Measure(pool, burst, true);
        var ordinary = AllocationMeasurement.WithoutConcurrentGc(() => Measure(pool, burst, false));
        var positive = AllocationMeasurement.WithoutConcurrentGc(() => Measure(pool, burst, true));
        await Assert.That(ordinary).IsEqualTo(0L);
        await Assert.That(positive).IsGreaterThanOrEqualTo(32L * 37);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure<T>(ArrayPool<T> pool, T[][] burst, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 32; iteration++)
        {
            for (var index = 0; index < burst.Length; index++) burst[index] = pool.Rent(16);
            for (var index = 0; index < burst.Length; index++)
            {
                pool.Return(burst[index]);
                burst[index] = null!;
            }
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [Arguments(4096, 256)]
    [Arguments(8192, 16)]
    [Arguments(2 * 1024 * 1024, 1)]
    public async Task PayloadRetentionIsBoundedBySize(int length, int capacity)
        => await CheckRetention(RespirePools.CreateResponsePayloadPool(), length, capacity);

    [Test]
    [Arguments(64, 256)]
    [Arguments(128, 16)]
    [Arguments(2048, 1)]
    public async Task ElementRetentionIsBoundedBySize(int length, int capacity)
        => await CheckRetention(RespirePools.CreateValueArrayPool(), length, capacity);

    private static async Task CheckRetention<T>(ArrayPool<T> pool, int length, int capacity)
    {
        var original = new T[capacity + 2][];
        for (var index = 0; index < original.Length; index++) original[index] = pool.Rent(length);
        foreach (var array in original) pool.Return(array);
        var identities = new HashSet<T[]>(original, ReferenceEqualityComparer.Instance);
        var next = new T[original.Length][];
        var reused = 0;
        try
        {
            for (var index = 0; index < next.Length; index++)
            {
                next[index] = pool.Rent(length);
                if (identities.Contains(next[index])) reused++;
            }
            await Assert.That(reused).IsEqualTo(capacity);
            await Assert.That(next.Distinct(ReferenceEqualityComparer.Instance).Count()).IsEqualTo(next.Length);
        }
        finally { foreach (var array in next) if (array is not null) pool.Return(array); }
    }

}
