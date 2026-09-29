using System.Text;
using BenchmarkDotNet.Attributes;
using Respire.Internal;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
public class PubSubLookupBenchmarks
{
    private readonly Dictionary<string, int> _legacyRoutes = new(StringComparer.Ordinal);
    private readonly ByteRouteDictionary<int> _utf8Routes = new();
    private readonly ByteRouteDictionary<int> _binaryRoutes = new();
    private readonly byte[] _incomingName = "notifications:user:42"u8.ToArray();
    private readonly byte[] _binaryName = [0xff, 0, (byte)':', 0xfe];

    [GlobalSetup]
    public void Setup()
    {
        const string name = "notifications:user:42";
        _legacyRoutes.Add(name, 42);
        _utf8Routes.Add(name, 42);
        _binaryRoutes.Add((RespireChannel)_binaryName, 43);
    }

    [Benchmark(Baseline = true, Description = "Decode string then Dictionary lookup")]
    public int StringLookup()
    {
        var name = Encoding.UTF8.GetString(_incomingName);
        return _legacyRoutes.TryGetValue(name, out var value) ? value : -1;
    }

    [Benchmark(Description = "Raw byte lookup")]
    public int Utf8Lookup()
        => _utf8Routes.TryGetValue(_incomingName, out _, out var value) ? value : -1;

    [Benchmark(Description = "Binary span lookup")]
    public int BinaryLookup()
        => _binaryRoutes.TryGetValue(_binaryName, out _, out var value) ? value : -1;
}

[MemoryDiagnoser]
public class PubSubChannelAccessBenchmarks
{
    private readonly RespireChannel _channel = "notifications:user:42";

    [Benchmark(Baseline = true)]
    public ReadOnlyMemory<byte> RawBytes() => _channel.Bytes;

    [Benchmark]
    public string DisplayText() => _channel.ToString();
}
