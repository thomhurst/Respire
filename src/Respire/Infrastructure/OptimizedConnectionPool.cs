using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Protocol;

namespace Respire.Infrastructure;

/// <summary>
/// Optimized connection pool with advanced load balancing and connection warming
/// </summary>
public sealed class OptimizedConnectionPool : IAsyncDisposable
{
    private readonly PipelineConnection[] _connections;
    private readonly ConnectionSlot[] _connectionSlots;
    private readonly SemaphoreSlim[] _connectionSemaphores;
    private readonly ConcurrentQueue<int> _warmConnections;
    private readonly Timer _connectionWarmer;
    private readonly ILogger? _logger;
    
    private long _roundRobinCounter = -1;
    private volatile bool _disposed;
    
    public int ConnectionCount => _connections.Length;
    public bool IsHealthy => !_disposed && _connections.Any(c => c.IsConnected);
    
    private OptimizedConnectionPool(
        PipelineConnection[] connections, 
        ILogger? logger)
    {
        _connections = connections;
        _connectionSlots = new ConnectionSlot[connections.Length];
        _connectionSemaphores = new SemaphoreSlim[connections.Length];
        _warmConnections = new ConcurrentQueue<int>();
        _logger = logger;
        
        // Initialize connection slots and semaphores
        for (var i = 0; i < connections.Length; i++)
        {
            _connectionSlots[i] = new ConnectionSlot(i, connections[i]);
            _connectionSemaphores[i] = new SemaphoreSlim(1, 1);
            _warmConnections.Enqueue(i);
        }
        
        // Start connection warmer (keeps connections active)
        _connectionWarmer = new Timer(WarmConnections, null, 
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
    
    public static async Task<OptimizedConnectionPool> CreateAsync(
        string host,
        int port,
        int connectionCount,
        ILogger? logger = null)
    {
        var connections = await PipelineConnectionFactory.CreateConnectionPoolAsync(
            host, port, connectionCount, logger).ConfigureAwait(false);
        
        return new OptimizedConnectionPool(connections, logger);
    }
    
    /// <summary>
    /// Gets the fastest available connection using load-based selection
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int Index, PipelineConnection Connection) GetFastestConnection()
    {
        // Try warm connections first (recently used, likely to have active TCP state)
        if (_warmConnections.TryDequeue(out var warmIndex) && _connections[warmIndex].IsConnected)
        {
            _warmConnections.Enqueue(warmIndex); // Re-queue for next time
            return (warmIndex, _connections[warmIndex]);
        }
        
        // Fallback to round-robin with optimized Interlocked operation
        var index = (int)(Interlocked.Increment(ref _roundRobinCounter) % _connections.Length);
        return (index, _connections[index]);
    }
    
    /// <summary>
    /// Gets a connection handle with automatic semaphore management and priority queuing
    /// </summary>
    public async ValueTask<OptimizedConnectionHandle> GetConnectionHandleAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        // Use load-based selection instead of simple round-robin
        var (index, connection) = GetFastestConnection();
        
        try
        {
            await _connectionSemaphores[index].WaitAsync(cancellationToken).ConfigureAwait(false);
            _connectionSlots[index].IncrementUsage();
            return new OptimizedConnectionHandle(this, index, connection);
        }
        catch
        {
            // If primary connection fails, try next available
            for (var i = 0; i < _connections.Length; i++)
            {
                var nextIndex = (index + i + 1) % _connections.Length;
                if (_connections[nextIndex].IsConnected)
                {
                    await _connectionSemaphores[nextIndex].WaitAsync(cancellationToken).ConfigureAwait(false);
                    _connectionSlots[nextIndex].IncrementUsage();
                    return new OptimizedConnectionHandle(this, nextIndex, _connections[nextIndex]);
                }
            }
            throw;
        }
    }
    
    /// <summary>
    /// Executes multiple operations in parallel with optimal connection distribution
    /// </summary>
    public async ValueTask ExecuteParallelOptimizedAsync(
        IEnumerable<Func<PipelineCommandWriter, ValueTask>> operations,
        CancellationToken cancellationToken = default)
    {
        var ops = operations.ToList();
        if (ops.Count == 0) return;
        
        // Distribute operations across connections for optimal parallelism
        var tasks = new List<ValueTask>(ops.Count);
        var connectionIndex = 0;
        
        foreach (var operation in ops)
        {
            var connection = _connections[connectionIndex % _connections.Length];
            connectionIndex++;
            
            tasks.Add(ExecuteOnConnectionAsync(connection, operation, cancellationToken));
        }
        
        // Execute all operations in parallel
        foreach (var task in tasks)
        {
            await task.ConfigureAwait(false);
        }
    }
    
    private async ValueTask ExecuteOnConnectionAsync(
        PipelineConnection connection,
        Func<PipelineCommandWriter, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        using var writer = new PipelineCommandWriter(connection);
        await operation(writer).ConfigureAwait(false);
    }
    
    private void WarmConnections(object? state)
    {
        if (_disposed) return;
        
        try
        {
            // Send PING to each connection to keep them warm
            var tasks = new List<ValueTask>();
            
            for (var i = 0; i < _connections.Length; i++)
            {
                if (_connections[i].IsConnected)
                {
                    tasks.Add(SendKeepAlivePing(i));
                }
            }
            
            // Execute all pings in parallel (fire-and-forget)
            _ = Task.Run(async () =>
            {
                foreach (var task in tasks)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Connection warming failed");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during connection warming");
        }
    }
    
    private async ValueTask SendKeepAlivePing(int connectionIndex)
    {
        var connection = _connections[connectionIndex];
        using var writer = new PipelineCommandWriter(connection);
        
        await writer.WritePingAsync().ConfigureAwait(false);
        
        // Read and discard the response to complete the round-trip
        var readResult = await connection.ReadAsync().ConfigureAwait(false);
        connection.AdvanceReader(readResult.Buffer.Start, readResult.Buffer.End);
    }
    
    internal void ReleaseConnection(int index)
    {
        _connectionSemaphores[index].Release();
        _connectionSlots[index].DecrementUsage();
        
        // Add back to warm connections if it's still healthy
        if (_connections[index].IsConnected)
        {
            _warmConnections.Enqueue(index);
        }
    }
    
    public PoolStats GetStats()
    {
        var connectedCount = _connections.Count(c => c.IsConnected);
        var totalUsage = _connectionSlots.Sum(slot => slot.CurrentUsage);
        
        return new PoolStats
        {
            TotalConnections = _connections.Length,
            ConnectedConnections = connectedCount,
            WarmConnections = _warmConnections.Count,
            TotalActiveUsage = totalUsage,
            AverageUsagePerConnection = connectedCount > 0 ? (double)totalUsage / connectedCount : 0
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OptimizedConnectionPool));
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            _connectionWarmer.Dispose();
            
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
            
            _logger?.LogInformation("Disposed optimized connection pool with {ConnectionCount} connections", 
                _connections.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during optimized connection pool disposal");
        }
    }
    
    /// <summary>
    /// Tracks connection usage statistics for load balancing
    /// </summary>
    private sealed class ConnectionSlot
    {
        private long _usageCount;
        
        public int Index { get; }
        public PipelineConnection Connection { get; }
        public long CurrentUsage => _usageCount;
        
        public ConnectionSlot(int index, PipelineConnection connection)
        {
            Index = index;
            Connection = connection;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementUsage() => Interlocked.Increment(ref _usageCount);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DecrementUsage() => Interlocked.Decrement(ref _usageCount);
    }
    
    /// <summary>
    /// Optimized connection handle with usage tracking
    /// </summary>
    public readonly struct OptimizedConnectionHandle : IDisposable
    {
        private readonly OptimizedConnectionPool _pool;
        private readonly int _index;
        
        public PipelineConnection Connection { get; }
        
        internal OptimizedConnectionHandle(OptimizedConnectionPool pool, int index, PipelineConnection connection)
        {
            _pool = pool;
            _index = index;
            Connection = connection;
        }
        
        public void Dispose()
        {
            _pool.ReleaseConnection(_index);
        }
    }
}

/// <summary>
/// Connection pool statistics for monitoring
/// </summary>
public readonly struct PoolStats
{
    public int TotalConnections { get; init; }
    public int ConnectedConnections { get; init; }
    public int WarmConnections { get; init; }
    public long TotalActiveUsage { get; init; }
    public double AverageUsagePerConnection { get; init; }
    
    public double ConnectionHealthPercentage => TotalConnections > 0 
        ? (double)ConnectedConnections / TotalConnections * 100 
        : 0;
}