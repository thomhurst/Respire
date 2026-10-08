using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Respire;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Measures the normal selection and accepted-send paths against a live Redis server.</summary>
[MemoryDiagnoser]
public class TransportAcceptanceBenchmarks
{
    private const int BatchSize = 32;
    private const int StreamPayloadLength = 256 * 1024;
    private readonly ValueTask<RespValue>[] _replies = new ValueTask<RespValue>[BatchSize];
    private readonly ValueTask<TimeSpan>[] _publicReplies = new ValueTask<TimeSpan>[BatchSize];
    private readonly byte[] _largePayload = new byte[5 * 1024 * 1024];
    private readonly DelayedReadStream _stream = new(new byte[StreamPayloadLength]);
    private readonly MemoryStream _instantStream = new(new byte[StreamPayloadLength]);
    private static readonly RawCommand Ping = new("*1\r\n$4\r\nPING\r\n"u8.ToArray());
    private readonly RespireKey _errorKey = "transport-acceptance:not-integer";
    private RespireConnectionMultiplexer _multiplexer = null!;
    private RespireClient _client = null!;
    private ErrorMetricBenchmarkScope _errorMetrics = null!;

    [Params(1, 2)]
    public int Connections { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _errorMetrics = new();
        ReportMemory("before-connect");
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configured) ? configured : 6379;
        _multiplexer = await RespireConnectionMultiplexer.CreateAsync(host, port, Connections);
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = Connections,
            Endpoints = { new RespireEndpoint(host, port) },
            ThreadPoolMonitoring = false,
        });
        await Pipeline();
        await _client.PingAsync();
        await _client.SetAsync(_errorKey, "value");
        await _errorMetrics.WarmAsync(_client, _errorKey);
        ReportMemory("connected");
        // Exercise bounded source reuse and both ordinary write layouts outside the timed region.
        for (var i = 0; i < 64; i++) await PublicPipeline();
        await SmallSet();
        for (var i = 0; i < Connections * 2; i++) await LargeSet();
        await StreamedSetWithInstantSource();
        ReportMemory("after-churn");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _client.DisposeAsync();
        await _multiplexer.DisposeAsync();
        _client = null!;
        _multiplexer = null!;
        _stream.Dispose();
        _instantStream.Dispose();
        _errorMetrics.Dispose();
        ReportMemory("after-dispose");
    }

    [Benchmark]
    public object SelectConnection() => _multiplexer.GetConnection();

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task Pipeline()
    {
        for (var i = 0; i < BatchSize; i++) _replies[i] = _multiplexer.SendAsync(Ping);
        for (var i = 0; i < BatchSize; i++)
        {
            using var reply = await _replies[i];
            if (!reply.AsSpan().SequenceEqual("PONG"u8))
                throw new InvalidOperationException("Unexpected pipelined PING response.");
        }
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task PublicPipeline()
    {
        for (var i = 0; i < BatchSize; i++) _publicReplies[i] = _client.PingAsync();
        for (var i = 0; i < BatchSize; i++) await _publicReplies[i];
    }

    [Benchmark]
    public async Task SmallSet()
    {
        if (!await _client.Strings.SetAsync("transport-acceptance:small", "value"))
            throw new InvalidOperationException("Unexpected SET response.");
    }

    [Benchmark]
    public async Task LargeSet()
    {
        if (!await _client.Strings.SetAsync("transport-acceptance:large", (RespireValue)_largePayload))
            throw new InvalidOperationException("Unexpected large SET response.");
    }

    private void ReportMemory(string stage)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("TRANSPORT_MEMORY " + JsonSerializer.Serialize(new
        {
            stage, connections = Connections, pipelineCommands = BatchSize * 64,
            callerPayloadBytes = _largePayload.Length + 2 * StreamPayloadLength,
            managedBytes = GC.GetTotalMemory(false), heapBytes = info.HeapSizeBytes,
            fragmentedBytes = info.FragmentedBytes, pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes,
            workingSetBytes = process.WorkingSet64, privateMemoryBytes = process.PrivateMemorySize64,
        }));
    }

    [Benchmark]
    public async Task StreamedSetWithDelayedSource()
    {
        _stream.Position = 0;
        if (!await _client.Strings.SetAsync("transport-acceptance:stream", _stream, StreamPayloadLength))
            throw new InvalidOperationException("Unexpected streamed SET response.");
    }

    [Benchmark]
    public async Task StreamedSetWithInstantSource()
    {
        _instantStream.Position = 0;
        if (!await _client.Strings.SetAsync("transport-acceptance:stream", _instantStream, StreamPayloadLength))
            throw new InvalidOperationException("Unexpected streamed SET response.");
    }

    private sealed class DelayedReadStream(byte[] payload) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async Task ClientPing() => await _client.PingAsync();

    [Benchmark]
    public async Task<string> ClientServerError()
    {
        try { await _client.Strings.IncrementAsync(_errorKey); }
        catch (RespireServerException error) when (error.Code == "ERR") { return error.Code; }
        throw new InvalidOperationException("Expected Redis ERR for a non-integer value.");
    }
}
