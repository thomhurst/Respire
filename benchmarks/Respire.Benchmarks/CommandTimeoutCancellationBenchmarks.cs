using BenchmarkDotNet.Attributes;
using Respire.Internal;

namespace Respire.Benchmarks;

[BenchmarkCategory("CancellationTokenSource")]
[MemoryDiagnoser]
public class CommandTimeoutCancellationBenchmarks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    private readonly CancellationTokenSource _callerSource = new();

    [GlobalSetup]
    public void WarmPool()
    {
        using var cancellation = CommandTimeoutCancellation.Create(default, Timeout);
    }

    [GlobalCleanup]
    public void Cleanup() => _callerSource.Dispose();

    [Benchmark(Baseline = true)]
    public bool Before_InertCaller()
    {
        using var source = new CancellationTokenSource(Timeout);
        return source.IsCancellationRequested;
    }

    [Benchmark]
    public bool After_InertCaller()
    {
        using var cancellation = CommandTimeoutCancellation.Create(default, Timeout);
        return cancellation.Token.IsCancellationRequested;
    }

    [Benchmark]
    public bool Before_CancelableCaller()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_callerSource.Token);
        source.CancelAfter(Timeout);
        return source.IsCancellationRequested;
    }

    [Benchmark]
    public bool After_CancelableCaller()
    {
        using var cancellation = CommandTimeoutCancellation.Create(_callerSource.Token, Timeout);
        return cancellation.Token.IsCancellationRequested;
    }
}
