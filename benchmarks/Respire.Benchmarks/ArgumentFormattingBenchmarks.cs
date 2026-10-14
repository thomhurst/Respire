using System.Text.Unicode;
using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

/// <summary>Public interpolation and unchanged value hot paths; also compiles before span interfaces were added.</summary>
[MemoryDiagnoser]
public class ArgumentFormattingBenchmarks
{
    private readonly RespireKey _key = new("benchmark:key"u8.ToArray());
    private readonly RespireValue _value = 123456789L;
    private readonly RespireValue _equivalent = "123456789";
    private readonly char[] _chars = new char[128];
    private readonly byte[] _bytes = new byte[128];

    [Benchmark]
    public int Utf16Key() => _chars.AsSpan().TryWrite($"key={_key}", out var written) ? written : -1;

    [Benchmark]
    public int Utf8Key() => Utf8.TryWrite(_bytes, $"key={_key}", out var written) ? written : -1;

    [Benchmark]
    public int Utf16Value() => _chars.AsSpan().TryWrite($"value={_value}", out var written) ? written : -1;

    [Benchmark]
    public int Utf8Value() => Utf8.TryWrite(_bytes, $"value={_value}", out var written) ? written : -1;

    [Benchmark]
    public int SerializeValue() => _value.WriteWirePayload(_bytes);

    [Benchmark]
    public bool ValueEquals() => _value.Equals(_equivalent);
}
