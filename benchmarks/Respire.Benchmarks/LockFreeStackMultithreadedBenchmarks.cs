using BenchmarkDotNet.Attributes;
using Reservoir;
using Respire.Networking;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
public class LockFreeStackMultithreadedBenchmarks
{
    private const int MaximumRetained = 4096;
    private const int WorkerCount = 8;
    private const int OperationsPerWorker = 16_384;
    private const int OperationsPerInvoke = WorkerCount * OperationsPerWorker;

    private readonly Barrier _barrier = new(WorkerCount + 1);
    private readonly int[] _results = new int[WorkerCount];
    private LockFreeStack<PoolBenchmarkItem> _lockFreeStack = null!;
    private ObjectPool<PoolBenchmarkItem, PoolBenchmarkItemPolicy> _reservoir = null!;
    private Thread[] _workers = null!;
    private Exception? _workerException;
    private Workload _workload;
    private bool _stopping;

    [GlobalSetup]
    public void Setup()
    {
        _lockFreeStack = new LockFreeStack<PoolBenchmarkItem>(MaximumRetained);
        _reservoir = new ObjectPool<PoolBenchmarkItem, PoolBenchmarkItemPolicy>(MaximumRetained);

        var reservoirItems = new PoolBenchmarkItem[WorkerCount];
        for (var i = 0; i < WorkerCount; i++)
        {
            _lockFreeStack.TryPush(new PoolBenchmarkItem());
            reservoirItems[i] = _reservoir.Rent();
        }

        foreach (var item in reservoirItems)
        {
            _reservoir.Return(item);
        }

        _workers = new Thread[WorkerCount];
        for (var i = 0; i < WorkerCount; i++)
        {
            var workerIndex = i;
            _workers[i] = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"Pool benchmark worker {i}",
            };
            _workers[i].Start();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stopping = true;
        _barrier.SignalAndWait();

        foreach (var worker in _workers)
        {
            worker.Join();
        }

        _reservoir.Dispose();
        _barrier.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvoke)]
    public int LockFreeStackRentReturn() => Run(Workload.LockFreeStack);

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int ReservoirRentReturn() => Run(Workload.Reservoir);

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int ReservoirRentScoped() => Run(Workload.ReservoirScoped);

    private int Run(Workload workload)
    {
        _workerException = null;
        _workload = workload;
        _barrier.SignalAndWait();
        _barrier.SignalAndWait();

        if (_workerException is { } exception)
        {
            throw new InvalidOperationException("Pool benchmark worker failed.", exception);
        }

        var result = 0;
        foreach (var workerResult in _results)
        {
            result += workerResult;
        }

        return result;
    }

    private void WorkerLoop(int workerIndex)
    {
        while (true)
        {
            _barrier.SignalAndWait();
            if (_stopping)
            {
                return;
            }

            try
            {
                _results[workerIndex] = _workload switch
                {
                    Workload.LockFreeStack => RunLockFreeStack(),
                    Workload.Reservoir => RunReservoir(),
                    Workload.ReservoirScoped => RunReservoirScoped(),
                    _ => throw new InvalidOperationException($"Unknown workload: {_workload}"),
                };
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _workerException, exception, null);
            }
            finally
            {
                _barrier.SignalAndWait();
            }
        }
    }

    private int RunLockFreeStack()
    {
        var result = 0;
        for (var i = 0; i < OperationsPerWorker; i++)
        {
            if (!_lockFreeStack.TryPop(out var item))
            {
                item = new PoolBenchmarkItem();
            }

            result += item.Value;
            _lockFreeStack.TryPush(item);
        }

        return result;
    }

    private int RunReservoir()
    {
        var result = 0;
        for (var i = 0; i < OperationsPerWorker; i++)
        {
            var item = _reservoir.Rent();
            result += item.Value;
            _reservoir.Return(item);
        }

        return result;
    }

    private int RunReservoirScoped()
    {
        var result = 0;
        for (var i = 0; i < OperationsPerWorker; i++)
        {
            using var lease = _reservoir.RentScoped(out var item);
            result += item.Value;
        }

        return result;
    }

    private enum Workload
    {
        LockFreeStack,
        Reservoir,
        ReservoirScoped,
    }

}
