using System.Text;
using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>ASCII, mixed and Unicode arguments through serialization and public SETRANGE calls.</summary>
[MemoryDiagnoser]
public class Utf8SuffixBenchmarks
{
    [Params("ASCII", "Mixed", "Unicode")]
    public string Shape { get; set; } = "ASCII";

    private IContainer? _container;
    private RespireClient _client = null!;
    private RespireClient _view = null!;
    private WriteBuffer _buffer = null!;
    private readonly KeyPrefix _prefix = new("tenant:");
    private string _text = null!;
    private RespireKey _key;
    private RespireValue _value;
    private readonly long _offset = 0;

    [GlobalSetup]
    public async Task Setup()
    {
        _text = Shape switch
        {
            "ASCII" => new string('a', 256),
            "Mixed" => new string('a', 240) + new string('é', 16),
            "Unicode" => new string('é', 256),
            _ => throw new InvalidOperationException("Unknown input shape."),
        };
        _key = "utf8:" + _text;
        _value = _text;
        _buffer = new WriteBuffer(8192);
        var expected = Encoding.UTF8.GetBytes(_text);
        ValidateFrame(Serialize(), expected);
        ValidateFrame(SerializePrefixed(), [.. _prefix.Bytes, .. expected]);

        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort) ? configuredPort : 6379;
        if (string.IsNullOrEmpty(host))
        {
            _container = new RedisBuilder("redis:8.10").Build();
            await _container.StartAsync();
            host = _container.Hostname;
            port = _container.GetMappedPublicPort(6379);
        }
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(host, port) }, Protocol = RespProtocol.Resp2, Connections = 1,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        _view = (RespireClient)_client.WithKeyPrefix("tenant:");
        if (await Single() != expected.Length || await SinglePrefixed() != expected.Length
            || await Pipeline() != expected.Length * 8 || await PipelinePrefixed() != expected.Length * 8
            || await _client.GetAsync<string>(_key) != _text || await _view.GetAsync<string>(_key) != _text)
            throw new InvalidOperationException("UTF-8 fixture did not preserve lengths, values and every reply.");
    }

    [Benchmark]
    public ReadOnlyMemory<byte> Serialize()
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        writer.WriteBulkString(_text);
        return _buffer.WrittenMemory;
    }

    [Benchmark]
    public ReadOnlyMemory<byte> SerializePrefixed()
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        writer.WritePrefixedKey(_prefix, _text, default);
        return _buffer.WrittenMemory;
    }

    [Benchmark] public ValueTask<long> Single() => _client.Strings.SetRangeAsync(_key, _offset, _value);
    [Benchmark] public ValueTask<long> SinglePrefixed() => _view.Strings.SetRangeAsync(_key, _offset, _value);

    [Benchmark(OperationsPerInvoke = 8)]
    public ValueTask<long> Pipeline() => SendPipeline(_client);

    [Benchmark(OperationsPerInvoke = 8)]
    public ValueTask<long> PipelinePrefixed() => SendPipeline(_view);

    private async ValueTask<long> SendPipeline(RespireClient client)
    {
        var first = client.Strings.SetRangeAsync(_key, _offset, _value);
        var second = client.Strings.SetRangeAsync(_key, _offset, _value);
        var third = client.Strings.SetRangeAsync(_key, _offset, _value);
        var fourth = client.Strings.SetRangeAsync(_key, _offset, _value);
        var fifth = client.Strings.SetRangeAsync(_key, _offset, _value);
        var sixth = client.Strings.SetRangeAsync(_key, _offset, _value);
        var seventh = client.Strings.SetRangeAsync(_key, _offset, _value);
        var eighth = client.Strings.SetRangeAsync(_key, _offset, _value);
        return await first + await second + await third + await fourth
            + await fifth + await sixth + await seventh + await eighth;
    }

    private static void ValidateFrame(ReadOnlyMemory<byte> frame, byte[] payload)
    {
        byte[] expected = [.. Encoding.ASCII.GetBytes($"${payload.Length}\r\n"), .. payload, 13, 10];
        if (!frame.Span.SequenceEqual(expected)) throw new InvalidOperationException("Incorrect serialization fixture.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _buffer?.Release();
        if (_client is not null) await _client.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
