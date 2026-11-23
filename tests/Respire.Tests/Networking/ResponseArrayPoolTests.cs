using System.Buffers;
using System.Collections.Concurrent;
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
    private const string ProbeMode = "RESPIRE_RESPONSE_ARRAY_POOL_PROBE";
    private const string ProbeRuntime = "RESPIRE_RESPONSE_ARRAY_POOL_RUNTIME";

    [Test]
    public Task WarmPayloadBurstsDoNotAllocate()
    {
        return RunProbeAsync("payload");
    }

    [Test]
    public Task WarmElementBurstsDoNotAllocate()
    {
        return RunProbeAsync("element");
    }

    private static Task RunProbeAsync(string mode)
    {
        var start = AsyncFlushSignalTests.CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(ResponseArrayPoolTests).Assembly.Location);
        start.Environment[ProbeMode] = mode;
        start.Environment[ProbeRuntime] = Environment.Version.ToString();

        return AsyncFlushSignalTests.RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    internal static int? RunIsolatedAllocationProbe()
    {
        string? mode = Environment.GetEnvironmentVariable(ProbeMode);

        if (mode is null)
        {
            return null;
        }

        try
        {
            if (Environment.GetEnvironmentVariable(ProbeRuntime) != Environment.Version.ToString())
            {
                throw new InvalidOperationException("Response array pool probe runtime differs from parent.");
            }

            switch (mode)
            {
                case "payload":
                    CheckWarmBursts(RespirePools.CreateResponsePayloadPool());
                    break;
                case "element":
                    CheckWarmBursts(RespirePools.CreateValueArrayPool());
                    break;
                default:
                    throw new InvalidOperationException($"Unknown response array pool probe mode: {mode}.");
            }

            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckWarmBursts<T>(ArrayPool<T> pool)
    {
        var burst = new T[200][];
        Measure(pool, burst, false);
        Measure(pool, burst, true);

        long ordinary = AllocationMeasurement.WithoutConcurrentGc(() => Measure(pool, burst, false));
        long positive = AllocationMeasurement.WithoutConcurrentGc(() => Measure(pool, burst, true));

        if (ordinary != 0 || positive < 32L * 37)
        {
            throw new InvalidOperationException(
                $"Response array pool probe for {typeof(T).Name} measured {ordinary} ordinary bytes and {positive} positive-control bytes.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure<T>(ArrayPool<T> pool, T[][] burst, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; iteration < 32; iteration++)
        {
            for (var index = 0; index < burst.Length; index++)
            {
                burst[index] = pool.Rent(16);
            }

            for (var index = 0; index < burst.Length; index++)
            {
                pool.Return(burst[index]);
                burst[index] = null!;
            }

            if (allocate)
            {
                GC.KeepAlive(new byte[37]);
            }
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

        for (var index = 0; index < original.Length; index++)
        {
            original[index] = pool.Rent(length);
        }

        foreach (var array in original)
        {
            pool.Return(array);
        }

        var identities = new HashSet<T[]>(original, ReferenceEqualityComparer.Instance);
        var next = new T[original.Length][];
        var reused = 0;

        try
        {
            for (var index = 0; index < next.Length; index++)
            {
                next[index] = pool.Rent(length);

                if (identities.Contains(next[index]))
                {
                    reused++;
                }
            }

            await Assert.That(reused).IsEqualTo(capacity);
            await Assert.That(next.Distinct(ReferenceEqualityComparer.Instance).Count()).IsEqualTo(next.Length);
        }
        finally { foreach (var array in next) { if (array is not null) { pool.Return(array); } } }
    }

    [Test]
    [Arguments(1, 16)]
    [Arguments(16, 16)]
    [Arguments(17, 32)]
    [Arguments(65, 128)]
    [Arguments(129, 256)]
    [Arguments(257, 257)]
    public async Task RentsUseOnlyTheirOwnBucketOrAnExactOverMaximumArray(int requested, int expected)
    {
        var pool = new BoundedResponseArrayPool<object>(64, 128, 256);
        var first = pool.Rent(requested);
        await Assert.That(first.Length).IsEqualTo(expected);
        first[0] = new object();
        pool.Return(first, clearArray: true);
        await Assert.That(first.All(value => value is null)).IsTrue();
        var next = pool.Rent(requested);

        try
        {
            await Assert.That(ReferenceEquals(first, next)).IsEqualTo(requested <= 256);
            await Assert.That(next.All(value => value is null)).IsTrue();
        }
        finally { pool.Return(next); }
    }

    [Test]
    public async Task OverMaximumProductionPayloadsAreNeverRetained()
    {
        var pool = RespirePools.CreateResponsePayloadPool();
        var length = RespirePools.MaxPooledResponsePayloadLength + 1;
        var first = pool.Rent(length);
        await Assert.That(first.Length).IsEqualTo(length);
        pool.Return(first);
        var next = pool.Rent(length);

        try
        {
            await Assert.That(next).IsNotSameReferenceAs(first);
        }
        finally { pool.Return(next); }
    }

    [Test]
    public async Task FullAndOverMaximumBucketsStillHonorClearing()
    {
        var pool = new BoundedResponseArrayPool<object>(64, 128, 256);
        var arrays = Enumerable.Range(0, 3).Select(_ => pool.Rent(256)).ToArray();

        foreach (var array in arrays)
        {
            Array.Fill(array, new object());
            pool.Return(array, clearArray: true);
            await Assert.That(array.All(value => value is null)).IsTrue();
        }

        await CheckRetention(new BoundedResponseArrayPool<object>(64, 128, 256), 256, 1);
    }

    [Test]
    public async Task ConcurrentMixedSizeRentAndCrossThreadReturnKeepExclusiveOwnership()
    {
        var pool = new BoundedResponseArrayPool<object>(64, 128, 256);
        var owners = new ConcurrentDictionary<object[], byte>(ReferenceEqualityComparer.Instance);
        Parallel.For(0, 50, new ParallelOptions { MaxDegreeOfParallelism = 50 }, worker =>
        {
            for (var iteration = 0; iteration < 128; iteration++)
            {
                var length = 16 << ((worker + iteration) % 5);
                var array = pool.Rent(length);

                if (!owners.TryAdd(array, 0))
                {
                    throw new InvalidOperationException("A response array has two live owners.");
                }

                if (array.Any(value => value is not null))
                {
                    throw new InvalidOperationException("A returned reference was not cleared.");
                }

                array[0] = new object();

                if (!owners.TryRemove(array, out _))
                {
                    throw new InvalidOperationException("A response owner was lost.");
                }

                pool.Return(array, clearArray: true);
            }
        });
        await Assert.That(owners.IsEmpty).IsTrue();

        var held = Enumerable.Range(0, 256).Select(_ => pool.Rent(16)).ToArray();
        var identities = new HashSet<object[]>(held, ReferenceEqualityComparer.Instance);
        // A dedicated other thread returns every buffer; renting remains on this thread.
        Exception? failure = null;
        var returner = new Thread(() =>
        {
            try
            {
                foreach (var array in held)
                {
                    pool.Return(array, clearArray: true);
                }
            }
            catch (Exception error) { failure = error; }
        });
        returner.Start();
        await Assert.That(returner.Join(TimeSpan.FromSeconds(5))).IsTrue();

        if (failure is not null)
        {
            throw failure;
        }

        var rented = new object[256][];

        try
        {
            for (var index = 0; index < rented.Length; index++)
            {
                rented[index] = pool.Rent(16);
            }

            await Assert.That(rented.All(identities.Contains)).IsTrue();
            await Assert.That(rented.Distinct(ReferenceEqualityComparer.Instance).Count()).IsEqualTo(256);
        }
        finally { foreach (var array in rented) { if (array is not null) { pool.Return(array, clearArray: true); } } }
    }

    [Test]
    public async Task InvalidRequestsAndForeignBucketSizesFailBeforeChangingStorage()
    {
        var pool = new BoundedResponseArrayPool<byte>(64, 128, 256);
        await Assert.That(() => pool.Rent(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => pool.Return(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => pool.Return(new byte[17])).Throws<ArgumentException>();
        await Assert.That(pool.Rent(0)).IsSameReferenceAs(Array.Empty<byte>());
        pool.Return(Array.Empty<byte>());
        await CheckRetention(pool, 16, 256);
    }

}
