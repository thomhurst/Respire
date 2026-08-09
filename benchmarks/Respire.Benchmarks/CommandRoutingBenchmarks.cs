using BenchmarkDotNet.Attributes;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;

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

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class ClusterOwnershipBenchmarks
{
    private ClusterRouter _router = null!;
    private RespireConnectionMultiplexer _oldOwner = null!;
    private RespireConnectionMultiplexer _newOwner = null!;

    [GlobalSetup]
    public void Setup()
    {
        var primary = RespireConnectionMultiplexer.Create("localhost", 6379);
        _oldOwner = RespireConnectionMultiplexer.Create("localhost", 6380);
        _newOwner = RespireConnectionMultiplexer.Create("localhost", 6381);
        _router = new ClusterRouter(new RespireOptions { Cluster = true }, primary);
        _router.SetSlotOwner(1, _oldOwner);
        _router.SetSlotOwner(2, _newOwner);
    }

    [Benchmark]
    public void MoveRoutedSlot()
    {
        _router.SetSlotOwner(0, _oldOwner);
        _router.SetSlotOwner(0, _newOwner);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _router.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _oldOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _newOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
