using System.Text;
using BenchmarkDotNet.Attributes;
using Respire.Commands;
using Respire.Networking;
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
    private readonly WriteBuffer _commandBuffer = new(512);

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

    [Benchmark(Baseline = true, Description = "Receive bulk string (reparse header)")]
    [BenchmarkCategory("BulkReceive")]
    public RespDataType ReceiveBulkString_ReparseHeader()
    {
        var pos = 0;
        RespParser.TryPeekBulkHeader(_bulkStringData, pos, out _, out _, out _);
        RespParser.TryParseValue(_bulkStringData, ref pos, out var value);
        var type = value.Type;
        value.Dispose();
        return type;
    }

    [Benchmark(Description = "Receive bulk string (reuse header)")]
    [BenchmarkCategory("BulkReceive")]
    public RespDataType ReceiveBulkString_ReuseHeader()
    {
        var pos = 0;
        RespParser.TryPeekBulkHeader(
            _bulkStringData, pos, out var type, out var length, out var headerEnd);
        RespParser.TryParseBulkValue(_bulkStringData, ref pos, type, length, headerEnd, out var value);
        var parsedType = value.Type;
        value.Dispose();
        return parsedType;
    }

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

    [Benchmark(Description = "Write GET command")]
    [BenchmarkCategory("Writing")]
    public int BuildGetCommand()
    {
        return WriteCommand(new Cmd1(Verbs.Get, "mykey"));
    }

    [Benchmark(Description = "Write SET command")]
    [BenchmarkCategory("Writing")]
    public int BuildSetCommand()
    {
        return WriteCommand(new Cmd2(Verbs.Set, "mykey", "myvalue"));
    }

    [Benchmark(Description = "Write PING command")]
    [BenchmarkCategory("Writing")]
    public int PreCompiledPing() => RespCommands.Ping.Length;

    private int WriteCommand<TCommand>(TCommand command)
        where TCommand : struct, IRespCommand
    {
        _commandBuffer.Reset();
        var writer = new RespWriter(_commandBuffer);
        command.Write(ref writer);
        return _commandBuffer.Count;
    }

    [GlobalCleanup]
    public void Cleanup() => _commandBuffer.Release();
}
