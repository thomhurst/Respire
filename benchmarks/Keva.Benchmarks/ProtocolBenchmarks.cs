using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using Keva.Protocol;

namespace Keva.Benchmarks;

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
    
    private ArrayPool<byte> _bytePool = null!;
    private ArrayPool<KevaValue> _valuePool = null!;
    private ArrayBufferWriter<byte> _writer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bytePool = ArrayPool<byte>.Shared;
        _valuePool = ArrayPool<KevaValue>.Shared;
        _writer = new ArrayBufferWriter<byte>(4096);
        
        // Simple string: +OK\r\n
        _simpleStringData = "+OK\r\n"u8.ToArray();
        
        // Bulk string: $11\r\nHello World\r\n
        _bulkStringData = "$11\r\nHello World\r\n"u8.ToArray();
        
        // Integer: :42\r\n
        _integerData = ":42\r\n"u8.ToArray();
        
        // Array with 3 bulk strings: *3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n
        _arrayData = "*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray();
        
        // Large array with 100 integers
        var largeArrayBuilder = new StringBuilder();
        largeArrayBuilder.Append("*100\r\n");
        for (int i = 0; i < 100; i++)
        {
            largeArrayBuilder.Append($":{i}\r\n");
        }
        _largeArrayData = Encoding.UTF8.GetBytes(largeArrayBuilder.ToString());
        
        // Nested array: *2\r\n*3\r\n:1\r\n:2\r\n:3\r\n*2\r\n+OK\r\n$4\r\ntest\r\n
        _nestedArrayData = "*2\r\n*3\r\n:1\r\n:2\r\n:3\r\n*2\r\n+OK\r\n$4\r\ntest\r\n"u8.ToArray();
        
        // Mixed types array: *6\r\n+OK\r\n:42\r\n$4\r\ntest\r\n_\r\n#t\r\n,3.14\r\n
        _mixedTypesData = "*6\r\n+OK\r\n:42\r\n$4\r\ntest\r\n_\r\n#t\r\n,3.14\r\n"u8.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // ArrayBufferWriter doesn't need disposal
    }

    // ===== PARSING BENCHMARKS =====
    
    [Benchmark(Description = "Parse Simple String")]
    [BenchmarkCategory("Parsing", "SimpleTypes")]
    public KevaValue ParseSimpleString()
    {
        var reader = new KevaReader(_simpleStringData);
        reader.TryRead(out var value);
        return value;
    }

    [Benchmark(Description = "Parse Bulk String")]
    [BenchmarkCategory("Parsing", "SimpleTypes")]
    public KevaValue ParseBulkString()
    {
        var reader = new KevaReader(_bulkStringData);
        reader.TryRead(out var value);
        return value;
    }

    [Benchmark(Description = "Parse Integer")]
    [BenchmarkCategory("Parsing", "SimpleTypes")]
    public KevaValue ParseInteger()
    {
        var reader = new KevaReader(_integerData);
        reader.TryRead(out var value);
        return value;
    }

    [Benchmark(Description = "Parse Command Array")]
    [BenchmarkCategory("Parsing", "Arrays")]
    public KevaValue ParseCommandArray()
    {
        var reader = new KevaReader(_arrayData);
        reader.TryRead(out var value);
        return value;
    }

    [Benchmark(Description = "Parse Large Array (100 items)")]
    [BenchmarkCategory("Parsing", "Arrays")]
    public KevaValue ParseLargeArray()
    {
        var reader = new KevaReader(_largeArrayData);
        reader.TryRead(out var value);
        return value;
    }

    [Benchmark(Description = "Parse Nested Array")]
    [BenchmarkCategory("Parsing", "Arrays")]
    public KevaValue ParseNestedArray()
    {
        var reader = new KevaReader(_nestedArrayData);
        reader.TryRead(out var value);
        return value;
    }

    [Benchmark(Description = "Parse Mixed Types Array")]
    [BenchmarkCategory("Parsing", "Arrays")]
    public KevaValue ParseMixedTypesArray()
    {
        var reader = new KevaReader(_mixedTypesData);
        reader.TryRead(out var value);
        return value;
    }

    // ===== WRITING BENCHMARKS =====

    [Benchmark(Description = "Write Simple String")]
    [BenchmarkCategory("Writing", "SimpleTypes")]
    public void WriteSimpleString()
    {
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        // writer.Write(KevaValue.SimpleString("OK"));
    }

    [Benchmark(Description = "Write Bulk String")]
    [BenchmarkCategory("Writing", "SimpleTypes")]
    public void WriteBulkString()
    {
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        // writer.Write(KevaValue.BulkString("Hello World"));
    }

    [Benchmark(Description = "Write Integer")]
    [BenchmarkCategory("Writing", "SimpleTypes")]
    public void WriteInteger()
    {
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        // writer.Write(KevaValue.Integer(42));
    }

    [Benchmark(Description = "Write Command")]
    [BenchmarkCategory("Writing", "Commands")]
    public void WriteCommand()
    {
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        // writer.WriteCommand("SET", "key", "value");
    }

    [Benchmark(Description = "Write Large Command (10 args)")]
    [BenchmarkCategory("Writing", "Commands")]
    public void WriteLargeCommand()
    {
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        var args = new[] { "arg1", "arg2", "arg3", "arg4", "arg5", "arg6", "arg7", "arg8", "arg9", "arg10" };
        // writer.WriteCommand("COMMAND", args);
    }

    [Benchmark(Description = "Write Array of 100 Integers")]
    [BenchmarkCategory("Writing", "Arrays")]
    public void WriteLargeArray()
    {
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        
        var values = new KevaValue[100];
        for (int i = 0; i < 100; i++)
        {
            values[i] = KevaValue.Integer(i);
        }
        
        // writer.Write(KevaValue.Array(values));
    }

    [Benchmark(Description = "Write Mixed Types Array")]
    [BenchmarkCategory("Writing", "Arrays")]
    public void WriteMixedTypesArray()
    {
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        
        var array = KevaValue.Array(
            KevaValue.SimpleString("OK"),
            KevaValue.Integer(42),
            KevaValue.BulkString("test"),
            KevaValue.Null,
            KevaValue.Boolean(true),
            KevaValue.Double(3.14)
        );
        
        // writer.Write(array);
    }

    // ===== ROUND-TRIP BENCHMARKS =====

    [Benchmark(Description = "Round-trip Simple Types")]
    [BenchmarkCategory("RoundTrip")]
    public void RoundTripSimpleTypes()
    {
        // Parse
        var reader = new KevaReader(_bulkStringData);
        reader.TryRead(out var value);
        
        // Write
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        // writer.Write(value);
    }

    [Benchmark(Description = "Round-trip Command Array")]
    [BenchmarkCategory("RoundTrip")]
    public void RoundTripCommandArray()
    {
        // Parse
        var reader = new KevaReader(_arrayData);
        reader.TryRead(out var value);
        
        // Write
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        // writer.Write(value);
    }

    [Benchmark(Description = "Round-trip Large Array")]
    [BenchmarkCategory("RoundTrip")]
    public void RoundTripLargeArray()
    {
        // Parse
        var reader = new KevaReader(_largeArrayData);
        reader.TryRead(out var value);
        
        // Write
        _writer.Clear();
        // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
        // writer.Write(value);
    }

    // ===== ALLOCATION STRESS TESTS =====

    [Benchmark(Description = "1000 Parse Operations")]
    [BenchmarkCategory("Stress")]
    public void ThousandParseOperations()
    {
        for (int i = 0; i < 1000; i++)
        {
            var reader = new KevaReader(_mixedTypesData);
            reader.TryRead(out _);
        }
    }

    [Benchmark(Description = "1000 Write Operations")]
    [BenchmarkCategory("Stress")]
    public void ThousandWriteOperations()
    {
        var value = KevaValue.Array(
            KevaValue.SimpleString("OK"),
            KevaValue.Integer(42),
            KevaValue.BulkString("test")
        );

        for (int i = 0; i < 1000; i++)
        {
            _writer.Clear();
            // Writer benchmarks disabled - need new writer implementation
        // var writer = new RespWriter(_writer, _bytePool);
            // writer.Write(value);
        }
    }
}