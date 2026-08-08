using BenchmarkDotNet.Attributes;
using Respire.Networking;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class LockFreeStackBenchmarks
{
    private readonly LockFreeStack<object> _pool = CreatePopulatedPool();
    private readonly LockFreeStack<object> _emptyPool = new(4096);

    [Benchmark]
    public bool RentReturn()
    {
        var popped = _pool.TryPop(out var item);
        return popped && _pool.TryPush(item!);
    }

    [Benchmark]
    public bool EmptyPop() => _emptyPool.TryPop(out _);

    private static LockFreeStack<object> CreatePopulatedPool()
    {
        var pool = new LockFreeStack<object>(4096);
        pool.TryPush(new object());
        return pool;
    }
}
