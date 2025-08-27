using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Keva.Protocol;

namespace Keva.Infrastructure;

/// <summary>
/// High-performance connection multiplexer that manages multiple pipeline connections
/// Provides connection pooling, load balancing, and automatic failover
/// </summary>
public sealed class KevaConnectionMultiplexer : IAsyncDisposable
{
    private readonly PipelineConnection[] _connections;
    private readonly SemaphoreSlim[] _connectionSemaphores;
    private readonly ConcurrentQueue<int> _availableConnections;
    private readonly ILogger? _logger;
    private readonly Timer _healthCheckTimer;
    private readonly CancellationTokenSource _cancellationTokenSource;
    
    private volatile bool _disposed;
    private int _roundRobinIndex = -1;
    
    public string Host { get; }
    public int Port { get; }
    public int ConnectionCount => _connections.Length;
    public bool IsConnected => !_disposed && _connections.Any(c => c.IsConnected);
    
    private KevaConnectionMultiplexer(
        PipelineConnection[] connections, 
        string host, 
        int port, 
        ILogger? logger)
    {
        _connections = connections;
        _connectionSemaphores = new SemaphoreSlim[connections.Length];
        _availableConnections = new ConcurrentQueue<int>();
        Host = host;
        Port = port;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();
        
        // Initialize semaphores and available connections
        for (int i = 0; i < connections.Length; i++)
        {
            _connectionSemaphores[i] = new SemaphoreSlim(1, 1);
            _availableConnections.Enqueue(i);
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
    public static async Task<KevaConnectionMultiplexer> CreateAsync(
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
        
        var multiplexer = new KevaConnectionMultiplexer(connections, host, port, logger);
        
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
    public async ValueTask<ConnectionHandle> GetConnectionHandleAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Try to get a free connection first
        if (_availableConnections.TryDequeue(out var freeIndex) && _connections[freeIndex].IsConnected)
        {
            await _connectionSemaphores[freeIndex].WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ConnectionHandle(this, freeIndex, _connections[freeIndex]);
        }
        
        // Fallback to round-robin if no free connections
        var (index, connection) = GetConnection();
        await _connectionSemaphores[index].WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionHandle(this, index, connection);
    }
    
    /// <summary>
    /// Creates a command writer for the optimal connection
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Pipeline command writer</returns>
    public async ValueTask<PipelineCommandWriter> GetCommandWriterAsync(CancellationToken cancellationToken = default)
    {
        var (_, connection) = GetConnection();
        return new PipelineCommandWriter(connection);
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
        using var writer = new PipelineCommandWriter(handle.Connection);
        await commandAction(writer).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Executes a command and reads the response
    /// </summary>
    /// <param name="commandAction">Action that writes the command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response data</returns>
    public async ValueTask<KevaValue> ExecuteCommandWithResponseAsync(
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken = default)
    {
        using var handle = await GetConnectionHandleAsync(cancellationToken).ConfigureAwait(false);
        using var writer = new PipelineCommandWriter(handle.Connection);
        
        // Write command
        await commandAction(writer).ConfigureAwait(false);
        
        // Read response
        var readResult = await handle.Connection.ReadAsync(cancellationToken).ConfigureAwait(false);
        
        // Parse response using ref struct outside of async context
        return ParseRespResponse(readResult.Buffer, handle.Connection);
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
            var connection = _connections[connectionIndex % _connections.Length];
            connectionIndex++;
            
            tasks.Add(ExecuteOnConnectionAsync(connection, command, cancellationToken));
        }
        
        foreach (var task in tasks)
        {
            await task.ConfigureAwait(false);
        }
    }
    
    private async ValueTask ExecuteOnConnectionAsync(
        PipelineConnection connection, 
        Func<PipelineCommandWriter, ValueTask> commandAction,
        CancellationToken cancellationToken)
    {
        using var writer = new PipelineCommandWriter(connection);
        await commandAction(writer).ConfigureAwait(false);
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
            AvailableConnections = _availableConnections.Count,
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
            
            for (int i = 0; i < _connections.Length; i++)
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
            using var writer = new PipelineCommandWriter(connection);
            await writer.WritePingAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            
            // Read the response to complete the ping
            var readResult = await connection.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Health check failed for connection {Index} to {Host}:{Port}", index, Host, Port);
        }
    }
    
    private void ReleaseConnection(int index)
    {
        _connectionSemaphores[index].Release();
        _availableConnections.Enqueue(index);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static KevaValue ParseRespResponse(ReadOnlySequence<byte> buffer, PipelineConnection connection)
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
        if (_disposed) throw new ObjectDisposedException(nameof(KevaConnectionMultiplexer));
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            _cancellationTokenSource.Cancel();
            _healthCheckTimer.Dispose();
            
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
    
    /// <summary>
    /// Disposable connection handle that automatically releases the connection
    /// </summary>
    public readonly struct ConnectionHandle : IDisposable
    {
        private readonly KevaConnectionMultiplexer _multiplexer;
        private readonly int _index;
        
        public PipelineConnection Connection { get; }
        
        internal ConnectionHandle(KevaConnectionMultiplexer multiplexer, int index, PipelineConnection connection)
        {
            _multiplexer = multiplexer;
            _index = index;
            Connection = connection;
        }
        
        public void Dispose()
        {
            _multiplexer.ReleaseConnection(_index);
        }
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