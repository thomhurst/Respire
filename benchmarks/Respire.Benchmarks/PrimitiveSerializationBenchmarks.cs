using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire.Benchmarks;

[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PrimitiveSerializationBenchmarks
{
    private readonly IRespireSerializer _serializer = RespireSerializer.Default;
    private readonly RespValue _integer = RespValue.BulkString("42");
    private readonly RespValue _boolean = RespValue.BulkString("true");
    private RespireClient _client = null!;

    [GlobalSetup]
    public void Setup() => _client = RespireClient.Create("localhost");

    [GlobalCleanup]
    public async Task Cleanup() => await _client.DisposeAsync();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Int32 write")]
    public RespireValue Int32Write_ObjectSerializer() => SerializeWithObjectSerializer(42);

    [Benchmark]
    [BenchmarkCategory("Int32 write")]
    public RespireValue Int32Write_PrimitiveFastPath() => _client.Serialize(42);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Boolean write")]
    public RespireValue BooleanWrite_ObjectSerializer() => SerializeWithObjectSerializer(true);

    [Benchmark]
    [BenchmarkCategory("Boolean write")]
    public RespireValue BooleanWrite_PrimitiveFastPath() => _client.Serialize(true);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Int32 read")]
    public int Int32Read_ObjectSerializer() => _serializer.Deserialize<int>(_integer.AsSpan());

    [Benchmark]
    [BenchmarkCategory("Int32 read")]
    public int Int32Read_PrimitiveFastPath() => _client.DeserializeBorrowed<int>(_integer);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Boolean read")]
    public bool BooleanRead_ObjectSerializer() => _serializer.Deserialize<bool>(_boolean.AsSpan());

    [Benchmark]
    [BenchmarkCategory("Boolean read")]
    public bool BooleanRead_PrimitiveFastPath() => _client.DeserializeBorrowed<bool>(_boolean);

    private RespireValue SerializeWithObjectSerializer<T>(T value)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        _serializer.Serialize(buffer, value);
        return buffer.WrittenMemory;
    }
}
