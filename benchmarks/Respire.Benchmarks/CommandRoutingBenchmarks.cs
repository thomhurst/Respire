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
    private readonly RespireValue[] _rawTokens = ["SET", "benchmark-key", "value"];
    private readonly RespireValue[] _rawEvalTokens = ["EVAL", "return 1", 1, "benchmark-key"];
    private readonly int _firstArgumentIndex = 1;

    [Benchmark(Baseline = true)]
    public bool DirectKeyRouting() => _key.TryGetClusterSlot(out _);

    [Benchmark]
    public bool MetadataKeyRouting() => _keyedCommand.TryGetClusterSlot(out _);

    [Benchmark]
    public bool MetadataUnkeyedRouting() => _unkeyedCommand.TryGetClusterSlot(out _);

    [Benchmark]
    public int RawCommandRouting()
        => DynamicCommandRouting.GetRoutingKeyIndex("SET", _rawTokens, _firstArgumentIndex);

    [Benchmark]
    public int RawEvalRouting()
        => DynamicCommandRouting.GetRoutingKeyIndex("EVAL", _rawEvalTokens, _firstArgumentIndex);
}
