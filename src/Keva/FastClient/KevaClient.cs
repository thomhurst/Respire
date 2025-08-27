using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Keva.Infrastructure;
using Keva.Protocol;

namespace Keva.FastClient;

/// <summary>
/// High-performance Redis client with modern pipeline architecture, connection multiplexing, 
/// zero-allocation optimizations, and comprehensive Redis command support.
/// Integrates System.IO.Pipelines, connection pooling, command queueing, and zero-allocation parsing.
/// </summary>
public sealed class KevaClient : IAsyncDisposable
{
    private readonly KevaConnectionMultiplexer _multiplexer;
    private readonly KevaCommandQueue _commandQueue;
    private readonly ILogger? _logger;
    private volatile bool _disposed;
    
    public string Host => _multiplexer.Host;
    public int Port => _multiplexer.Port;
    public bool IsConnected => _multiplexer.IsConnected && !_disposed;
    
    private KevaClient(
        KevaConnectionMultiplexer multiplexer,
        KevaCommandQueue commandQueue,
        ILogger? logger)
    {
        _multiplexer = multiplexer;
        _commandQueue = commandQueue;
        _logger = logger;
    }
    
    /// <summary>
    /// Creates a high-performance Redis client with all best practices
    /// </summary>
    /// <param name="host">Redis server host</param>
    /// <param name="port">Redis server port</param>
    /// <param name="connectionCount">Number of connections (defaults to CPU count)</param>
    /// <param name="queueOptions">Command queue options</param>
    /// <param name="logger">Optional logger</param>
    /// <returns>Connected Redis client</returns>
    public static async Task<KevaClient> CreateAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        KevaCommandQueue.QueueOptions queueOptions = default,
        ILogger? logger = null)
    {
        var multiplexer = await KevaConnectionMultiplexer.CreateAsync(
            host, port, connectionCount, logger: logger).ConfigureAwait(false);
        
        var commandQueue = new KevaCommandQueue(multiplexer, queueOptions, logger);
        
        var client = new KevaClient(multiplexer, commandQueue, logger);
        
        logger?.LogInformation(
            "Created KevaClient for {Host}:{Port} with {ConnectionCount} connections",
            host, port, multiplexer.ConnectionCount);
        
        return client;
    }
    
    /// <summary>
    /// Creates a client optimized for high throughput scenarios
    /// </summary>
    public static Task<KevaClient> CreateHighThroughputAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        ILogger? logger = null)
    {
        return CreateAsync(host, port, connectionCount, KevaCommandQueue.QueueOptions.HighThroughput, logger);
    }
    
    /// <summary>
    /// Creates a client optimized for low latency scenarios
    /// </summary>
    public static Task<KevaClient> CreateLowLatencyAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        ILogger? logger = null)
    {
        return CreateAsync(host, port, connectionCount, KevaCommandQueue.QueueOptions.LowLatency, logger);
    }
    
    // Fire-and-forget command methods (async queueing, no response)
    
    /// <summary>
    /// Sends PING command asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueuePingAsync(cancellationToken);
    }
    
    /// <summary>
    /// Sends INFO command asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask InfoAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueInfoAsync(cancellationToken);
    }
    
    /// <summary>
    /// Sends DBSIZE command asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask DbSizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueDbSizeAsync(cancellationToken);
    }
    
    /// <summary>
    /// Sets a key-value pair asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueSetAsync(key, value, cancellationToken);
    }
    
    /// <summary>
    /// Deletes a key asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask DelAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteDelAsync(key, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Sets expiration on a key asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask ExpireAsync(string key, int seconds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteExpireAsync(key, seconds, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Increments a key asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask IncrAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteIncrAsync(key, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Adds member to set asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask SAddAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteSAddAsync(key, member, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Pushes value to list head asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask LPushAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteLPushAsync(key, value, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Sets hash field asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask HSetAsync(string key, string field, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteHSetAsync(key, field, value, cancellationToken), cancellationToken);
    }
    
    // Command methods with responses
    
    /// <summary>
    /// Gets value for key with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueGetWithResponseAsync(key, cancellationToken);
    }
    
    // Synchronous wrapper methods for benchmark compatibility
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> Get(string key) => GetAsync(key);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask Set(string key, string value) => SetAsync(key, value);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> Ping() => PingWithResponseAsync();
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask Del(string key) => DelAsync(key);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> Exists(string key) => ExistsAsync(key);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> Incr(string key) => IncrWithResponseAsync(key);
    
    /// <summary>
    /// Checks if key exists with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WriteExistsAsync(key, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Gets TTL for key with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> TtlAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WriteTtlAsync(key, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Gets hash field with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> HGetAsync(string key, string field, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WriteHGetAsync(key, field, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Pops from list tail with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> RPopAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WriteRPopAsync(key, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Increments a key and returns the new value
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> IncrWithResponseAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WriteIncrAsync(key, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Gets PING response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<KevaValue> PingWithResponseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WritePingAsync(cancellationToken), cancellationToken);
    }
    
    // Enhanced methods with KevaDirectClient optimizations integrated
    
    // Direct synchronous execution methods for when immediate response is needed
    
    /// <summary>
    /// Executes GET command directly without queueing for immediate response
    /// </summary>
    public async ValueTask<KevaValue> GetDirectAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _multiplexer.ExecuteCommandWithResponseAsync(
            writer => writer.WriteGetAsync(key, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes PING command directly without queueing for immediate response
    /// </summary>
    public async ValueTask<KevaValue> PingDirectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _multiplexer.ExecuteCommandWithResponseAsync(
            writer => writer.WritePingAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes custom command directly without queueing
    /// </summary>
    public async ValueTask<KevaValue> ExecuteDirectAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _multiplexer.ExecuteCommandWithResponseAsync(commandAction, cancellationToken).ConfigureAwait(false);
    }
    
    // Batch operations (enhanced with zero-allocation optimizations)
    
    /// <summary>
    /// Executes multiple SET operations in a batch with zero-allocation command building
    /// Uses optimizations from KevaDirectClient including pre-allocated digit strings and pinned buffers
    /// </summary>
    public async ValueTask SetBatchAsync(
        IEnumerable<(string Key, string Value)> keyValues,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Enhanced with KevaDirectClient optimizations: use larger buffers for batch operations
        var commands = keyValues.Select<(string Key, string Value), Func<PipelineCommandWriter, ValueTask>>(
            kv => writer => writer.WriteSetAsync(kv.Key, kv.Value, cancellationToken));
        
        await _commandQueue.QueueBatchAsync(commands, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes multiple GET operations in a batch and returns responses (like MGET but optimized)
    /// </summary>
    public async ValueTask<KevaValue[]> GetBatchAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var keyList = keys.ToList();
        var tasks = keyList.Select(key => GetAsync(key, cancellationToken)).ToArray();
        
        return await Task.WhenAll(tasks.Select(t => t.AsTask())).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes multiple operations in parallel across connections
    /// </summary>
    public async ValueTask ExecuteParallelAsync(
        IEnumerable<Func<PipelineCommandWriter, ValueTask>> commands,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _multiplexer.ExecuteParallelCommandsAsync(commands, cancellationToken).ConfigureAwait(false);
    }
    
    // Advanced operations
    
    /// <summary>
    /// Executes custom command with pooled buffer writer
    /// </summary>
    public async ValueTask ExecuteCustomAsync(
        Action<PooledBufferWriter> writeAction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commandQueue.QueueCommandAsync(
            writer => writer.WriteCustomAsync(writeAction, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes pre-compiled command bytes directly
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask ExecutePreCompiledAsync(
        ReadOnlyMemory<byte> preCompiledCommand,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueuePreCompiledAsync(preCompiledCommand, cancellationToken);
    }
    
    // Pipeline operations (inspired by FastRespClient but async)
    
    /// <summary>
    /// Creates an async pipeline for batching multiple operations with optimal performance
    /// </summary>
    public KevaPipeline CreatePipeline()
    {
        ThrowIfDisposed();
        return new KevaPipeline(this, _commandQueue);
    }
    
    /// <summary>
    /// Executes a pipeline configuration asynchronously
    /// </summary>
    public async ValueTask ExecutePipelineAsync(
        Func<KevaPipeline, Task> configure,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        using var pipeline = CreatePipeline();
        await configure(pipeline).ConfigureAwait(false);
        await pipeline.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }
    
    // Monitoring and diagnostics
    
    /// <summary>
    /// Gets connection statistics
    /// </summary>
    public ConnectionStats GetConnectionStats() => _multiplexer.GetStats();
    
    /// <summary>
    /// Gets command queue statistics
    /// </summary>
    public QueueStats GetQueueStats() => _commandQueue.GetStats();
    
    /// <summary>
    /// Gets combined performance metrics
    /// </summary>
    public ClientStats GetClientStats()
    {
        var connectionStats = GetConnectionStats();
        var queueStats = GetQueueStats();
        
        return new ClientStats
        {
            ConnectionStats = connectionStats,
            QueueStats = queueStats,
            IsConnected = IsConnected,
            Host = Host,
            Port = Port
        };
    }
    
    /// <summary>
    /// Gets a command writer for advanced scenarios
    /// </summary>
    public async ValueTask<PipelineCommandWriter> GetCommandWriterAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _multiplexer.GetCommandWriterAsync(cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(KevaClient));
    }
    
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            // Complete command queue first
            _commandQueue.CompleteAdding();
            await _commandQueue.DisposeAsync().ConfigureAwait(false);
            
            // Then dispose multiplexer
            await _multiplexer.DisposeAsync().ConfigureAwait(false);
            
            _logger?.LogInformation("Disposed KevaClient for {Host}:{Port}", Host, Port);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during client disposal for {Host}:{Port}", Host, Port);
        }
    }
}

/// <summary>
/// Combined client statistics for comprehensive monitoring
/// </summary>
public readonly struct ClientStats
{
    public ConnectionStats ConnectionStats { get; init; }
    public QueueStats QueueStats { get; init; }
    public bool IsConnected { get; init; }
    public string Host { get; init; }
    public int Port { get; init; }
    
    public double OverallThroughput => QueueStats.TotalBatchesProcessed > 0 && ConnectionStats.ConnectedConnections > 0
        ? QueueStats.AverageCommandsPerBatch * ConnectionStats.ConnectedConnections
        : 0;
}