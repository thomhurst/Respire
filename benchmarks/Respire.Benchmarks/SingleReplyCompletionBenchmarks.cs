using BenchmarkDotNet.Attributes;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Actual pooled reply lifetimes, with both sequential release orders.</summary>
[MemoryDiagnoser]
public class SingleReplyCompletionBenchmarks
{
    private readonly PendingResponsePool _pool = new(1);
    private readonly RespValue _reply = RespValue.Integer(42);

    [GlobalSetup]
    public void Setup()
    {
        if (CallerReleasesFirst() != 42 || ReceiverReleasesFirst() != 42
            || MultiReply3CallerFirst() != 42 || MultiReply3ReceiverFirst() != 42
            || MultiReply64CallerFirst() != 42 || MultiReply64ReceiverFirst() != 42)
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

    [Benchmark]
    public long MultiReply3CallerFirst() => CompleteMultiReply(3, callerFirst: true);

    [Benchmark]
    public long MultiReply3ReceiverFirst() => CompleteMultiReply(3, callerFirst: false);

    [Benchmark]
    public long MultiReply64CallerFirst() => CompleteMultiReply(64, callerFirst: true);

    [Benchmark]
    public long MultiReply64ReceiverFirst() => CompleteMultiReply(64, callerFirst: false);

    private long CompleteMultiReply(int replyCount, bool callerFirst)
    {
        var source = MultiReplyPendingResponseSource.Rent(replyCount, 0, "MULTI/EXEC");
        PendingResponse completion = source;
        var pending = source.Task;
        // One operation drains one complete sequence, including each intermediate reply.
        for (var i = 0; i < replyCount - 1; i++)
        {
            source.TrySetResult(in _reply);
            completion.ReleaseRef();
        }
        source.TrySetResult(in _reply);
        if (!callerFirst) completion.ReleaseRef();
        using var reply = pending.GetAwaiter().GetResult();
        var result = reply.AsInteger();
        if (callerFirst) completion.ReleaseRef();
        return result;
    }
}
