using BenchmarkDotNet.Attributes;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Actual pooled single-reply lifetime, with both sequential release orders.</summary>
[MemoryDiagnoser]
public class SingleReplyCompletionBenchmarks
{
    private readonly PendingResponsePool _pool = new(1);
    private readonly RespValue _reply = RespValue.Integer(42);

    [GlobalSetup]
    public void Setup()
    {
        if (CallerReleasesFirst() != 42 || ReceiverReleasesFirst() != 42)
            throw new InvalidOperationException("Completion fixture lost or changed a reply.");
    }

    [Benchmark]
    public long CallerReleasesFirst()
    {
        var source = _pool.Rent();
        var pending = source.Task;
        source.TrySetResult(in _reply);
        using var reply = pending.GetAwaiter().GetResult();
        var result = reply.AsInteger();
        source.ReleaseRef();
        return result;
    }

    [Benchmark]
    public long ReceiverReleasesFirst()
    {
        var source = _pool.Rent();
        var pending = source.Task;
        source.TrySetResult(in _reply);
        source.ReleaseRef();
        using var reply = pending.GetAwaiter().GetResult();
        return reply.AsInteger();
    }
}
