using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Protocol;
using Respire.Commands;

namespace Respire.FastClient;

/// <summary>
/// High-performance Redis client with modern pipeline architecture, connection multiplexing, 
/// zero-allocation optimizations, and comprehensive Redis command support.
/// Integrates System.IO.Pipelines, connection pooling, command queueing, and zero-allocation parsing.
/// </summary>
public sealed class RespireClient : IAsyncDisposable
{
    private readonly RespireConnectionMultiplexer _multiplexer;
    private readonly IRespireCommandQueue _commandQueue;
    private readonly ILogger? _logger;
    private volatile bool _disposed;
    
    // Direct execution for GET operations
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _preEncodedGetCommands = new();
    private const int MaxPreEncodedCommands = 1000;
    
    public string Host => _multiplexer.Host;
    public int Port => _multiplexer.Port;
    public bool IsConnected => _multiplexer.IsConnected && !_disposed;
    
    private RespireClient(
        RespireConnectionMultiplexer multiplexer,
        IRespireCommandQueue commandQueue,
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
    public static async Task<RespireClient> CreateAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        RespireCommandQueue.QueueOptions? queueOptions = null,
        ILogger? logger = null)
    {
        var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
            host, port, connectionCount, logger: logger).ConfigureAwait(false);
        
        // Use the pipelined queue for proper batching
        var options = queueOptions ?? RespireCommandQueue.QueueOptions.Default;
        IRespireCommandQueue commandQueue = new RespireCommandQueuePipelined(
            multiplexer, 
            maxBatchSize: options.BatchSize, 
            batchTimeout: options.BatchTimeout,
            tcsPoolSize: options.TcsPoolSize,
            logger);
        
        var client = new RespireClient(multiplexer, commandQueue, logger);
        
        logger?.LogInformation(
            "Created RespireClient for {Host}:{Port} with {ConnectionCount} connections",
            host, port, multiplexer.ConnectionCount);
        
        return client;
    }
    
    /// <summary>
    /// Creates a client configured for high throughput scenarios
    /// </summary>
    public static Task<RespireClient> CreateHighThroughputAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        ILogger? logger = null)
    {
        return CreateAsync(host, port, connectionCount, RespireCommandQueue.QueueOptions.HighThroughput, logger);
    }
    
    /// <summary>
    /// Creates a client configured for low latency scenarios
    /// </summary>
    public static Task<RespireClient> CreateLowLatencyAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        ILogger? logger = null)
    {
        return CreateAsync(host, port, connectionCount, RespireCommandQueue.QueueOptions.LowLatency, logger);
    }
    
    // Fire-and-forget command methods (async queueing, no response)
    
    /// <summary>
    /// Sends PING command asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.PingCommand(cancellationToken);
        return _commandQueue.QueueCommandAsync(command.ExecuteAsync, cancellationToken);
    }
    
    /// <summary>
    /// Sends INFO command asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask InfoAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteInfoAsync(cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Sends DBSIZE command asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask DbSizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteDbSizeAsync(cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Sets a key-value pair asynchronously
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.SetCommand(key, value, cancellationToken);
        var response = await _commandQueue.QueueCommandWithResponseAsync(
            command.ExecuteAsync, cancellationToken).ConfigureAwait(false);
        
        // Verify we got "OK" response
        if (!response.Type.Equals(RespDataType.SimpleString) || !response.AsString().Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SET command failed. Expected 'OK' but got: {response}");
        }
    }
    
    /// <summary>
    /// Deletes a key asynchronously
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> DelAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.DelCommand(key, cancellationToken);
        var response = await _commandQueue.QueueCommandWithResponseAsync(
            command.ExecuteAsync, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
    }
    
    /// <summary>
    /// Sets expiration on a key asynchronously
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<bool> ExpireAsync(string key, int seconds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var response = await _commandQueue.QueueCommandWithResponseAsync(
            writer => writer.WriteExpireAsync(key, seconds, cancellationToken), cancellationToken).ConfigureAwait(false);
        return response.AsInteger() == 1;
    }
    
    /// <summary>
    /// Increments a key asynchronously
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<long> IncrAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.IncrCommand(key, cancellationToken);
        var response = await _commandQueue.QueueCommandWithResponseAsync(
            command.ExecuteAsync, cancellationToken).ConfigureAwait(false);
        return response.AsInteger();
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
    /// Removes member from set asynchronously (fire-and-forget)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask SRemAsync(string key, string member, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandAsync(writer => writer.WriteSRemAsync(key, member, cancellationToken), cancellationToken);
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
    public ValueTask<RespireValue> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // All commands must go through the queue to avoid deadlocks
        var command = new PooledCommands.GetCommand(key, cancellationToken);
        return _commandQueue.QueueCommandWithResponseAsync(
            command.ExecuteAsync, 
            cancellationToken);
    }
    
    /// <summary>
    /// Executes GET operation directly on connection
    /// Uses ConnectionLease to avoid boxing and async state machines
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<RespireValue> ExecuteGetDirectAsync(string key, CancellationToken cancellationToken)
    {
        // Try to use pre-encoded command if available for minimal allocations
        if (_preEncodedGetCommands.TryGetValue(key, out var encodedCommand))
        {
            return ExecutePreEncodedGetDirectAsync(encodedCommand, cancellationToken);
        }
        
        // Get connection lease - often completes synchronously!
        var leaseTask = _multiplexer.GetConnectionLeaseAsync(cancellationToken);
        
        if (leaseTask.IsCompletedSuccessfully)
        {
            // Synchronous path - no async state machine!
            var lease = leaseTask.Result;
            return ExecuteGetWithLeaseAsync(lease, key, cancellationToken);
        }
        
        // Async fallback
        return ExecuteGetDirectAsyncSlow(leaseTask.AsTask(), key, cancellationToken);
    }
    
    private async ValueTask<RespireValue> ExecuteGetDirectAsyncSlow(Task<Infrastructure.ConnectionLease> leaseTask, string key, CancellationToken cancellationToken)
    {
        var lease = await leaseTask.ConfigureAwait(false);
        return await ExecuteGetWithLeaseAsync(lease, key, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async ValueTask<RespireValue> ExecuteGetWithLeaseAsync(Infrastructure.ConnectionLease lease, string key, CancellationToken cancellationToken)
    {
        try
        {
            // With C# 13, we can use ref structs in async methods!
            using var bufferWriter = RespireMemoryPool.Shared.CreateBufferWriter();
            
            // Build command directly into pooled buffer
            var span = bufferWriter.GetSpan(32 + key.Length);
            var written = Protocol.RespCommands.BuildGetCommandSpan(span, key);
            bufferWriter.Advance(written);
            
            // Get the written memory for sending and potential caching
            var commandMemory = bufferWriter.WrittenMemory;
            
            // Write directly without additional allocation
            var writeTask = lease.Connection.WritePreCompiledCommandAsync(commandMemory, cancellationToken);
            
            // Cache for future use if appropriate (do this while write is in progress)
            if (_preEncodedGetCommands.Count < MaxPreEncodedCommands && key.Length <= 128)
            {
                var commandBytes = new byte[commandMemory.Length];
                commandMemory.CopyTo(commandBytes);
                _preEncodedGetCommands.TryAdd(key, commandBytes);
            }
            
            // Wait for write to complete if needed
            if (!writeTask.IsCompletedSuccessfully)
            {
                await writeTask.ConfigureAwait(false);
            }
            
            // Read response - keep reading until we have a complete response
            while (true)
            {
                var readResult = await lease.Connection.ReadAsync(cancellationToken).ConfigureAwait(false);
                
                // Parse response
                var value = ParseRespResponse(readResult.Buffer, lease.Connection);
                if (!value.Type.Equals(RespDataType.None))
                {
                    return value;
                }
                
                // Check if connection was closed
                if (readResult.IsCompleted)
                {
                    throw new InvalidOperationException("Connection closed before complete response received");
                }
                
                // Continue reading more data
            }
        }
        finally
        {
            lease.Return();
        }
    }
    
    /// <summary>
    /// Direct execution for pre-encoded GET commands using ConnectionLease
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<RespireValue> ExecutePreEncodedGetDirectAsync(byte[] encodedCommand, CancellationToken cancellationToken)
    {
        // Get connection lease - often completes synchronously!
        var leaseTask = _multiplexer.GetConnectionLeaseAsync(cancellationToken);
        
        if (leaseTask.IsCompletedSuccessfully)
        {
            // Synchronous path - no async state machine!
            var lease = leaseTask.Result;
            return ExecutePreEncodedWithLeaseAsync(lease, encodedCommand, cancellationToken);
        }
        
        // Async fallback
        return ExecutePreEncodedGetAsyncSlow(leaseTask.AsTask(), encodedCommand, cancellationToken);
    }
    
    private async ValueTask<RespireValue> ExecutePreEncodedGetAsyncSlow(Task<Infrastructure.ConnectionLease> leaseTask, byte[] encodedCommand, CancellationToken cancellationToken)
    {
        var lease = await leaseTask.ConfigureAwait(false);
        return await ExecutePreEncodedWithLeaseAsync(lease, encodedCommand, cancellationToken).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async ValueTask<RespireValue> ExecutePreEncodedWithLeaseAsync(Infrastructure.ConnectionLease lease, byte[] encodedCommand, CancellationToken cancellationToken)
    {
        try
        {
            // Write pre-encoded bytes directly
            var writeTask = lease.Connection.WritePreCompiledCommandAsync(encodedCommand, cancellationToken);
            
            if (!writeTask.IsCompletedSuccessfully)
            {
                await writeTask.ConfigureAwait(false);
            }
            
            // Read and parse response - keep reading until complete
            while (true)
            {
                var readResult = await lease.Connection.ReadAsync(cancellationToken).ConfigureAwait(false);
                var reader = new Protocol.RespPipelineReader(readResult.Buffer);
                
                if (reader.TryReadValue(out var value))
                {
                    lease.Connection.AdvanceReader(reader.Consumed, reader.Examined);
                    return value;
                }
                
                if (readResult.IsCompleted)
                {
                    lease.Connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
                    throw new InvalidOperationException("Connection closed before complete response received");
                }
                
                lease.Connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
            }
        }
        finally
        {
            lease.Return();
        }
    }
    
    /// <summary>
    /// Optimized path for pre-encoded GET commands (legacy, for compatibility)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async ValueTask<RespireValue> ExecutePreEncodedGetAsync(byte[] encodedCommand, CancellationToken cancellationToken)
    {
        // Get connection directly for pre-encoded commands
        using var handle = await _multiplexer.GetConnectionHandleAsync(cancellationToken).ConfigureAwait(false);
        
        // Write pre-encoded bytes directly without any allocations
        await handle.Connection.WritePreCompiledCommandAsync(encodedCommand, cancellationToken).ConfigureAwait(false);
        
        // Read and parse response - keep reading until complete
        while (true)
        {
            var readResult = await handle.Connection.ReadAsync(cancellationToken).ConfigureAwait(false);
            var reader = new Protocol.RespPipelineReader(readResult.Buffer);
            
            if (reader.TryReadValue(out var value))
            {
                handle.Connection.AdvanceReader(reader.Consumed, reader.Examined);
                return value;
            }
            
            if (readResult.IsCompleted)
            {
                handle.Connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
                throw new InvalidOperationException("Connection closed before complete response received");
            }
            
            handle.Connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
        }
    }
    
    
    /// <summary>
    /// Parses RESP response from buffer
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RespireValue ParseRespResponse(ReadOnlySequence<byte> buffer, Infrastructure.PipelineConnection connection)
    {
        var reader = new Protocol.RespPipelineReader(buffer);
        
        if (reader.TryReadValue(out var value))
        {
            connection.AdvanceReader(reader.Consumed, reader.Examined);
            return value;
        }
        
        // Incomplete response - mark as examined but not consumed
        // The caller should continue reading more data
        connection.AdvanceReader(buffer.Start, buffer.End);
        return default;
    }
    
    // Synchronous wrapper methods for benchmark compatibility
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> Get(string key) => GetAsync(key);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask Set(string key, string value) => SetAsync(key, value);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> Ping() => PingWithResponseAsync();
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<long> Del(string key) => DelAsync(key);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> Exists(string key) => ExistsAsync(key);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> Incr(string key) => IncrWithResponseAsync(key);
    
    /// <summary>
    /// Checks if key exists with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.ExistsCommand(key, cancellationToken);
        return _commandQueue.QueueCommandWithResponseAsync(command.ExecuteAsync, cancellationToken);
    }
    
    /// <summary>
    /// Gets TTL for key with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> TtlAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WriteTtlAsync(key, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Gets hash field with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> HGetAsync(string key, string field, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WriteHGetAsync(key, field, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Pops from list tail with response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> RPopAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _commandQueue.QueueCommandWithResponseAsync(writer => writer.WriteRPopAsync(key, cancellationToken), cancellationToken);
    }
    
    /// <summary>
    /// Increments a key and returns the new value
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> IncrWithResponseAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.IncrCommand(key, cancellationToken);
        return _commandQueue.QueueCommandWithResponseAsync(command.ExecuteAsync, cancellationToken);
    }
    
    /// <summary>
    /// Gets PING response
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RespireValue> PingWithResponseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.PingCommand(cancellationToken);
        return _commandQueue.QueueCommandWithResponseAsync(command.ExecuteAsync, cancellationToken);
    }
    
    // Enhanced methods with RespireDirectClient optimizations integrated
    
    // Direct synchronous execution methods for when immediate response is needed
    
    /// <summary>
    /// Executes GET command directly without queueing for immediate response
    /// </summary>
    public async ValueTask<RespireValue> GetDirectAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.GetCommand(key, cancellationToken);
        return await _multiplexer.ExecuteCommandWithResponseAsync(
            command.ExecuteAsync, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes PING command directly without queueing for immediate response
    /// </summary>
    public async ValueTask<RespireValue> PingDirectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var command = new PooledCommands.PingCommand(cancellationToken);
        return await _multiplexer.ExecuteCommandWithResponseAsync(
            command.ExecuteAsync, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes custom command directly without queueing
    /// </summary>
    public async ValueTask<RespireValue> ExecuteDirectAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _multiplexer.ExecuteCommandWithResponseAsync(commandAction, cancellationToken).ConfigureAwait(false);
    }
    
    // Batch operations (enhanced with zero-allocation optimizations)
    
    /// <summary>
    /// Executes multiple SET operations in a batch with zero-allocation command building
    /// Uses ArrayPool for temporary arrays and avoids LINQ allocations
    /// </summary>
    public async ValueTask SetBatchAsync(
        IEnumerable<(string Key, string Value)> keyValues,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Convert to array using ArrayPool to avoid allocations
        var keyValueList = keyValues as IList<(string Key, string Value)> ?? keyValues.ToList();
        var count = keyValueList.Count;
        
        if (count == 0) return;
        
        // Use ArrayPool for temporary command storage to reduce allocations
        var commands = ArrayPool<PooledCommands.SetCommand>.Shared.Rent(count);
        
        try
        {
            // Pre-create all SetCommand structs
            for (var i = 0; i < count; i++)
            {
                var kv = keyValueList[i];
                commands[i] = new PooledCommands.SetCommand(kv.Key, kv.Value, cancellationToken);
            }
            
            // Queue each command individually - they will be batched by the pipelined queue
            for (var i = 0; i < count; i++)
            {
                await _commandQueue.QueueCommandAsync(commands[i].ExecuteAsync, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<PooledCommands.SetCommand>.Shared.Return(commands, clearArray: true);
        }
    }
    
    /// <summary>
    /// Executes multiple GET operations in a batch and returns responses (like MGET but optimized)
    /// Zero-allocation ValueTask handling with ArrayPool for temporary arrays
    /// </summary>
    public async ValueTask<RespireValue[]> GetBatchAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        var keyList = keys as IList<string> ?? keys.ToList();
        var count = keyList.Count;
        
        if (count == 0) return Array.Empty<RespireValue>();
        
        // Use ArrayPool for temporary task storage to reduce allocations
        var tasks = ArrayPool<ValueTask<RespireValue>>.Shared.Rent(count);
        
        try
        {
            // Create GET commands using pooled struct commands
            for (var i = 0; i < count; i++)
            {
                var command = new PooledCommands.GetCommand(keyList[i], cancellationToken);
                tasks[i] = _commandQueue.QueueCommandWithResponseAsync(command.ExecuteAsync, cancellationToken);
            }
            
            // Optimized ValueTask processing without boxing - await each directly
            var results = new RespireValue[count];
            for (var i = 0; i < count; i++)
            {
                results[i] = await tasks[i].ConfigureAwait(false);
            }
            
            return results;
        }
        finally
        {
            ArrayPool<ValueTask<RespireValue>>.Shared.Return(tasks, clearArray: true);
        }
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
        return _commandQueue.QueueCommandAsync(
            writer => writer.WritePreCompiledAsync(preCompiledCommand, cancellationToken), cancellationToken);
    }
    
    // Pipeline operations (inspired by FastRespClient but async)
    
    /// <summary>
    /// Creates an async pipeline for batching multiple operations with optimal performance
    /// </summary>
    public RespirePipeline CreatePipeline()
    {
        ThrowIfDisposed();
        return new RespirePipeline(this, _commandQueue);
    }
    
    /// <summary>
    /// Executes a pipeline configuration asynchronously
    /// </summary>
    public async ValueTask ExecutePipelineAsync(
        Func<RespirePipeline, Task> configure,
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
    public QueueStats GetQueueStats()
    {
        // Return default stats since interface doesn't expose stats
        return new QueueStats();
    }
    
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
    
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RespireClient));
    }
    
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    
    /// <summary>
    /// Clears the pre-encoded command cache to free memory
    /// </summary>
    public void ClearCommandCache()
    {
        _preEncodedGetCommands.Clear();
        _logger?.LogDebug("Cleared pre-encoded command cache");
    }
    
    /// <summary>
    /// Gets the current size of the pre-encoded command cache
    /// </summary>
    public int GetCommandCacheSize() => _preEncodedGetCommands.Count;
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            // Clear pre-encoded command cache
            _preEncodedGetCommands.Clear();
            
            // Dispose command queue
            await _commandQueue.DisposeAsync().ConfigureAwait(false);
            
            // Then dispose multiplexer
            await _multiplexer.DisposeAsync().ConfigureAwait(false);
            
            _logger?.LogInformation("Disposed RespireClient for {Host}:{Port}", Host, Port);
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