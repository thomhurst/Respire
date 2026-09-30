using BenchmarkDotNet.Attributes;
using Respire.Networking;

namespace Respire.Benchmarks;

/// <summary>Tracks the allocation-free FIFO operations paid by every command and reply.</summary>
[MemoryDiagnoser]
public class InflightRingBenchmarks
{
    private readonly InflightRing _ring = new(1024);
    private readonly PendingResponse _source = InflightRing.DiscardSentinel;

    [Benchmark]
    public bool EnqueueAndDequeue()
    {
        if (!_ring.TryEnqueue(_source))
            throw new InvalidOperationException("The preceding invocation must drain the ring.");
        return _ring.TryDequeue(out var source) && ReferenceEquals(source, _source);
    }
}
