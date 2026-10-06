using System.Text;
using BenchmarkDotNet.Attributes;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Complete, nested, and fragmented aggregate receive paths, including disposal.</summary>
[MemoryDiagnoser]
public class AggregateParsingBenchmarks
{
    [Params(3, 1024)]
    public int Count { get; set; }

    private byte[] _flat = null!;
    private byte[] _nested = null!;
    private readonly RespParseState _parser = new(int.MaxValue);

    [GlobalSetup]
    public void Setup()
    {
        _flat = Encoding.ASCII.GetBytes($"*{Count}\r\n" + string.Concat(Enumerable.Repeat(":42\r\n", Count)));
        _nested = [.. "*1\r\n"u8, .. _flat];
        if (Complete() != Count || Nested() != Count || Fragmented() != Count || ScalarBatch() != 42L * Count)
            throw new InvalidOperationException("Aggregate benchmark fixture did not parse correctly.");
    }

    [Benchmark]
    public int Complete()
    {
        var pos = 0;
        RespParser.TryParseValue(_flat, ref pos, out var value);
        using (value) return value.AsArray().Length;
    }

    [Benchmark]
    public int Nested()
    {
        var pos = 0;
        RespParser.TryParseValue(_nested, ref pos, out var value);
        using (value) return value.AsArray()[0].AsArray().Length;
    }

    [Benchmark]
    public long ScalarBatch()
    {
        long sum = 0;
        for (var i = 0; i < Count; i++)
        {
            var pos = 0;
            RespParser.TryParseValue(":42\r\n"u8, ref pos, out var value);
            sum += value.AsInteger();
            value.Dispose();
        }
        return sum;
    }

    [Benchmark]
    public int Fragmented()
    {
        var pos = 0;
        for (var end = 8; end < _flat.Length; end += 64)
            _parser.TryParse(_flat.AsSpan(0, end), ref pos, out _, out _);
        _parser.TryParse(_flat, ref pos, out var value, out _);
        using (value) return value.AsArray().Length;
    }

    [GlobalCleanup]
    public void Cleanup() => _parser.Dispose();
}
