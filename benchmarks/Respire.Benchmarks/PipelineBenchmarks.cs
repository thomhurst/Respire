using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>
/// Benchmarks for the wire protocol (parsing, command building) and live client operations.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class PipelineBenchmarks
{
    private static readonly byte[] SimpleStringData = "+PONG\r\n"u8.ToArray();
    private static readonly byte[] BulkStringData = "$13\r\nbenchmark_val\r\n"u8.ToArray();
    private static readonly byte[] IntegerData = ":12345\r\n"u8.ToArray();

    private RespireClient _client = null!;
    private readonly WriteBuffer _commandBuffer = new(512);
    private const string TestKey = "benchmark_key";
    private const string TestValue = "benchmark_value_with_some_length_to_it";

    [GlobalSetup]
    public async Task Setup()
    {
        try
        {
            _client = await RespireClient.ConnectAsync("localhost:6379");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not connect to Redis for benchmarks: {ex.Message}");
            Console.WriteLine("Benchmarks will measure parsing and command building only");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }

        _commandBuffer.Release();
    }

    // Command building benchmarks

    [Benchmark]
    public int PreCompiledCommands_BuildGetCommand()
    {
        return WriteCommand(new Cmd1(Verbs.Get, TestKey));
    }

    [Benchmark]
    public int PreCompiledCommands_BuildSetCommand()
    {
        return WriteCommand(new Cmd2(Verbs.Set, TestKey, TestValue));
    }

    // Wire parser benchmarks

    [Benchmark]
    public bool RespParser_ParseSimpleString()
    {
        var pos = 0;
        var status = RespParser.TryParseValue(SimpleStringData, ref pos, out var value);
        value.Dispose();
        return status == RespParseStatus.Done;
    }

    [Benchmark]
    public bool RespParser_ParseBulkString()
    {
        var pos = 0;
        var status = RespParser.TryParseValue(BulkStringData, ref pos, out var value);
        value.Dispose();
        return status == RespParseStatus.Done;
    }

    [Benchmark]
    public bool RespParser_ParseInteger()
    {
        var pos = 0;
        var status = RespParser.TryParseValue(IntegerData, ref pos, out var value);
        value.Dispose();
        return status == RespParseStatus.Done;
    }

    // Live Redis operation benchmarks (conditional on Redis availability)

    [Benchmark]
    public async Task RespireClient_SetOperation()
    {
        if (_client != null)
        {
            await _client.SetAsync($"{TestKey}_respire", TestValue);
        }
    }

    [Benchmark]
    public async Task RespireClient_GetOperation()
    {
        if (_client != null)
        {
            await _client.GetStringAsync(TestKey);
        }
    }

    [Benchmark]
    public async Task RespireClient_PingOperation()
    {
        if (_client != null)
        {
            await _client.PingAsync();
        }
    }

    // Batch operation benchmarks

    [Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(1000)]
    public async Task RespireClient_BatchSetOperations(int batchSize)
    {
        if (_client != null)
        {
            var tasks = Enumerable.Range(0, batchSize)
                .Select(i => _client.SetAsync($"{TestKey}_batch_{i}", $"{TestValue}_{i}").AsTask())
                .ToArray();

            await Task.WhenAll(tasks);
        }
    }

    // Memory allocation comparison benchmarks

    [Benchmark]
    public int PreEncodedCommands_Ping() => RespCommands.Ping.AsSpan().Length;

    [Benchmark]
    public byte[] ManualCommandBuilding_Ping()
    {
        return "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    }

    [Benchmark]
    public void ZeroAllocCommandBuilding_Get()
    {
        WriteCommand(new Cmd1(Verbs.Get, "test"));
    }

    [Benchmark]
    public byte[] TraditionalCommandBuilding_Get()
    {
        var key = "test";
        var command = $"*2\r\n$3\r\nGET\r\n${key.Length}\r\n{key}\r\n";
        return System.Text.Encoding.UTF8.GetBytes(command);
    }

    private int WriteCommand<TCommand>(TCommand command)
        where TCommand : struct, IRespCommand
    {
        _commandBuffer.Reset();
        var writer = new RespWriter(_commandBuffer);
        command.Write(ref writer);
        return _commandBuffer.Count;
    }
}

/// <summary>
/// Throughput benchmarks for measuring operations per second
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class PipelineThroughputBenchmarks
{
    private RespireClient _client = null!;
    private const string TestKey = "throughput_test";
    private const string TestValue = "throughput_value";

    [Params(1, 10, 100, 1000)]
    public int OperationCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        try
        {
            _client = await RespireClient.ConnectAsync("localhost:6379");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not connect to Redis: {ex.Message}");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }
    }

    [Benchmark]
    public async Task RespireClient_ConcurrentSets()
    {
        if (_client != null)
        {
            var tasks = Enumerable.Range(0, OperationCount)
                .Select(i => _client.SetAsync($"{TestKey}_{i}", $"{TestValue}_{i}").AsTask())
                .ToArray();

            await Task.WhenAll(tasks);
        }
    }

    [Benchmark]
    public async Task RespireClient_ConcurrentGets()
    {
        if (_client != null)
        {
            var tasks = Enumerable.Range(0, OperationCount)
                .Select(i => _client.GetStringAsync($"{TestKey}_{i}").AsTask())
                .ToArray();

            await Task.WhenAll(tasks);
        }
    }
}
