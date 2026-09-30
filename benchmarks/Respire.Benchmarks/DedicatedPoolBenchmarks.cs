using BenchmarkDotNet.Attributes;
using Respire.Internal;
using Respire.Networking;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Dedicated lease reuse, excluding Redis commands and connection establishment.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class DedicatedPoolBenchmarks
{
    private const int LeasesPerWorker = 256;
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.2-alpine").Build();
    private DedicatedConnectionPool _pool = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        await _redis.StartAsync();
        _pool = new DedicatedConnectionPool(_redis.Hostname, _redis.GetMappedPublicPort(6379),
            RespireConnectionOptions.Default, null);
        var connections = new RespireConnection[4];
        for (var i = 0; i < connections.Length; i++) connections[i] = await _pool.RentAsync(CancellationToken.None);
        foreach (var connection in connections) _pool.Return(connection);
        // Ensure setup produced a usable, reusable lease before any measurement.
        var rented = await _pool.RentAsync(CancellationToken.None);
        if (!rented.IsConnected) throw new InvalidOperationException("Dedicated lease is not connected.");
        _pool.Return(rented);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_pool is not null) await _pool.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Benchmark(OperationsPerInvoke = LeasesPerWorker)]
    public Task SequentialLeaseReuse() => ReuseAsync();

    // Task scheduling is included identically on both sides; report cost per lease.
    [Benchmark(OperationsPerInvoke = 4 * LeasesPerWorker)]
    public Task FourWorkerLeaseReuse()
        => Task.WhenAll(Task.Run(ReuseAsync), Task.Run(ReuseAsync), Task.Run(ReuseAsync), Task.Run(ReuseAsync));

    private async Task ReuseAsync()
    {
        for (var i = 0; i < LeasesPerWorker; i++)
        {
            var connection = await _pool.RentAsync(CancellationToken.None);
            _pool.Return(connection);
        }
    }
}
