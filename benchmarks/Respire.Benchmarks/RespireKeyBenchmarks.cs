using System.Text;
using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class RespireKeyBenchmarks
{
    private readonly RespireKey _stringKey = new("benchmark-key");
    private readonly RespireKey _bytesKey = new(Encoding.UTF8.GetBytes("benchmark-key"));

    [Benchmark]
    public bool MixedEquals() => _stringKey.Equals(_bytesKey);

    [Benchmark]
    public int ByteHashCode() => _bytesKey.GetHashCode();

    [Benchmark]
    public int ClusterSlot() => _stringKey.ClusterSlot;

    [Benchmark(Baseline = true)]
    public RespireKey StringRemovalLeaseKey()
        => "respire-rm-lease:" + Guid.NewGuid().ToString("N");

    [Benchmark]
    public RespireKey ClusterRemovalLeaseKey()
        => RespireClient.CreateClusterRemovalLeaseKey(_stringKey.ClusterSlot);
}
