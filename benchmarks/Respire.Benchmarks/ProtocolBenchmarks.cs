using System.Text;
using BenchmarkDotNet.Attributes;
using Respire.Protocol;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ProtocolBenchmarks
{
    private byte[] _simpleStringData = null!;
    private byte[] _bulkStringData = null!;
    private byte[] _integerData = null!;
    private byte[] _arrayData = null!;
    private byte[] _largeArrayData = null!;
    private byte[] _nestedArrayData = null!;
    private byte[] _mixedTypesData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _simpleStringData = "+OK\r\n"u8.ToArray();
        _bulkStringData = "$11\r\nHello World\r\n"u8.ToArray();
        _integerData = ":42\r\n"u8.ToArray();
        _arrayData = "*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray();

        var largeArrayBuilder = new StringBuilder();
        largeArrayBuilder.Append("*100\r\n");
        for (var i = 0; i < 100; i++)
        {
            largeArrayBuilder.Append($":{i}\r\n");
        }

        _largeArrayData = Encoding.UTF8.GetBytes(largeArrayBuilder.ToString());

        _nestedArrayData = "*2\r\n*3\r\n:1\r\n:2\r\n:3\r\n*2\r\n+OK\r\n$4\r\ntest\r\n"u8.ToArray();
        _mixedTypesData = "*6\r\n+OK\r\n:42\r\n$4\r\ntest\r\n_\r\n#t\r\n,3.14\r\n"u8.ToArray();
    }

    // ===== PARSING BENCHMARKS =====

    [Benchmark(Description = "Parse Simple String")]
    [BenchmarkCategory("Parsing", "SimpleTypes")]
    public RespDataType ParseSimpleString() => ParseAndDispose(_simpleStringData);

    [Benchmark(Description = "Parse Bulk String")]
    [BenchmarkCategory("Parsing", "SimpleTypes")]
    public RespDataType ParseBulkString() => ParseAndDispose(_bulkStringData);

    [Benchmark(Description = "Parse Integer")]
    [BenchmarkCategory("Parsing", "SimpleTypes")]
    public RespDataType ParseInteger() => ParseAndDispose(_integerData);

    [Benchmark(Description = "Parse Command Array")]
    [BenchmarkCategory("Parsing", "Arrays")]
    public RespDataType ParseCommandArray() => ParseAndDispose(_arrayData);

    [Benchmark(Description = "Parse Large Array (100 ints)")]
    [BenchmarkCategory("Parsing", "Arrays")]
    public RespDataType ParseLargeArray() => ParseAndDispose(_largeArrayData);

    [Benchmark(Description = "Parse Nested Array")]
    [BenchmarkCategory("Parsing", "Arrays")]
    public RespDataType ParseNestedArray() => ParseAndDispose(_nestedArrayData);

    [Benchmark(Description = "Parse Mixed Types Array")]
    [BenchmarkCategory("Parsing", "Arrays")]
    public RespDataType ParseMixedTypesArray() => ParseAndDispose(_mixedTypesData);

    private static RespDataType ParseAndDispose(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        RespParser.TryParseValue(data, ref pos, out var value);
        var type = value.Type;
        value.Dispose();
        return type;
    }

    // ===== COMMAND BUILDING BENCHMARKS =====

    [Benchmark(Description = "Build GET command (span)")]
    [BenchmarkCategory("Writing")]
    public int BuildGetCommand()
    {
        Span<byte> buffer = stackalloc byte[256];
        return RespCommands.BuildGetCommand(buffer, "mykey");
    }

    [Benchmark(Description = "Build SET command (span)")]
    [BenchmarkCategory("Writing")]
    public int BuildSetCommand()
    {
        Span<byte> buffer = stackalloc byte[512];
        return RespCommands.BuildSetCommand(buffer, "mykey", "myvalue");
    }

    [Benchmark(Description = "Pre-compiled PING")]
    [BenchmarkCategory("Writing")]
    public int PreCompiledPing() => RespCommands.Ping.Length;
}
