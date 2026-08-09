using BenchmarkDotNet.Attributes;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class StringWriteBenchmarks
{
    private readonly WriteBuffer _buffer = new(4096);
    private string _ascii = null!;
    private string _unicode = null!;

    [Params(13, 1024)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _ascii = new string('x', Length);
        _unicode = new string('\u00A3', Length);
    }

    [GlobalCleanup]
    public void Cleanup() => _buffer.Release();

    [Benchmark]
    public int WriteAscii()
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        writer.WriteBulkString(_ascii);
        return _buffer.Count;
    }

    [Benchmark]
    public int WriteUnicode()
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        writer.WriteBulkString(_unicode);
        return _buffer.Count;
    }
}
