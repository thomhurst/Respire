using BenchmarkDotNet.Attributes;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Tracks keyed command serialization and the cost of creating a namespace view.</summary>
[MemoryDiagnoser]
public class KeyPrefixBenchmarks
{
    private RespireClient _client = null!;
    private RespireClient _prefixed = null!;
    private WriteBuffer _buffer = null!;
    private RespireKey _text;
    private RespireKey _binary;
    private string _prefix = "tenant:";

    [GlobalSetup]
    public void Setup()
    {
        _client = RespireClient.Create("localhost");
        _prefixed = (RespireClient)_client.WithKeyPrefix(_prefix);
        _buffer = new WriteBuffer(256);
        _text = "benchmark-key";
        _binary = "benchmark-key"u8.ToArray();
        _ = _client.Strings;
    }

    [Benchmark(Baseline = true)]
    public int PlainStringGet() => WriteGet(_client, in _text);

    [Benchmark]
    public int PrefixedStringGet() => WriteGet(_prefixed, in _text);

    [Benchmark]
    public int PlainBinaryGet() => WriteGet(_client, in _binary);

    [Benchmark]
    public int PrefixedBinaryGet() => WriteGet(_prefixed, in _binary);

    [Benchmark]
    public IRespireClient CreatePrefixView() => _client.WithKeyPrefix(_prefix);

    [Benchmark]
    public IStringCommands ReadInitializedFacet() => _client.Strings;

    private int WriteGet(RespireClient client, in RespireKey key)
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        var command = new Cmd1(RespireCommands.String.GET.Verb, client.Key(in key));
        command.Write(ref writer);
        return _buffer.Count;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _buffer.Release();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
