using BenchmarkDotNet.Attributes;
using Respire.Commands;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class CommandRoutingBenchmarks
{
    private readonly RespireValue _key = "benchmark-key";
    private readonly Cmd1 _keyedCommand = new(Verbs.Get, "benchmark-key");
    private readonly Cmd1 _unkeyedCommand = new(Verbs.Info, "memory");

    [Benchmark(Baseline = true)]
    public bool DirectKeyRouting() => _key.TryGetClusterSlot(out _);

    [Benchmark]
    public bool MetadataKeyRouting() => _keyedCommand.TryGetClusterSlot(out _);

    [Benchmark]
    public bool MetadataUnkeyedRouting() => _unkeyedCommand.TryGetClusterSlot(out _);
}
