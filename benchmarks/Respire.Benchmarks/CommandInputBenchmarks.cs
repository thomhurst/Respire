using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class CommandInputBenchmarks
{
    private int _bitFieldWidth = 64;
    private long _bitFieldIndex = 1024;
    private long _increment = 1;

    [Benchmark]
    public RespireBitFieldOperation CreateBitFieldOperation()
        => RespireBitFieldOperation.Increment(RespireBitFieldEncoding.Signed(_bitFieldWidth), RespireBitFieldOffset.Fields(_bitFieldIndex), _increment);

    [Benchmark]
    public RespireGeoSearchShape CreateGeoSearchShape()
        => RespireGeoSearchShape.Box(10, 20, RespireGeoUnit.Kilometers);

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
