using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class RespireScriptBenchmarks
{
    private string _source = null!;

    [Params(13, 1024)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup() => _source = new string('x', Length);

    [Benchmark]
    public RespireScript Create() => RespireScript.Create(_source);
}
