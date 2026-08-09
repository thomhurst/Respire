#if NET9_0_OR_GREATER
using System.Collections.Concurrent;
using System.Text;
using BenchmarkDotNet.Attributes;
using Respire.Internal;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
public class PubSubLookupBenchmarks
{
    private readonly ConcurrentDictionary<string, int> _legacyRoutes = new(StringComparer.Ordinal);
    private readonly Utf8RouteDictionary<int> _utf8Routes = new();
    private readonly byte[] _incomingName = "notifications:user:42"u8.ToArray();

    [GlobalSetup]
    public void Setup()
    {
        const string name = "notifications:user:42";
        _legacyRoutes.TryAdd(name, 42);
        _utf8Routes.Add(name, 42);
    }

    [Benchmark(Baseline = true, Description = "Decode string then ConcurrentDictionary lookup")]
    public int StringLookup()
    {
        var name = Encoding.UTF8.GetString(_incomingName);
        return _legacyRoutes.TryGetValue(name, out var value) ? value : -1;
    }

    [Benchmark(Description = "UTF-8 alternate lookup")]
    public int Utf8Lookup()
        => _utf8Routes.TryGetValue(_incomingName, out _, out var value) ? value : -1;
}
#endif
