using BenchmarkDotNet.Attributes;
using Reservoir;
using Respire.Networking;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
public class LockFreeStackBenchmarks
{
    private const int MaximumRetained = 4096;

    private readonly LockFreeStack<PoolBenchmarkItem> _lockFreeStack = CreatePopulatedStack();
    private readonly ObjectPool<PoolBenchmarkItem, PoolBenchmarkItemPolicy> _reservoir = new(MaximumRetained);

    [Benchmark(Baseline = true)]
    public int LockFreeStackRentReturn()
    {
        if (!_lockFreeStack.TryPop(out var item))
        {
            return 0;
        }

        var value = item.Value;
        _lockFreeStack.TryPush(item);
        return value;
    }

    [Benchmark]
    public int ReservoirRentReturn()
    {
        var item = _reservoir.Rent();
        var value = item.Value;
        _reservoir.Return(item);
        return value;
    }

    [Benchmark]
    public int ReservoirRentScoped()
    {
        using var lease = _reservoir.RentScoped(out var item);
        return item.Value;
    }

    private static LockFreeStack<PoolBenchmarkItem> CreatePopulatedStack()
    {
        var stack = new LockFreeStack<PoolBenchmarkItem>(MaximumRetained);
        stack.TryPush(new PoolBenchmarkItem());
        return stack;
    }
}

internal sealed class PoolBenchmarkItem
{
    public int Value { get; } = 42;
}

internal readonly struct PoolBenchmarkItemPolicy : IPooledObjectPolicy<PoolBenchmarkItem>
{
    public PoolBenchmarkItem Create() => new();

    public bool TryReset(PoolBenchmarkItem _) => true;
}
