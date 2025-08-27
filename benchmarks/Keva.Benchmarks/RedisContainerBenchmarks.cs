using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Keva.Client;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Keva.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
[Config(typeof(Config))]
public class RedisContainerBenchmarks
{
    private readonly IContainer _redisContainer = new RedisBuilder().Build();
    private IKevaClient _kevaClient = null!;
    private ConnectionMultiplexer _stackExchangeRedis = null!;
    private IDatabase _stackExchangeDb = null!;
    
    private readonly string _smallValue = "Hello, World!";
    private readonly string _mediumValue = new string('X', 1024); // 1KB
    private readonly string _largeValue = new string('Y', 10240); // 10KB
    
    private readonly string[] _multiGetKeys = Enumerable.Range(0, 5).Select(i => $"key{i}").ToArray();
    
    private class Config : ManualConfig
    {
        public Config()
        {
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Mean);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P95);
        }
    }
    
    [GlobalSetup]
    public async Task Setup()
    {
        await _redisContainer.StartAsync();
        
        var port = _redisContainer.GetMappedPublicPort(6379);
        
        // Setup Keva client
        _kevaClient = KevaClientBuilder.Create()
            .UseEndpoint("localhost", port)
            .EnableAutoReconnect()
            .SetPoolSize(min: 5, max: 10)
            .Build();
        
        // Setup StackExchange.Redis
        _stackExchangeRedis = await ConnectionMultiplexer.ConnectAsync($"localhost:{port}");
        _stackExchangeDb = _stackExchangeRedis.GetDatabase();
        
        // Warm up with some test data
        await WarmupAsync();
    }
    
    private async Task WarmupAsync()
    {
        // Set up test data for multi-get operations
        for (int i = 0; i < _multiGetKeys.Length; i++)
        {
            await _kevaClient.SetAsync(_multiGetKeys[i], $"value{i}");
        }
        
        // Warm up connections
        for (int i = 0; i < 10; i++)
        {
            await _kevaClient.PingAsync();
            await _stackExchangeDb.PingAsync();
        }
    }
    
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _kevaClient.DisposeAsync();
        _stackExchangeRedis?.Dispose();
        await _redisContainer.StopAsync();
        await _redisContainer.DisposeAsync();
    }
    
    // PING benchmarks
    [Benchmark(Baseline = true)]
    public async Task<bool> Keva_Ping()
    {
        return await _kevaClient.PingAsync();
    }
    
    [Benchmark]
    public async Task<TimeSpan> StackExchange_Ping()
    {
        return await _stackExchangeDb.PingAsync();
    }
    
    // SET benchmarks - Small value
    [Benchmark]
    public async Task<bool> Keva_Set_Small()
    {
        return await _kevaClient.SetAsync("benchmark_key", _smallValue);
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Set_Small()
    {
        return await _stackExchangeDb.StringSetAsync("benchmark_key", _smallValue);
    }
    
    // SET benchmarks - Medium value (1KB)
    [Benchmark]
    public async Task<bool> Keva_Set_Medium()
    {
        return await _kevaClient.SetAsync("benchmark_key_medium", _mediumValue);
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Set_Medium()
    {
        return await _stackExchangeDb.StringSetAsync("benchmark_key_medium", _mediumValue);
    }
    
    // SET benchmarks - Large value (10KB)
    [Benchmark]
    public async Task<bool> Keva_Set_Large()
    {
        return await _kevaClient.SetAsync("benchmark_key_large", _largeValue);
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Set_Large()
    {
        return await _stackExchangeDb.StringSetAsync("benchmark_key_large", _largeValue);
    }
    
    // GET benchmarks
    [Benchmark]
    public async Task<string?> Keva_Get()
    {
        return await _kevaClient.GetAsync("key0");
    }
    
    [Benchmark]
    public async Task<RedisValue> StackExchange_Get()
    {
        return await _stackExchangeDb.StringGetAsync("key0");
    }
    
    // MGET benchmarks (5 keys)
    [Benchmark]
    public async Task<string?[]> Keva_MGet()
    {
        return await _kevaClient.MGetAsync(_multiGetKeys);
    }
    
    [Benchmark]
    public async Task<RedisValue[]> StackExchange_MGet()
    {
        var keys = _multiGetKeys.Select(k => (RedisKey)k).ToArray();
        return await _stackExchangeDb.StringGetAsync(keys);
    }
    
    // Pipeline benchmarks - 5 operations
    [Benchmark]
    public async Task Keva_Pipeline_5_Sets()
    {
        var tasks = new Task[5];
        for (int i = 0; i < 5; i++)
        {
            var key = $"pipeline_key_{i}";
            var value = $"pipeline_value_{i}";
            tasks[i] = _kevaClient.SetAsync(key, value).AsTask();
        }
        await Task.WhenAll(tasks);
    }
    
    [Benchmark]
    public async Task StackExchange_Pipeline_5_Sets()
    {
        var batch = _stackExchangeDb.CreateBatch();
        var tasks = new Task[5];
        for (int i = 0; i < 5; i++)
        {
            var key = $"pipeline_key_{i}";
            var value = $"pipeline_value_{i}";
            tasks[i] = batch.StringSetAsync(key, value);
        }
        batch.Execute();
        await Task.WhenAll(tasks);
    }
    
    // EXISTS benchmarks
    [Benchmark]
    public async Task<bool> Keva_Exists()
    {
        return await _kevaClient.ExistsAsync("key0");
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Exists()
    {
        return await _stackExchangeDb.KeyExistsAsync("key0");
    }
    
    // DEL benchmarks
    [Benchmark]
    public async Task<bool> Keva_Del()
    {
        await _kevaClient.SetAsync("temp_key", "temp_value");
        return await _kevaClient.DelAsync("temp_key");
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Del()
    {
        await _stackExchangeDb.StringSetAsync("temp_key", "temp_value");
        return await _stackExchangeDb.KeyDeleteAsync("temp_key");
    }
    
    // INCR benchmarks
    [Benchmark]
    public async Task<long> Keva_Incr()
    {
        return await _kevaClient.IncrAsync("counter");
    }
    
    [Benchmark]
    public async Task<long> StackExchange_Incr()
    {
        return await _stackExchangeDb.StringIncrementAsync("counter");
    }
}