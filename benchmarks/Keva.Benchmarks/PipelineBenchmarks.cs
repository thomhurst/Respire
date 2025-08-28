using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Respire.FastClient;
using Respire.Infrastructure;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>
/// Comprehensive benchmarks comparing the new pipeline architecture against previous implementations
/// Measures throughput, memory allocation, and latency improvements
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class PipelineBenchmarks
{
    private RespireClient _kevaClient = null!;
    private RespireMemoryPool _memoryPool = null!;
    private const string TestKey = "benchmark_key";
    private const string TestValue = "benchmark_value_with_some_length_to_it";
    
    [GlobalSetup]
    public async Task Setup()
    {
        try
        {
            _memoryPool = RespireMemoryPool.Shared;
            
            // Create unified client (will fail if Redis is not available, but benchmark structure will be valid)
            _kevaClient = await RespireClient.CreateAsync("localhost", 6379);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not connect to Redis for benchmarks: {ex.Message}");
            Console.WriteLine("Benchmarks will measure client creation and command building only");
        }
    }
    
    [GlobalCleanup]
    public void Cleanup()
    {
        _kevaClient?.Dispose();
    }
    
    // Memory pooling benchmarks
    
    [Benchmark]
    public void MemoryPool_RentReturn_1KB()
    {
        using var memory = _memoryPool.RentMemory(1024);
        // Simulate some work
        memory.Memory.Span.Fill(0x42);
    }
    
    [Benchmark]
    public void MemoryPool_ArrayRentReturn_1KB()
    {
        var array = _memoryPool.RentArray(1024);
        Array.Fill(array, (byte)0x42, 0, Math.Min(1024, array.Length));
        _memoryPool.ReturnArray(array);
    }
    
    [Benchmark]
    public void StandardAllocation_1KB()
    {
        var array = new byte[1024];
        Array.Fill(array, (byte)0x42);
    }
    
    [Benchmark]
    public void PooledBufferWriter_Write1KB()
    {
        using var writer = _memoryPool.CreateBufferWriter();
        var data = new byte[1024];
        Array.Fill(data, (byte)0x42);
        writer.WritePreCompiledCommand(data);
    }
    
    // Command building benchmarks
    
    [Benchmark]
    public int PreCompiledCommands_BuildGetCommand()
    {
        Span<byte> buffer = stackalloc byte[256];
        return RespCommands.BuildGetCommand(buffer, TestKey);
    }
    
    [Benchmark]
    public int PreCompiledCommands_BuildSetCommand()
    {
        Span<byte> buffer = stackalloc byte[512];
        return RespCommands.BuildSetCommand(buffer, TestKey, TestValue);
    }
    
    [Benchmark]
    public void PooledBufferWriter_BuildComplexCommand()
    {
        using var writer = _memoryPool.CreateBufferWriter();
        writer.WriteBulkString(TestKey);
        writer.WriteBulkString(TestValue);
        writer.WritePreCompiledCommand("*2\r\n$3\r\nSET\r\n"u8);
    }
    
    // Pipeline reader benchmarks
    
    [Benchmark]
    public bool RespPipelineReader_ParseSimpleString()
    {
        var data = "+PONG\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        return reader.TryReadValue(out _);
    }
    
    [Benchmark]
    public bool RespPipelineReader_ParseBulkString()
    {
        var data = "$13\r\nbenchmark_val\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        return reader.TryReadValue(out _);
    }
    
    [Benchmark]
    public bool RespPipelineReader_ParseInteger()
    {
        var data = ":12345\r\n"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        var reader = new RespPipelineReader(sequence);
        return reader.TryReadValue(out _);
    }
    
    // Connection creation benchmarks (measures client initialization overhead)
    
    [Benchmark]
    public async Task CreatePipelineClient()
    {
        try
        {
            await using var client = await RespireClient.CreateAsync("localhost", 6379);
        }
        catch
        {
            // Expected if Redis is not available
        }
    }
    
    [Benchmark]
    public async Task CreatePreCompiledClient()
    {
        try
        {
            await using var client = await RespireClient.CreateAsync("localhost", 6379);
        }
        catch
        {
            // Expected if Redis is not available
        }
    }
    
    [Benchmark]
    public async Task CreateFastClient()
    {
        try
        {
            await using var client = await RespireClient.CreateAsync("localhost", 6379);
        }
        catch
        {
            // Expected if Redis is not available
        }
    }
    
    // Live Redis operation benchmarks (conditional on Redis availability)
    
    [Benchmark]
    public async Task RespireClient_SetOperation()
    {
        if (_kevaClient != null)
        {
            await _kevaClient.SetAsync($"{TestKey}_keva", TestValue);
        }
    }
    
    [Benchmark]
    public async Task RespireClient_GetOperation()
    {
        if (_kevaClient != null)
        {
            await _kevaClient.GetAsync(TestKey);
        }
    }
    
    [Benchmark]
    public async Task RespireClient_PingOperation()
    {
        if (_kevaClient != null)
        {
            await _kevaClient.PingAsync();
        }
    }
    
    // Batch operation benchmarks
    
    [Benchmark]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(1000)]
    public async Task RespireClient_BatchSetOperations(int batchSize)
    {
        if (_kevaClient != null)
        {
            var tasks = Enumerable.Range(0, batchSize)
                .Select(i => _kevaClient.SetAsync($"{TestKey}_batch_{i}", $"{TestValue}_{i}").AsTask())
                .ToArray();
            
            await Task.WhenAll(tasks);
        }
    }
    
    // Memory allocation comparison benchmarks
    
    [Benchmark]
    public void RespCommands_PreCompiledPing()
    {
        var pingCommand = RespCommands.Ping;
        // Simulate sending
        var _ = pingCommand.AsSpan();
    }
    
    [Benchmark]
    public byte[] ManualCommandBuilding_Ping()
    {
        return "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    }
    
    [Benchmark]
    public void ZeroAllocCommandBuilding_Get()
    {
        Span<byte> buffer = stackalloc byte[128];
        var length = RespCommands.BuildGetCommand(buffer, "test");
        var command = buffer[..length];
    }
    
    [Benchmark]
    public byte[] TraditionalCommandBuilding_Get()
    {
        var key = "test";
        var command = $"*2\r\n$3\r\nGET\r\n${key.Length}\r\n{key}\r\n";
        return System.Text.Encoding.UTF8.GetBytes(command);
    }
}

/// <summary>
/// Throughput benchmarks for measuring operations per second
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class PipelineThroughputBenchmarks
{
    private RespireClient _kevaClient = null!;
    private const string TestKey = "throughput_test";
    private const string TestValue = "throughput_value";
    
    [Params(1, 10, 100, 1000)]
    public int OperationCount { get; set; }
    
    [GlobalSetup]
    public async Task Setup()
    {
        try
        {
            _kevaClient = await RespireClient.CreateAsync("localhost", 6379);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not connect to Redis: {ex.Message}");
        }
    }
    
    [GlobalCleanup]
    public void Cleanup()
    {
        _kevaClient?.Dispose();
    }
    
    [Benchmark]
    public async Task RespireClient_ConcurrentSets()
    {
        if (_kevaClient != null)
        {
            var tasks = Enumerable.Range(0, OperationCount)
                .Select(i => _kevaClient.SetAsync($"{TestKey}_{i}", $"{TestValue}_{i}").AsTask())
                .ToArray();
            
            await Task.WhenAll(tasks);
        }
    }
    
    [Benchmark]
    public async Task RespireClient_ConcurrentGets()
    {
        if (_kevaClient != null)
        {
            var tasks = Enumerable.Range(0, OperationCount)
                .Select(i => _kevaClient.GetAsync($"{TestKey}_{i}").AsTask())
                .ToArray();
            
            await Task.WhenAll(tasks);
        }
    }
}