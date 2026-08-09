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
    private readonly RespireValue _scalarKey = 123456789;
    private readonly Cmd1 _keyedCommand = new(Verbs.Get, "benchmark-key");
    private readonly Cmd1 _unkeyedCommand = new(Verbs.Info, "memory");
    private readonly CatalogCommand _catalogCommand = new(
        RespireCommands.String.GET, ["benchmark-key"]);
    private readonly BitFieldCommand _bitFieldCommand = new(
        RespireCommands.Bitmap.BITFIELD.Verb,
        "benchmark-key",
        [BitFieldOperation.Get("u8", "0")]);
    private readonly BitOpCommand _bitOpCommand = new(
        RespireCommands.Bitmap.BITOP.Verb,
        "AND",
        "key",
        ["source-key"]);
    private readonly GeoAddCommand _geoAddCommand = new(
        RespireCommands.Geo.GEOADD.Verb,
        "benchmark-key",
        GeoAddCondition.Always,
        changed: false,
        []);
    private readonly GeoSearchCommand _geoSearchCommand = new(
        RespireCommands.Geo.GEOSEARCH.Verb,
        "benchmark-key",
        GeoSearchOrigin.FromCoordinates(0, 0),
        GeoSearchShape.Circle(1),
        default,
        destination: null,
        storeDistance: false);
    private readonly RespireValue[] _rawTokens = ["SET", "benchmark-key", "value"];
    private readonly RespireValue[] _rawEvalTokens = ["EVAL", "return 1", 1, "benchmark-key"];
    private readonly RespireValue[] _rawMSetExTokens = ["MSETEX", 1, "benchmark-key", "value"];
    private readonly int _firstArgumentIndex = 1;

    [Benchmark(Baseline = true)]
    public bool DirectKeyRouting() => _key.TryGetClusterSlot(out _);

    [Benchmark]
    public bool ScalarKeyRouting() => _scalarKey.TryGetClusterSlot(out _);

    [Benchmark]
    public bool MetadataKeyRouting() => _keyedCommand.TryGetClusterSlot(out _);

    [Benchmark]
    public bool MetadataUnkeyedRouting() => _unkeyedCommand.TryGetClusterSlot(out _);

    [Benchmark]
    public bool CatalogKeyRouting() => TryGetSlot(_catalogCommand);

    [Benchmark]
    public bool BitFieldRouting() => TryGetSlot(_bitFieldCommand);

    [Benchmark]
    public bool BitOpRouting() => TryGetSlot(_bitOpCommand);

    [Benchmark]
    public bool GeoAddRouting() => TryGetSlot(_geoAddCommand);

    [Benchmark]
    public bool GeoSearchRouting() => TryGetSlot(_geoSearchCommand);

    [Benchmark]
    public int RawCommandRouting()
        => DynamicCommandRouting.GetRoutingKeyIndex("SET", _rawTokens, _firstArgumentIndex);

    [Benchmark]
    public int RawEvalRouting()
        => DynamicCommandRouting.GetRoutingKeyIndex("EVAL", _rawEvalTokens, _firstArgumentIndex);

    [Benchmark]
    public int RawMSetExRouting()
        => DynamicCommandRouting.GetRoutingKeyIndex("MSETEX", _rawMSetExTokens, _firstArgumentIndex);

    private static bool TryGetSlot<TCommand>(TCommand command)
        where TCommand : struct, Respire.Protocol.IRespCommand
        => command.TryGetClusterSlot(out _);
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

    [Benchmark]
    public bool CheckConnectivity() => _router.IsConnected;

    [GlobalCleanup]
    public void Cleanup()
    {
        _router.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _oldOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _newOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
