using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// High-performance connection multiplexer that manages multiple pipeline connections
/// Provides connection pooling, load balancing, and automatic failover
/// </summary>
public sealed class RespireConnectionMultiplexer : IAsyncDisposable
{
    private readonly PipelineConnection[] _connections;
    private readonly SemaphoreSlim[] _connectionSemaphores;
    private readonly ObjectPool<PipelineCommandWriter>[] _writerPools;
    private readonly ILogger? _logger;
    private readonly Timer _healthCheckTimer;
    private readonly CancellationTokenSource _cancellationTokenSource;
    
    
    private volatile bool _disposed;
    private int _roundRobinIndex = -1;
    private int _roundRobinCounter = -1;
    private readonly int _connectionCount;
    
    public string Host { get; }
    public int Port { get; }
    public int ConnectionCount => _connections.Length;
    public bool IsConnected => !_disposed && _connections.Any(c => c.IsConnected);
    
    private RespireConnectionMultiplexer(
        PipelineConnection[] connections, 
        string host, 
        int port, 
        ILogger? logger)
    {
        _connections = connections;
        _connectionSemaphores = new SemaphoreSlim[connections.Length];
        _writerPools = new ObjectPool<PipelineCommandWriter>[connections.Length];
        Host = host;
        Port = port;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();
        _connectionCount = connections.Length;
        
        // Initialize semaphores and writer pools
        for (var i = 0; i < connections.Length; i++)
        {
            _connectionSemaphores[i] = new SemaphoreSlim(1, 1);
            
            // Create an ObjectPool for each connection with a custom policy
            var policy = new PipelineCommandWriterPooledObjectPolicy(connections[i]);
            _writerPools[i] = new DefaultObjectPool<PipelineCommandWriter>(policy, maximumRetained: 16);
        }
        
        // Start health check timer (every 30 seconds)
        _healthCheckTimer = new Timer(PerformHealthCheck, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
    
    /// <summary>
    /// Creates a connection multiplexer with the specified number of connections
    /// </summary>
    /// <param name="host">Redis server host</param>
    /// <param name="port">Redis server port</param>
    /// <param name="connectionCount">Number of connections to create (defaults to CPU count)</param>
    /// <param name="connectTimeout">Connection timeout</param>
    /// <param name="logger">Optional logger</param>
    /// <returns>Connected multiplexer</returns>
    public static async Task<RespireConnectionMultiplexer> CreateAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        TimeSpan connectTimeout = default,
        ILogger? logger = null)
    {
        if (connectionCount <= 0)
            connectionCount = Environment.ProcessorCount;
        
        if (connectTimeout == default)
            connectTimeout = TimeSpan.FromSeconds(30);
        
        var connections = await PipelineConnectionFactory.CreateConnectionPoolAsync(
            host, port, connectionCount, logger).ConfigureAwait(false);
        
        var multiplexer = new RespireConnectionMultiplexer(connections, host, port, logger);
        
        logger?.LogInformation(
            "Created connection multiplexer with {ConnectionCount} connections to {Host}:{Port}", 
            connectionCount, host, port);
        
        return multiplexer;
    }
    
    /// <summary>
    /// Gets a connection for executing a single command
    /// Uses round-robin load balancing for optimal distribution
    /// </summary>
    /// <returns>Connection index and the connection</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int Index, PipelineConnection Connection) GetConnection()
    {
        ThrowIfDisposed();
        
        // Round-robin selection for load balancing
        var index = Interlocked.Increment(ref _roundRobinIndex) % _connections.Length;
        return (index, _connections[index]);
    }
    
    /// <summary>
    /// Gets an available connection with automatic semaphore management
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Disposable connection handle</returns>
    public ValueTask<ConnectionHandle> GetConnectionHandleAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Try to find an available connection using round-robin
        // Try all connections before waiting
        for (int i = 0; i < _connectionCount; i++)
        {
            var index = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)_connectionCount);
            
            // Try to acquire semaphore synchronously
            if (_connectionSemaphores[index].Wait(0))
            {
                // Always create a new handle - pooling them causes double-dispose issues
                return new ValueTask<ConnectionHandle>(new ConnectionHandle(this, index, _connections[index]));
            }
        }
        
        // All connections busy, need to wait for one
        // Select next connection in round-robin order and wait
        var waitIndex = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)_connectionCount);
        return GetConnectionHandleAsyncSlow(waitIndex, cancellationToken);
    }
    
    private async ValueTask<ConnectionHandle> GetConnectionHandleAsyncSlow(int index, CancellationToken cancellationToken)
    {
        await _connectionSemaphores[index].WaitAsync(cancellationToken).ConfigureAwait(false);
        
        // Always create a new handle - pooling them causes double-dispose issues
        return new ConnectionHandle(this, index, _connections[index]);
    }
    
    /// <summary>
    /// Gets a connection directly with zero allocations for high-performance scenarios
    /// Returns connection index and connection reference as a tuple
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<(int Index, PipelineConnection Connection)> GetConnectionDirectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Try round-robin with immediate acquisition
        for (var i = 0; i < _connectionCount; i++)
        {
            var index = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)_connectionCount);
            
            if (_connectionSemaphores[index].Wait(0))
            {
                // Semaphore acquired synchronously - return completed ValueTask with no allocations
                return new ValueTask<(int, PipelineConnection)>((index, _connections[index]));
            }
        }
        
        // Slow path: need to wait for a connection
        return GetConnectionDirectAsyncSlow(cancellationToken);
    }
    
    private async ValueTask<(int Index, PipelineConnection Connection)> GetConnectionDirectAsyncSlow(CancellationToken cancellationToken)
    {
        // Fall back to waiting on next connection in sequence
        var index = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)_connectionCount);
        await _connectionSemaphores[index].WaitAsync(cancellationToken).ConfigureAwait(false);
        return (index, _connections[index]);
    }
    
    /// <summary>
    /// Gets the writer pool for a specific connection
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ObjectPool<PipelineCommandWriter> GetWriterPool(int connectionIndex)
    {
        ThrowIfDisposed();
        if (connectionIndex < 0 || connectionIndex >= _writerPools.Length)
            throw new ArgumentOutOfRangeException(nameof(connectionIndex));
        
        return _writerPools[connectionIndex];
    }
    
    /// <summary>
    /// Releases a connection acquired via GetConnectionDirectAsync
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseConnection(int connectionIndex)
    {
        if (connectionIndex >= 0 && connectionIndex < _connectionSemaphores.Length)
        {
            _connectionSemaphores[connectionIndex].Release();
        }
    }
    
    /// <summary>
    /// Gets a connection lease for zero-allocation command execution
    /// Optimized for synchronous completion when connections are available
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ConnectionLease> GetConnectionLeaseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Try round-robin with immediate acquisition
        for (var i = 0; i < _connectionCount; i++)
        {
            var index = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)_connectionCount);
            
            if (_connectionSemaphores[index].Wait(0))
            {
                // Semaphore acquired synchronously - return completed ValueTask
                return new ValueTask<ConnectionLease>(new ConnectionLease(this, index, _connections[index]));
            }
        }
        
        // Slow path: need to wait for a connection
        return GetConnectionLeaseAsyncSlow(cancellationToken);
    }
    
    private async ValueTask<ConnectionLease> GetConnectionLeaseAsyncSlow(CancellationToken cancellationToken)
    {
        // Fall back to waiting on next connection in sequence
        var index = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)_connectionCount);
        await _connectionSemaphores[index].WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionLease(this, index, _connections[index]);
    }
    
    /// <summary>
    /// Rents a writer for a specific connection
    /// Writers are pooled per connection to reduce allocations
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PipelineCommandWriter RentWriter(int connectionIndex, PipelineConnection connection)
    {
        var pool = _writerPools[connectionIndex];
        var writer = pool.Get();
        
        // Ensure the writer is using the correct connection
        writer.UpdateConnection(connection);
        return writer;
    }
    
    /// <summary>
    /// Returns a writer to the pool for reuse
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnWriter(int connectionIndex, PipelineCommandWriter writer)
    {
        var pool = _writerPools[connectionIndex];
        pool.Return(writer);
    }
    
    /// <summary>
    /// Executes a command on the optimal connection
    /// </summary>
    /// <param name="commandAction">Action that writes the command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask ExecuteCommandAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction, 
        CancellationToken cancellationToken = default)
    {
        using var handle = await GetConnectionHandleAsync(cancellationToken).ConfigureAwait(false);
        
        // Rent a writer from the pool
        var writer = RentWriter(handle.Index, handle.Connection);
        
        try
        {
            await commandAction(writer).ConfigureAwait(false);
        }
        finally
        {
            ReturnWriter(handle.Index, writer);
        }
    }
    
    /// <summary>
    /// Executes a command and reads the response
    /// </summary>
    /// <param name="commandAction">Action that writes the command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response data</returns>
    public async ValueTask<RespireValue> ExecuteCommandWithResponseAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default)
    {
        // Get connection directly with zero allocations
        var (connectionIndex, connection) = await GetConnectionDirectAsync(cancellationToken).ConfigureAwait(false);
        
        // Rent a writer from the pool
        var writer = RentWriter(connectionIndex, connection);
        
        try
        {
            // Write command
            await commandAction(writer).ConfigureAwait(false);
            
            // Read response - keep reading until we have a complete response
            ReadResult readResult;
            while (true)
            {
                readResult = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
                
                // Parse response using ref struct outside of async context
                var reader = new RespPipelineReader(readResult.Buffer);
                
                if (reader.TryReadValue(out var value))
                {
                    connection.AdvanceReader(reader.Consumed, reader.Examined);
                    return value;
                }
                
                // Check if connection was closed
                if (readResult.IsCompleted)
                {
                    connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
                    throw new InvalidOperationException("Connection closed before complete response received");
                }
                
                // Tell the pipeline that we've examined the data but haven't consumed it
                // Consumed = Start (nothing consumed), Examined = End (everything examined)
                connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
            }
        }
        finally
        {
            ReturnWriter(connectionIndex, writer);
            // Release the semaphore directly
            _connectionSemaphores[connectionIndex].Release();
        }
    }
    
    /// <summary>
    /// Executes multiple commands in parallel across all connections
    /// </summary>
    /// <param name="commands">Commands to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async ValueTask ExecuteParallelCommandsAsync(
        IEnumerable<Func<PipelineCommandWriter, ValueTask>> commands,
        CancellationToken cancellationToken = default)
    {
        var commandList = commands.ToList();
        var tasks = new List<ValueTask>(commandList.Count);
        var connectionIndex = 0;
        
        foreach (var command in commandList)
        {
            var index = connectionIndex % _connections.Length;
            var connection = _connections[index];
            connectionIndex++;
            
            tasks.Add(ExecuteOnConnectionAsync(index, connection, command, cancellationToken));
        }
        
        foreach (var task in tasks)
        {
            await task.ConfigureAwait(false);
        }
    }
    
    private async ValueTask ExecuteOnConnectionAsync(
        int connectionIndex,
        PipelineConnection connection, 
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken)
    {
        var writer = RentWriter(connectionIndex, connection);
        try
        {
            await commandAction(writer).ConfigureAwait(false);
        }
        finally
        {
            ReturnWriter(connectionIndex, writer);
        }
    }
    
    /// <summary>
    /// Gets connection statistics for monitoring
    /// </summary>
    /// <returns>Connection statistics</returns>
    public ConnectionStats GetStats()
    {
        var connectedCount = _connections.Count(c => c.IsConnected);
        var totalConnections = _connections.Length;
        
        return new ConnectionStats
        {
            TotalConnections = totalConnections,
            ConnectedConnections = connectedCount,
            AvailableConnections = 0, // Not tracking anymore, using semaphores
            Host = Host,
            Port = Port
        };
    }
    
    private async void PerformHealthCheck(object? state)
    {
        if (_disposed) return;
        
        try
        {
            var tasks = new List<ValueTask>();
            
            for (var i = 0; i < _connections.Length; i++)
            {
                var connection = _connections[i];
                if (!connection.IsConnected)
                {
                    _logger?.LogWarning("Connection {Index} to {Host}:{Port} is not connected", i, Host, Port);
                    continue;
                }
                
                // Send PING to verify connection health
                tasks.Add(SendHealthCheckPing(connection, i));
            }
            
            foreach (var task in tasks)
        {
            await task.ConfigureAwait(false);
        }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during health check for {Host}:{Port}", Host, Port);
        }
    }
    
    private async ValueTask SendHealthCheckPing(PipelineConnection connection, int index)
    {
        try
        {
            var writer = RentWriter(index, connection);
            try
            {
                await writer.WritePingAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
                
                // Read the response to complete the ping
                var readResult = await connection.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
                connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
            }
            finally
            {
                ReturnWriter(index, writer);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check failed for connection {Index} to {Host}:{Port}", index, Host, Port);
        }
    }
    
    /// <summary>
    /// Returns a connection handle (releases the semaphore)
    /// </summary>
    internal void ReturnConnectionHandle(int index, ConnectionHandle handle)
    {
        // Just release the semaphore - we don't pool handles anymore since they're structs
        _connectionSemaphores[index].Release();
    }
    
    /// <summary>
    /// Releases the semaphore for a connection directly (zero-allocation path)
    /// </summary>
    internal void ReleaseSemaphore(int connectionIndex)
    {
        _connectionSemaphores[connectionIndex].Release();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RespireValue ParseRespResponse(ReadOnlySequence<byte> buffer, PipelineConnection connection)
    {
        var reader = new RespPipelineReader(buffer);
        
        if (reader.TryReadValue(out var value))
        {
            connection.AdvanceReader(reader.Consumed, reader.Examined);
            return value;
        }
        
        connection.AdvanceReader(buffer.Start, buffer.End);
        return default;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RespireConnectionMultiplexer));
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            _cancellationTokenSource.Cancel();
            _healthCheckTimer.Dispose();
            
            // Writer pools will be GC'd with their contents
            // ObjectPool doesn't provide a way to drain all items
            
            // Dispose all connections
            foreach (var connection in _connections)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            
            // Dispose semaphores
            foreach (var semaphore in _connectionSemaphores)
            {
                semaphore.Dispose();
            }
            
            _logger?.LogInformation("Disposed connection multiplexer for {Host}:{Port}", Host, Port);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during multiplexer disposal for {Host}:{Port}", Host, Port);
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }
}

/// <summary>
/// Disposable connection handle that automatically releases the connection (for compatibility)
/// </summary>
public readonly struct ConnectionHandle : IDisposable
{
    private readonly RespireConnectionMultiplexer _multiplexer;
    private readonly int _index;
    
    public PipelineConnection Connection { get; }
    public int Index => _index;
    
    internal ConnectionHandle(RespireConnectionMultiplexer multiplexer, int index, PipelineConnection connection)
    {
        _multiplexer = multiplexer;
        _index = index;
        Connection = connection;
    }
    
    public void Dispose()
    {
        _multiplexer.ReturnConnectionHandle(_index, this);
    }
}

/// <summary>
/// Connection statistics for monitoring
/// </summary>
public readonly struct ConnectionStats
{
    public int TotalConnections { get; init; }
    public int ConnectedConnections { get; init; }
    public int AvailableConnections { get; init; }
    public string Host { get; init; }
    public int Port { get; init; }
    
    public double ConnectionHealthPercentage => TotalConnections > 0 
        ? (double)ConnectedConnections / TotalConnections * 100 
        : 0;
}