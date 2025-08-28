using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using DotNet.Testcontainers.Containers;
using Respire.FastClient;
using Respire.Protocol;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
[Config(typeof(Config))]
public class RedisContainerBenchmarks
{
    private readonly IContainer _redisContainer = new RedisBuilder().Build();
    private RespireClient _kevaClient = null!;
    private ConnectionMultiplexer? _stackExchangeRedis;
    private IDatabase? _stackExchangeDb;
    
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
        
        // Setup Respire client
        _kevaClient = await RespireClient.CreateAsync("localhost", port);
        
        // Setup StackExchange.Redis
        _stackExchangeRedis = await ConnectionMultiplexer.ConnectAsync($"localhost:{port}");
        _stackExchangeDb = _stackExchangeRedis.GetDatabase();
        
        // Warm up with some test data
        await WarmupAsync();
    }
    
    private async Task WarmupAsync()
    {
        // Set up test data for multi-get operations
        for (var i = 0; i < _multiGetKeys.Length; i++)
        {
            await _kevaClient.SetAsync(_multiGetKeys[i], $"value{i}");
        }
        
        // Warm up connections
        for (var i = 0; i < 10; i++)
        {
            _kevaClient.Ping();
            await _stackExchangeDb.PingAsync();
        }
    }
    
    [GlobalCleanup]
    public async Task Cleanup()
    {
        _kevaClient?.Dispose();
        await _kevaClient.DisposeAsync();
        _stackExchangeRedis?.Dispose();
        await _redisContainer.StopAsync();
        await _redisContainer.DisposeAsync();
    }
    
    // PING benchmarks
    [Benchmark(Baseline = true)]
    public async Task<RespireValue> Respire_Ping()
    {
        return await _kevaClient.Ping();
    }
    
    [Benchmark]
    public async Task<TimeSpan> StackExchange_Ping()
    {
        return await _stackExchangeDb.PingAsync();
    }
    
    // SET benchmarks - Small value
    [Benchmark]
    public async Task Respire_Set_Small()
    {
        await _kevaClient.Set("benchmark_key", _smallValue);
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Set_Small()
    {
        return await _stackExchangeDb.StringSetAsync("benchmark_key", _smallValue);
    }
    
    // SET benchmarks - Medium value (1KB)
    [Benchmark]
    public async Task Respire_Set_Medium()
    {
        await _kevaClient.Set("benchmark_key_medium", _mediumValue);
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Set_Medium()
    {
        return await _stackExchangeDb.StringSetAsync("benchmark_key_medium", _mediumValue);
    }
    
    // SET benchmarks - Large value (10KB)
    [Benchmark]
    public async Task Respire_Set_Large()
    {
        await _kevaClient.Set("benchmark_key_large", _largeValue);
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Set_Large()
    {
        return await _stackExchangeDb.StringSetAsync("benchmark_key_large", _largeValue);
    }
    
    // GET benchmarks
    [Benchmark]
    public async Task<RespireValue> Respire_Get()
    {
        return await _kevaClient.Get("key0");
    }
    
    [Benchmark]
    public async Task<RedisValue> StackExchange_Get()
    {
        return await _stackExchangeDb.StringGetAsync("key0");
    }
    
    // MGET benchmarks (5 keys)
    // [Benchmark]
    // public string?[] Respire_MGet()
    // {
    //     return _kevaClient.MGet(_multiGetKeys);
    // }
    
    [Benchmark]
    public async Task<RedisValue[]> StackExchange_MGet()
    {
        var keys = _multiGetKeys.Select(k => (RedisKey)k).ToArray();
        return await _stackExchangeDb.StringGetAsync(keys);
    }
    
    // Pipeline benchmarks - 5 operations
    // [Benchmark]
    // public void Respire_Pipeline_5_Sets()
    // {
    //     _kevaClient.Pipeline(p =>
    //     {
    //         for (int i = 0; i < 5; i++)
    //         {
    //             p.Set($"pipeline_key_{i}", $"pipeline_value_{i}");
    //         }
    //     });
    // }
    
    [Benchmark]
    public async Task StackExchange_Pipeline_5_Sets()
    {
        var batch = _stackExchangeDb.CreateBatch();
        var tasks = new Task[5];
        for (var i = 0; i < 5; i++)
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
    public async Task<RespireValue> Respire_Exists()
    {
        return await _kevaClient.Exists("key0");
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Exists()
    {
        return await _stackExchangeDb.KeyExistsAsync("key0");
    }
    
    // DEL benchmarks
    [Benchmark]
    public async Task Respire_Del()
    {
        await _kevaClient.Set("temp_key", "temp_value");
        await _kevaClient.Del("temp_key");
    }
    
    [Benchmark]
    public async Task<bool> StackExchange_Del()
    {
        await _stackExchangeDb.StringSetAsync("temp_key", "temp_value");
        return await _stackExchangeDb.KeyDeleteAsync("temp_key");
    }
    
    // INCR benchmarks
    [Benchmark]
    public async Task<RespireValue> Respire_Incr()
    {
        return await _kevaClient.Incr("counter");
    }
    
    [Benchmark]
    public async Task<long> StackExchange_Incr()
    {
        return await _stackExchangeDb.StringIncrementAsync("counter");
    }
}