using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Respire.Json;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Compares owned-array serialization with the typed JSON.SET destination path on real Redis.</summary>
[MemoryDiagnoser]
public partial class JsonWriteBenchmarks
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:8.10").Build();
    private RespireClient _client = null!;
    private RespireJsonClient _json = null!;
    private IRespireJsonCommands _commands = null!;
    private Document _document = null!;

    [Params(64, 16384)]
    public int Length { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _document = new(new string('x', Length));
        await _redis.StartAsync();
        _client = await RespireClient.ConnectAsync($"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)}");
        _json = new RespireJsonClient(_client);
        _commands = _json.Commands;
        if (!await SerializeToArrayThenSet() || !await TypedSet())
            throw new InvalidOperationException("JSON.SET did not acknowledge benchmark setup.");
        var restored = await _json.GetAsync("codec-benchmark", JsonContext.Default.Document);
        if (!restored.Found || restored.Value != _document)
            throw new InvalidOperationException("JSON.SET round trip failed.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        await _redis.DisposeAsync();
    }

    // Matches the previous typed SET serialization and await structure.
    [Benchmark(Baseline = true)]
    public async ValueTask<bool> SerializeToArrayThenSet()
    {
        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(_document, JsonContext.Default.Document);
        return await SetCoreAsync(utf8Json).ConfigureAwait(false);
    }

    private async ValueTask<bool> SetCoreAsync(ReadOnlyMemory<byte> payload)
    {
        using var result = await _commands.SetAsync("codec-benchmark", ".", payload).ConfigureAwait(false);
        return !result.IsNull;
    }

    [Benchmark]
    public ValueTask<bool> TypedSet() => _json.SetAsync("codec-benchmark", _document, JsonContext.Default.Document);

    public sealed record Document(string Text);
    [JsonSerializable(typeof(Document))]
    internal partial class JsonContext : JsonSerializerContext;
}
