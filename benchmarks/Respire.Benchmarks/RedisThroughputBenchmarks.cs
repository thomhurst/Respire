using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using DotNet.Testcontainers.Containers;
using Respire.FastClient;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 1, iterationCount: 3)]
[Config(typeof(Config))]
public class RedisThroughputBenchmarks
{
    private readonly IContainer _redisContainer = new RedisBuilder().Build();
    private RespireClient _kevaClient = null!;
    private ConnectionMultiplexer _stackExchangeRedis = null!;
    private IDatabase _stackExchangeDb = null!;
    
    [Params(1, 10)]
    public int ConcurrentClients { get; set; }
    
    [Params(100)]
    public int OperationsPerClient { get; set; }
    
    private class Config : ManualConfig
    {
        public Config()
        {
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Mean);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P95);
            AddColumn(new ThroughputColumn());
        }
    }
    
    private class ThroughputColumn : IColumn
    {
        public string Id => nameof(ThroughputColumn);
        public string ColumnName => "Ops/sec";
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;
        public string Legend => "Operations per second";
        
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            var report = summary[benchmarkCase];
            if (report == null || !report.Success)
            {
                return "N/A";
            }

            var meanNs = report.ResultStatistics?.Mean ?? 0;
            if (meanNs == 0)
            {
                return "N/A";
            }

            var opsPerSec = 1_000_000_000.0 / meanNs;
            return $"{opsPerSec:N0}";
        }
        
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) 
            => GetValue(summary, benchmarkCase);
        
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
        public bool IsAvailable(Summary summary) => true;
    }
    
    [GlobalSetup]
    public async Task Setup()
    {
        await _redisContainer.StartAsync();
        
        var port = _redisContainer.GetMappedPublicPort(6379);
        
        // Setup Respire client with larger pool for concurrent operations
        _kevaClient = await RespireClient.CreateAsync("localhost", port, connectionCount: 10);
        
        // Setup StackExchange.Redis with similar configuration
        var options = ConfigurationOptions.Parse($"localhost:{port}");
        options.ConnectTimeout = 5000;
        options.SyncTimeout = 5000;
        options.AsyncTimeout = 5000;
        _stackExchangeRedis = await ConnectionMultiplexer.ConnectAsync(options);
        _stackExchangeDb = _stackExchangeRedis.GetDatabase();
        
        // Warm up
        for (var i = 0; i < 100; i++)
        {
            await _kevaClient.SetAsync($"warmup_{i}", $"value_{i}");
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
    
    [Benchmark(Baseline = true)]
    public async Task Respire_Concurrent_SetGet()
    {
        var tasks = new Task[ConcurrentClients];
        
        for (var client = 0; client < ConcurrentClients; client++)
        {
            var clientId = client;
            tasks[client] = Task.Run(async () =>
            {
                for (var op = 0; op < OperationsPerClient; op++)
                {
                    var key = $"client_{clientId}_op_{op}";
                    await _kevaClient.SetAsync(key, $"value_{op}");
                    await _kevaClient.GetAsync(key);
                }
            });
        }
        
        await Task.WhenAll(tasks);
    }
    
    [Benchmark]
    public async Task StackExchange_Concurrent_SetGet()
    {
        var tasks = new Task[ConcurrentClients];
        
        for (var client = 0; client < ConcurrentClients; client++)
        {
            var clientId = client;
            tasks[client] = Task.Run(async () =>
            {
                for (var op = 0; op < OperationsPerClient; op++)
                {
                    var key = $"client_{clientId}_op_{op}";
                    await _stackExchangeDb.StringSetAsync(key, $"value_{op}");
                    await _stackExchangeDb.StringGetAsync(key);
                }
            });
        }
        
        await Task.WhenAll(tasks);
    }
    
    [Benchmark]
    public async Task Respire_Concurrent_Incr()
    {
        var tasks = new Task[ConcurrentClients];
        
        for (var client = 0; client < ConcurrentClients; client++)
        {
            var clientId = client;
            tasks[client] = Task.Run(async () =>
            {
                var key = $"counter_{clientId}";
                for (var op = 0; op < OperationsPerClient; op++)
                {
                    await _kevaClient.IncrAsync(key);
                }
            });
        }
        
        await Task.WhenAll(tasks);
    }
    
    [Benchmark]
    public async Task StackExchange_Concurrent_Incr()
    {
        var tasks = new Task[ConcurrentClients];
        
        for (var client = 0; client < ConcurrentClients; client++)
        {
            var clientId = client;
            tasks[client] = Task.Run(async () =>
            {
                var key = $"counter_{clientId}";
                for (var op = 0; op < OperationsPerClient; op++)
                {
                    await _stackExchangeDb.StringIncrementAsync(key);
                }
            });
        }
        
        await Task.WhenAll(tasks);
    }
}