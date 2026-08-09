using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class CommandInputBenchmarks
{
    [Benchmark]
    public BitFieldOperation CreateBitFieldOperation()
        => BitFieldOperation.Increment("i64", "#1024", 1);

    [Benchmark]
    public GeoSearchShape CreateGeoSearchShape()
        => GeoSearchShape.Box(10, 20, GeoUnit.Kilometers);

    [Benchmark]
    public GeoSearchOrigin CreateGeoSearchOrigin()
        => GeoSearchOrigin.FromCoordinates(-0.1, 51.5);

    [Benchmark]
    public int ClassifyCatalogCommand()
        => (int)new RespireCommand("BLMOVEM", RespireCommandSource.Valkey).Behavior;
}

[MemoryDiagnoser]
[ShortRunJob]
public class GeoResultMemberBenchmarks
{
    private readonly byte[] _member = "benchmark-member"u8.ToArray();

    [Benchmark(Baseline = true)]
    public string DecodeMemberAsString() => Encoding.UTF8.GetString(_member);

    [Benchmark]
    public byte[] CopyMemberBytes() => _member.AsSpan().ToArray();
}
